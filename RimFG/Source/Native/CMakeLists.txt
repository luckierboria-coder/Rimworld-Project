cmake_minimum_required(VERSION 3.21)
project(RimFGNative LANGUAGES CXX)

if(NOT WIN32)
  message(FATAL_ERROR "RimFG.Native V0.1 targets Windows x64 only.")
endif()

include(FetchContent)

# Prefer an explicitly supplied Unity PluginAPI directory (best for matching the
# exact Unity version used by the target RimWorld build). For CI/development,
# fall back to Unity's official NativeRenderingPlugin headers.
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

add_library(RimFG.Native SHARED
  RimFGNative.cpp
)

target_compile_features(RimFG.Native PRIVATE cxx_std_17)
target_compile_definitions(RimFG.Native PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_include_directories(RimFG.Native PRIVATE "${UNITY_PLUGIN_API_DIR}")
# d3dcompiler is used only when the native backend initializes its compute shader;
# it is not called on RimWorld's per-frame simulation path.
target_link_libraries(RimFG.Native PRIVATE d3d11 dxgi d3dcompiler)

set_target_properties(RimFG.Native PROPERTIES
  OUTPUT_NAME "RimFG.Native"
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/../../Plugins/x86_64"
)

if(MSVC)
  target_compile_options(RimFG.Native PRIVATE /W4 /permissive- /EHsc)
endif()
