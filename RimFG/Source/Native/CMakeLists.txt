cmake_minimum_required(VERSION 3.21)
project(RimFGNative LANGUAGES CXX)

if(NOT WIN32)
  message(FATAL_ERROR "RimFG.Native V0.1 targets Windows x64 only.")
endif()

include(FetchContent)

if(NOT DEFINED UNITY_PLUGIN_API_DIR OR UNITY_PLUGIN_API_DIR STREQUAL "")
  FetchContent_Declare(
    unity_native_rendering
    GIT_REPOSITORY https://github.com/Unity-Technologies/NativeRenderingPlugin.git
    GIT_TAG master
    GIT_SHALLOW TRUE
  )
  FetchContent_MakeAvailable(unity_native_rendering)
  set(UNITY_PLUGIN_API_DIR "${unity_native_rendering_SOURCE_DIR}/PluginSource/source/Unity")
endif()

set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
FetchContent_Declare(
  minhook
  GIT_REPOSITORY https://github.com/TsudaKageyu/minhook.git
  GIT_TAG v1.3.4
  GIT_SHALLOW TRUE
)
FetchContent_MakeAvailable(minhook)

file(READ "${CMAKE_CURRENT_SOURCE_DIR}/RimFGNativeUavSafe.cpp" RIMFG_UAV_SAFE_SOURCE)

# RimWorld mods load this native DLL through LoadLibrary, so UnityPluginLoad is
# not a reliable source of IUnityInterfaces. Bootstrap the real Unity D3D11
# device directly from the backbuffer captured by the Present hook.
string(REPLACE
"    bool RefreshUnityDevice()\n    {\n        if (g_unityDevice && g_unityContext && g_adapter)\n        {\n            g_d3d11Ready.store(true, std::memory_order_release);\n            return true;\n        }\n        if (!g_unityInterfaces) return false;\n        auto* d3d11 = g_unityInterfaces->Get<IUnityGraphicsD3D11>();\n        if (!d3d11) return false;\n        g_unityDevice = d3d11->GetDevice();\n        if (!g_unityDevice) return false;\n        g_unityDevice->GetImmediateContext(&g_unityContext);\n        ComPtr<IDXGIDevice> dxgi;\n        if (SUCCEEDED(g_unityDevice->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(dxgi.GetAddressOf()))) && dxgi)\n            dxgi->GetAdapter(&g_adapter);\n        const bool ok = g_unityContext && g_adapter;\n        g_d3d11Ready.store(ok, std::memory_order_release);\n        return ok;\n    }"
"    bool RefreshUnityDevice(ID3D11Texture2D* source = nullptr)\n    {\n        if (g_unityDevice && g_unityContext && g_adapter)\n        {\n            g_d3d11Ready.store(true, std::memory_order_release);\n            return true;\n        }\n\n        if (source)\n        {\n            ID3D11Device* device = nullptr;\n            source->GetDevice(&device);\n            if (device)\n            {\n                g_unityDevice = device;\n                g_unityDevice->GetImmediateContext(&g_unityContext);\n                ComPtr<IDXGIDevice> dxgi;\n                if (SUCCEEDED(g_unityDevice->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(dxgi.GetAddressOf()))) && dxgi)\n                    dxgi->GetAdapter(&g_adapter);\n                const bool ok = g_unityContext && g_adapter;\n                g_d3d11Ready.store(ok, std::memory_order_release);\n                if (ok) return true;\n            }\n        }\n\n        if (g_unityInterfaces)\n        {\n            auto* d3d11 = g_unityInterfaces->Get<IUnityGraphicsD3D11>();\n            if (d3d11)\n            {\n                g_unityDevice = d3d11->GetDevice();\n                if (g_unityDevice)\n                {\n                    g_unityDevice->GetImmediateContext(&g_unityContext);\n                    ComPtr<IDXGIDevice> dxgi;\n                    if (SUCCEEDED(g_unityDevice->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(dxgi.GetAddressOf()))) && dxgi)\n                        dxgi->GetAdapter(&g_adapter);\n                }\n            }\n        }\n        const bool ok = g_unityDevice && g_unityContext && g_adapter;\n        g_d3d11Ready.store(ok, std::memory_order_release);\n        return ok;\n    }"
RIMFG_UAV_SAFE_SOURCE "${RIMFG_UAV_SAFE_SOURCE}")

string(REPLACE
"        if (!source || !RefreshUnityDevice()) return false;"
"        if (!source || !RefreshUnityDevice(source)) return false;"
RIMFG_UAV_SAFE_SOURCE "${RIMFG_UAV_SAFE_SOURCE}")

# Bootstrap the device/adapter from the current backbuffer before waiting for
# the worker. The worker needs the adapter to create its independent device.
string(REPLACE
"        if (!g_enabled.load(std::memory_order_relaxed) || !source || !sourceWindow) return false;\n        if (!g_workerReady.load(std::memory_order_acquire)) return false;\n        if (!EnsureCaptureResources(source) || !g_unityContext) return false;"
"        if (!g_enabled.load(std::memory_order_relaxed) || !source || !sourceWindow) return false;\n        if (!RefreshUnityDevice(source))\n        {\n            g_nativeStage.store(static_cast<int>(NativeStage::ErrorNoDevice), std::memory_order_release);\n            return false;\n        }\n        if (!g_workerReady.load(std::memory_order_acquire))\n        {\n            g_workerCv.notify_one();\n            return false;\n        }\n        if (!EnsureCaptureResources(source) || !g_unityContext) return false;"
RIMFG_UAV_SAFE_SOURCE "${RIMFG_UAV_SAFE_SOURCE}")

string(REPLACE
"            if (!g_workerDevice)\n            {\n                if (g_adapter && CreateWorkerDevice()) g_workerReady.store(true, std::memory_order_release);\n                else { std::this_thread::sleep_for(std::chrono::milliseconds(10)); continue; }\n            }"
"            if (!g_workerReady.load(std::memory_order_acquire))\n            {\n                if (g_adapter && CreateWorkerDevice()) g_workerReady.store(true, std::memory_order_release);\n                else { std::this_thread::sleep_for(std::chrono::milliseconds(10)); continue; }\n            }"
RIMFG_UAV_SAFE_SOURCE "${RIMFG_UAV_SAFE_SOURCE}")

string(REPLACE
"            HWND window = nullptr;\n            if (!ConsumeSlot(index, window))\n            {\n                g_workerReady.store(false, std::memory_order_release);\n                std::this_thread::sleep_for(std::chrono::milliseconds(2));\n                continue;\n            }\n            if (g_havePreviousMetadata) PublishBatch(window);\n            else g_nativeStage.store(static_cast<int>(NativeStage::HistoryPrimed), std::memory_order_release);"
"            const bool hadHistoryBeforeConsume = g_havePreviousMetadata;\n            HWND window = nullptr;\n            if (!ConsumeSlot(index, window))\n            {\n                std::this_thread::sleep_for(std::chrono::milliseconds(2));\n                continue;\n            }\n            if (hadHistoryBeforeConsume) PublishBatch(window);\n            else g_nativeStage.store(static_cast<int>(NativeStage::HistoryPrimed), std::memory_order_release);"
RIMFG_UAV_SAFE_SOURCE "${RIMFG_UAV_SAFE_SOURCE}")

set(RIMFG_GATE_FIX_SOURCE "${CMAKE_CURRENT_BINARY_DIR}/RimFGNativeUavSafeGateFix.cpp")
file(WRITE "${RIMFG_GATE_FIX_SOURCE}" "${RIMFG_UAV_SAFE_SOURCE}")

file(READ "${CMAKE_CURRENT_SOURCE_DIR}/PresentHookOwnershipFix.cpp" RIMFG_PRESENT_SOURCE)

string(REPLACE
"        int QueryMonitorRefreshHz(HWND owner)\n        {\n            if (!owner) return 60;\n            MONITORINFOEXW mi{}; mi.cbSize = sizeof(mi);\n            if (!GetMonitorInfoW(MonitorFromWindow(owner, MONITOR_DEFAULTTONEAREST), &mi)) return 60;\n            DEVMODEW dm{}; dm.dmSize = sizeof(dm);\n            if (!EnumDisplaySettingsW(mi.szDevice, ENUM_CURRENT_SETTINGS, &dm)) return 60;\n            const int hz = static_cast<int>(dm.dmDisplayFrequency);\n            return hz >= 24 && hz <= 1000 ? hz : 60;\n        }"
"        int QueryMonitorRefreshHz(HWND owner)\n        {\n            if (!owner || !IsWindow(owner)) return 60;\n            RECT client{};\n            if (!GetClientRect(owner, &client)) return 60;\n            POINT center{(client.left + client.right) / 2, (client.top + client.bottom) / 2};\n            if (!ClientToScreen(owner, &center)) return 60;\n            const HMONITOR monitor = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);\n            MONITORINFOEXW mi{}; mi.cbSize = sizeof(mi);\n            if (!monitor || !GetMonitorInfoW(monitor, &mi)) return 60;\n            DEVMODEW dm{}; dm.dmSize = sizeof(dm);\n            if (!EnumDisplaySettingsW(mi.szDevice, ENUM_CURRENT_SETTINGS, &dm)) return 60;\n            const int hz = static_cast<int>(dm.dmDisplayFrequency);\n            return hz >= 24 && hz <= 1000 ? hz : 60;\n        }"
RIMFG_PRESENT_SOURCE "${RIMFG_PRESENT_SOURCE}")

string(REPLACE
"            if (g_presenterReady.load(std::memory_order_acquire) && g_outputSwapChain && g_outputDevice &&\n                g_compositionOwner == batch.sourceWindow && g_outputWidth == static_cast<UINT>(batch.width) &&\n                g_outputHeight == static_cast<UINT>(batch.height) && g_outputFormat == wanted) return true;"
"            if (g_presenterReady.load(std::memory_order_acquire) && g_outputSwapChain && g_outputDevice &&\n                g_compositionOwner == batch.sourceWindow && g_outputWidth == static_cast<UINT>(batch.width) &&\n                g_outputHeight == static_cast<UINT>(batch.height) && g_outputFormat == wanted)\n            {\n                g_monitorRefreshHz.store(QueryMonitorRefreshHz(batch.sourceWindow), std::memory_order_release);\n                return true;\n            }"
RIMFG_PRESENT_SOURCE "${RIMFG_PRESENT_SOURCE}")

set(RIMFG_WINDOWED_PRESENT_SOURCE "${CMAKE_CURRENT_BINARY_DIR}/PresentHookWindowedFix.cpp")
file(WRITE "${RIMFG_WINDOWED_PRESENT_SOURCE}" "${RIMFG_PRESENT_SOURCE}")

add_library(RimFG.Native SHARED
  "${RIMFG_GATE_FIX_SOURCE}"
  "${RIMFG_WINDOWED_PRESENT_SOURCE}"
  OpticalFlowBackend.cpp
  GpuBudget.cpp
)

target_compile_features(RimFG.Native PRIVATE cxx_std_17)
target_compile_definitions(RimFG.Native PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
target_include_directories(RimFG.Native PRIVATE "${UNITY_PLUGIN_API_DIR}" "${CMAKE_CURRENT_SOURCE_DIR}")
target_link_libraries(RimFG.Native PRIVATE d3d11 dxgi d3dcompiler dcomp minhook)

set_target_properties(RimFG.Native PROPERTIES
  OUTPUT_NAME "RimFG.Native"
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/../../Plugins/x86_64"
)

if(MSVC)
  target_compile_options(RimFG.Native PRIVATE /W4 /permissive- /EHsc)
endif()
