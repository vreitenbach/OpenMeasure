cmake_minimum_required(VERSION 3.14)
project(measflow_c C)

set(CMAKE_C_STANDARD 99)
set(CMAKE_C_STANDARD_REQUIRED ON)

# ── Library ──────────────────────────────────────────────────────────────────

add_library(measflow STATIC measflow.c)
target_include_directories(measflow PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})

if(MSVC)
    target_compile_options(measflow PRIVATE /W4 /WX)
else()
    target_compile_options(measflow PRIVATE -Wall -Wextra -Wpedantic -Werror)
endif()

# ── Optional compression support ─────────────────────────────────────────────

find_package(PkgConfig QUIET)

if(PkgConfig_FOUND)
    pkg_check_modules(LZ4 IMPORTED_TARGET liblz4)
    pkg_check_modules(ZSTD IMPORTED_TARGET libzstd)
endif()

if(LZ4_FOUND)
    target_compile_definitions(measflow PUBLIC MEAS_HAVE_LZ4)
    target_link_libraries(measflow PUBLIC PkgConfig::LZ4)
    message(STATUS "LZ4 compression: enabled")
else()
    message(STATUS "LZ4 compression: disabled (liblz4 not found)")
endif()

if(ZSTD_FOUND)
    target_compile_definitions(measflow PUBLIC MEAS_HAVE_ZSTD)
    target_link_libraries(measflow PUBLIC PkgConfig::ZSTD)
    message(STATUS "Zstd compression: enabled")
else()
    message(STATUS "Zstd compression: disabled (libzstd not found)")
endif()

# ── Quickstart ───────────────────────────────────────────────────────────────

option(MEAS_BUILD_QUICKSTART "Build quickstart example" OFF)

if(MEAS_BUILD_QUICKSTART)
    add_executable(quickstart quickstart/quickstart.c)
    target_link_libraries(quickstart PRIVATE measflow m)
    if(MSVC)
        target_compile_options(quickstart PRIVATE /W4)
    else()
        target_compile_options(quickstart PRIVATE -Wall -Wextra)
    endif()
endif()

# ── Tests ────────────────────────────────────────────────────────────────────

option(MEAS_BUILD_TESTS "Build C unit tests" ON)

if(MEAS_BUILD_TESTS)
    enable_testing()
    add_executable(test_measflow tests/test_measflow.c)
    target_link_libraries(test_measflow PRIVATE measflow)
    if(MSVC)
        target_compile_options(test_measflow PRIVATE /W4)
    else()
        target_compile_options(test_measflow PRIVATE -Wall -Wextra)
    endif()
    add_test(NAME measflow_tests COMMAND test_measflow)
endif()
