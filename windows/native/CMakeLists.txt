# Build driver for the native dependencies of the Windows client.
#
# Produces whisper.dll (+ its ggml DLLs) from the pinned whisper.cpp submodule.
# The mac app links whisper.cpp statically; we build shared because the C# side
# reaches it through P/Invoke.
#
# Configure and build via build-native.ps1 rather than invoking cmake directly.

cmake_minimum_required(VERSION 3.21)

project(osw_native LANGUAGES C CXX)

# ---------------------------------------------------------------------------
# The ONLY path here that reaches outside windows/.
# Overridable so the eventual standalone repo can point elsewhere without
# editing this file. See docs/windows-port.md, "Repository layout".
# ---------------------------------------------------------------------------
set(OSW_WHISPER_CPP_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../libwhisper/whisper.cpp"
    CACHE PATH "Path to the whisper.cpp submodule")

if(NOT EXISTS "${OSW_WHISPER_CPP_DIR}/CMakeLists.txt")
  message(FATAL_ERROR
    "whisper.cpp not found at ${OSW_WHISPER_CPP_DIR}\n"
    "Run: git submodule update --init --recursive")
endif()

# ---------------------------------------------------------------------------
# Build shape
# ---------------------------------------------------------------------------

# We P/Invoke whisper.dll, so shared rather than the mac build's static .a.
set(BUILD_SHARED_LIBS ON CACHE BOOL "" FORCE)

# GGML_NATIVE bakes in the *build machine's* instruction set. Fine for a local
# build, wrong for anything we ship: a binary built on an AVX-512 machine
# crashes on one without it. Pin an explicit baseline instead.
set(GGML_NATIVE  OFF CACHE BOOL "" FORCE)
set(GGML_AVX     ON  CACHE BOOL "" FORCE)
set(GGML_AVX2    ON  CACHE BOOL "" FORCE)
set(GGML_FMA     ON  CACHE BOOL "" FORCE)
set(GGML_F16C    ON  CACHE BOOL "" FORCE)
set(GGML_AVX512  OFF CACHE BOOL "" FORCE)

# AVX2 is a safe floor here: it predates every CPU that meets the Windows 11
# hardware requirements, and Rev. 3 puts the OS floor at Windows 11.

# ggml defaults OpenMP ON, which drags in the MSVC OpenMP redistributable
# (vcomp140.dll) as a deployment dependency. The mac build has the mirror
# problem and has to ship libomp.dylib. ggml's own threadpool is enough.
set(GGML_OPENMP  OFF CACHE BOOL "" FORCE)

# M0 is CPU-only. Vulkan is an M1 decision and needs the Vulkan SDK present.
set(GGML_VULKAN  OFF CACHE BOOL "" FORCE)

# Nothing here needs whisper's own executables.
set(WHISPER_BUILD_TESTS    OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_SERVER   OFF CACHE BOOL "" FORCE)
set(WHISPER_CURL           OFF CACHE BOOL "" FORCE)
set(WHISPER_SDL2           OFF CACHE BOOL "" FORCE)

# Third-party warnings are not ours to fix and drown the log.
set(WHISPER_ALL_WARNINGS_3RD_PARTY OFF CACHE BOOL "" FORCE)

if(MSVC)
  # whisper.cpp sources are UTF-8; without this MSVC guesses the ANSI codepage.
  add_compile_options($<$<COMPILE_LANGUAGE:C,CXX>:/utf-8>)
endif()

add_subdirectory("${OSW_WHISPER_CPP_DIR}" "${CMAKE_CURRENT_BINARY_DIR}/whisper.cpp")

message(STATUS "osw_native: whisper.cpp at ${OSW_WHISPER_CPP_DIR}")
message(STATUS "osw_native: shared=${BUILD_SHARED_LIBS} avx2=${GGML_AVX2} openmp=${GGML_OPENMP} vulkan=${GGML_VULKAN}")
