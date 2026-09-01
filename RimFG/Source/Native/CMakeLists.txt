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

# True two-frame temporal interpolation. The earlier safety build only warped
# CurrentFrame, so independently moving pawns remained frozen inside each FG
# batch and jumped on the next real frame (slideshow effect).
string(REPLACE
"    constexpr int kOutputSets = 2;"
"    constexpr int kOutputSets = 3;"
RIMFG_UAV_SAFE_SOURCE "${RIMFG_UAV_SAFE_SOURCE}")

string(REPLACE
"    ComPtr<ID3D11Texture2D> g_current;\n    ComPtr<ID3D11ShaderResourceView> g_currentSrv;"
"    ComPtr<ID3D11Texture2D> g_previous;\n    ComPtr<ID3D11ShaderResourceView> g_previousSrv;\n    ComPtr<ID3D11Texture2D> g_current;\n    ComPtr<ID3D11ShaderResourceView> g_currentSrv;"
RIMFG_UAV_SAFE_SOURCE "${RIMFG_UAV_SAFE_SOURCE}")

string(REPLACE
"Texture2D<float4> CurrentFrame : register(t0);"
"Texture2D<float4> PreviousFrame : register(t0);\nTexture2D<float4> CurrentFrame : register(t1);"
RIMFG_UAV_SAFE_SOURCE "${RIMFG_UAV_SAFE_SOURCE}")

string(REPLACE
"    float f = saturate(BackwardFraction);\n    float safeZoom = max(ZoomScale, 0.001);\n    float z = pow(safeZoom, f);\n    float2 src = center + (p - center) / z + ImageShiftPixels * f;\n    OutputFrame[id.xy] = CurrentFrame.Load(int3(ClampCoord(src), 0));"
"    float f = saturate(BackwardFraction);\n    float t = 1.0 - f;\n    float safeZoom = max(ZoomScale, 0.001);\n    float currentZ = pow(safeZoom, f);\n    float previousZ = pow(safeZoom, t);\n    float2 currentSrc = center + (p - center) / currentZ + ImageShiftPixels * f;\n    float2 previousSrc = center + (p - center) * previousZ - ImageShiftPixels * t;\n    float4 a = PreviousFrame.Load(int3(ClampCoord(previousSrc), 0));\n    float4 b = CurrentFrame.Load(int3(ClampCoord(currentSrc), 0));\n    OutputFrame[id.xy] = lerp(a, b, t);"
RIMFG_UAV_SAFE_SOURCE "${RIMFG_UAV_SAFE_SOURCE}")

string(REPLACE
"        g_currentSrv.Reset(); g_current.Reset(); g_generatedUav.Reset(); g_generated.Reset();"
"        g_previousSrv.Reset(); g_previous.Reset(); g_currentSrv.Reset(); g_current.Reset(); g_generatedUav.Reset(); g_generated.Reset();"
RIMFG_UAV_SAFE_SOURCE "${RIMFG_UAV_SAFE_SOURCE}")

string(REPLACE
"        if (FAILED(g_workerDevice->CreateTexture2D(&in, nullptr, &g_current)) || !g_current ||\n            FAILED(g_workerDevice->CreateShaderResourceView(g_current.Get(), nullptr, &g_currentSrv)) || !g_currentSrv)\n        {\n            g_nativeStage.store(static_cast<int>(NativeStage::ErrorWorkerResource), std::memory_order_release);\n            return false;\n        }"
"        if (FAILED(g_workerDevice->CreateTexture2D(&in, nullptr, &g_previous)) || !g_previous ||\n            FAILED(g_workerDevice->CreateShaderResourceView(g_previous.Get(), nullptr, &g_previousSrv)) || !g_previousSrv ||\n            FAILED(g_workerDevice->CreateTexture2D(&in, nullptr, &g_current)) || !g_current ||\n            FAILED(g_workerDevice->CreateShaderResourceView(g_current.Get(), nullptr, &g_currentSrv)) || !g_currentSrv)\n        {\n            g_nativeStage.store(static_cast<int>(NativeStage::ErrorWorkerResource), std::memory_order_release);\n            return false;\n        }"
RIMFG_UAV_SAFE_SOURCE "${RIMFG_UAV_SAFE_SOURCE}")

string(REPLACE
"        g_workerContext->CopyResource(g_current.Get(), opened->texture.Get());\n        g_currentMetadata = s.metadata; g_currentQpc = s.qpc; window = s.sourceWindow;"
"        if (g_havePreviousMetadata)\n            g_workerContext->CopyResource(g_previous.Get(), g_current.Get());\n        g_workerContext->CopyResource(g_current.Get(), opened->texture.Get());\n        g_currentMetadata = s.metadata; g_currentQpc = s.qpc; window = s.sourceWindow;"
RIMFG_UAV_SAFE_SOURCE "${RIMFG_UAV_SAFE_SOURCE}")

string(REPLACE
"        ID3D11ShaderResourceView* srv = g_currentSrv.Get(); ID3D11UnorderedAccessView* uav = g_generatedUav.Get(); ID3D11Buffer* cb = g_motionConstants.Get();\n        g_workerContext->CSSetShader(g_interpolateCs.Get(), nullptr, 0); g_workerContext->CSSetShaderResources(0, 1, &srv);"
"        ID3D11ShaderResourceView* srvs[2] = {g_previousSrv.Get(), g_currentSrv.Get()}; ID3D11UnorderedAccessView* uav = g_generatedUav.Get(); ID3D11Buffer* cb = g_motionConstants.Get();\n        g_workerContext->CSSetShader(g_interpolateCs.Get(), nullptr, 0); g_workerContext->CSSetShaderResources(0, 2, srvs);"
RIMFG_UAV_SAFE_SOURCE "${RIMFG_UAV_SAFE_SOURCE}")

string(REPLACE
"        ID3D11ShaderResourceView* ns = nullptr; ID3D11UnorderedAccessView* nu = nullptr; ID3D11Buffer* nb = nullptr;\n        g_workerContext->CSSetShaderResources(0, 1, &ns);"
"        ID3D11ShaderResourceView* ns[2] = {nullptr, nullptr}; ID3D11UnorderedAccessView* nu = nullptr; ID3D11Buffer* nb = nullptr;\n        g_workerContext->CSSetShaderResources(0, 2, ns);"
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

# Presenter runs on its own thread, so DO_NOT_WAIT only creates avoidable holes in
# the temporal sequence. Let DXGI/DWM back-pressure this thread instead of
# dropping output while Unity keeps running independently.
string(REPLACE
"            const HRESULT hr = g_outputSwapChain->Present(0, DXGI_PRESENT_DO_NOT_WAIT);"
"            const HRESULT hr = g_outputSwapChain->Present(0, 0);"
RIMFG_PRESENT_SOURCE "${RIMFG_PRESENT_SOURCE}")

# Do not abandon a partially displayed Prev->Current temporal segment whenever
# a newer real frame arrives. Finish the active batch, then pick up the latest
# available batch. This prevents repeatedly showing only the first few samples
# of each motion segment (the observed slideshow cadence).
string(REPLACE
"                if (latest.batchId != active.batchId)\n                {\n                    if (active.batchId != 0 && cursor < active.frameCount)\n                    {\n                        g_stalePredictionCount.fetch_add(static_cast<std::uint64_t>(active.frameCount - cursor), std::memory_order_relaxed);\n                        ReclaimRemaining(active, cursor);\n                    }\n                    active = latest;\n                    cursor = 0;\n                }"
"                if (active.batchId == 0)\n                {\n                    active = latest;\n                    cursor = 0;\n                }\n                else if (cursor >= active.frameCount && latest.batchId != active.batchId)\n                {\n                    active = latest;\n                    cursor = 0;\n                }"
RIMFG_PRESENT_SOURCE "${RIMFG_PRESENT_SOURCE}")

string(REPLACE
"                // A newer batch may have arrived while sleeping. Reclaim the old\n                // unconsumed handles before switching ownership to the new batch.\n                latest = ReadLatestBatch();\n                if (latest.batchId != 0 && latest.batchId != active.batchId)\n                {\n                    if (cursor < active.frameCount)\n                    {\n                        g_stalePredictionCount.fetch_add(static_cast<std::uint64_t>(active.frameCount - cursor), std::memory_order_relaxed);\n                        ReclaimRemaining(active, cursor);\n                    }\n                    active = latest;\n                    cursor = 0;\n                    continue;\n                }"
"                // Keep temporal ordering coherent: a newer batch is adopted only\n                // after the active Prev->Current sequence reaches its endpoint."
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
