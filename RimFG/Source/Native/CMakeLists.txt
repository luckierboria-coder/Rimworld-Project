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

# Keep the validated UAV-safe producer as the canonical source, and apply the
# narrow worker-state fix at configure time. This avoids duplicating the whole
# native producer while the state machine is still under active validation.
file(READ "${CMAKE_CURRENT_SOURCE_DIR}/RimFGNativeUavSafe.cpp" RIMFG_UAV_SAFE_SOURCE)

string(REPLACE
"            if (!g_workerDevice)\n            {\n                if (g_adapter && CreateWorkerDevice()) g_workerReady.store(true, std::memory_order_release);\n                else { std::this_thread::sleep_for(std::chrono::milliseconds(10)); continue; }\n            }"
"            if (!g_workerReady.load(std::memory_order_acquire))\n            {\n                if (g_adapter && CreateWorkerDevice()) g_workerReady.store(true, std::memory_order_release);\n                else { std::this_thread::sleep_for(std::chrono::milliseconds(10)); continue; }\n            }"
RIMFG_UAV_SAFE_SOURCE "${RIMFG_UAV_SAFE_SOURCE}")

string(REPLACE
"            HWND window = nullptr;\n            if (!ConsumeSlot(index, window))\n            {\n                g_workerReady.store(false, std::memory_order_release);\n                std::this_thread::sleep_for(std::chrono::milliseconds(2));\n                continue;\n            }\n            if (g_havePreviousMetadata) PublishBatch(window);\n            else g_nativeStage.store(static_cast<int>(NativeStage::HistoryPrimed), std::memory_order_release);"
"            const bool hadHistoryBeforeConsume = g_havePreviousMetadata;\n            HWND window = nullptr;\n            if (!ConsumeSlot(index, window))\n            {\n                // A keyed-mutex timeout or transient shared-resource miss must not\n                // permanently close the producer gate. Leave the slot READY and retry.\n                std::this_thread::sleep_for(std::chrono::milliseconds(2));\n                continue;\n            }\n            if (hadHistoryBeforeConsume) PublishBatch(window);\n            else g_nativeStage.store(static_cast<int>(NativeStage::HistoryPrimed), std::memory_order_release);"
RIMFG_UAV_SAFE_SOURCE "${RIMFG_UAV_SAFE_SOURCE}")

set(RIMFG_GATE_FIX_SOURCE "${CMAKE_CURRENT_BINARY_DIR}/RimFGNativeUavSafeGateFix.cpp")
file(WRITE "${RIMFG_GATE_FIX_SOURCE}" "${RIMFG_UAV_SAFE_SOURCE}")

# Windowed RimWorld may straddle displays. Resolve refresh rate from the
# physical monitor containing the client-area center, and refresh it whenever
# a batch arrives so moving/maximizing the window updates the pacing cap.
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
