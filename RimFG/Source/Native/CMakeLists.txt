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

add_library(RimFG.Native SHARED
  RimFGNative.cpp
  PresentHook.cpp
  OpticalFlowBackend.cpp
  GpuBudget.cpp
)

target_compile_features(RimFG.Native PRIVATE cxx_std_17)
target_compile_definitions(RimFG.Native PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
target_include_directories(RimFG.Native PRIVATE "${UNITY_PLUGIN_API_DIR}")
target_link_libraries(RimFG.Native PRIVATE d3d11 dxgi d3dcompiler dcomp minhook)

set_target_properties(RimFG.Native PROPERTIES
  OUTPUT_NAME "RimFG.Native"
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/../../Plugins/x86_64"
)

if(MSVC)
  target_compile_options(RimFG.Native PRIVATE /W4 /permissive- /EHsc)
endif()
