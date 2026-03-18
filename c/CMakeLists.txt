cmake_minimum_required(VERSION 3.14)
project(measflow_c C)

set(CMAKE_C_STANDARD 99)
set(CMAKE_C_STANDARD_REQUIRED ON)
include(GNUInstallDirs)

# ── Library ──────────────────────────────────────────────────────────────────

add_library(measflow measflow.c)
target_include_directories(measflow PUBLIC
    $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}>
    $<INSTALL_INTERFACE:${CMAKE_INSTALL_INCLUDEDIR}>
)

if(BUILD_SHARED_LIBS)
    target_compile_definitions(measflow PUBLIC MEASFLOW_SHARED)
    target_compile_definitions(measflow PRIVATE MEASFLOW_EXPORTS)
endif()

if(MSVC)
    target_compile_options(measflow PRIVATE /W4 /WX)
else()
    target_compile_options(measflow PRIVATE -Wall -Wextra -Wpedantic -Werror)
endif()

# ── Optional compression support (via vcpkg) ────────────────────────────────

find_package(lz4 CONFIG QUIET)
if(lz4_FOUND AND TARGET lz4::lz4)
    target_compile_definitions(measflow PUBLIC MEAS_HAVE_LZ4)
    target_link_libraries(measflow PUBLIC lz4::lz4)
    message(STATUS "LZ4 compression: enabled")
else()
    message(STATUS "LZ4 compression: disabled (lz4 not found)")
endif()

find_package(zstd CONFIG QUIET)
if(zstd_FOUND AND TARGET zstd::libzstd)
    target_compile_definitions(measflow PUBLIC MEAS_HAVE_ZSTD)
    target_link_libraries(measflow PUBLIC zstd::libzstd)
    message(STATUS "Zstd compression: enabled")
else()
    message(STATUS "Zstd compression: disabled (zstd not found)")
endif()

# ── Install ─────────────────────────────────────────────────────────────────

install(TARGETS measflow
    EXPORT measflow-targets
    ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR}
    LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}
    RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR}
)
install(FILES measflow.h DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})
install(EXPORT measflow-targets
    FILE measflow-config.cmake
    NAMESPACE measflow::
    DESTINATION ${CMAKE_INSTALL_LIBDIR}/cmake/measflow
)

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

# ── Benchmarks ──────────────────────────────────────────────────────────────

option(MEAS_BUILD_BENCHMARKS "Build format comparison benchmarks" OFF)

if(MEAS_BUILD_BENCHMARKS)
    add_executable(bench_format_comparison benchmarks/format_comparison.c)
    target_link_libraries(bench_format_comparison PRIVATE measflow)
    if(MSVC)
        target_compile_definitions(bench_format_comparison PRIVATE _CRT_SECURE_NO_WARNINGS)
        target_compile_options(bench_format_comparison PRIVATE /W4)
    else()
        target_compile_options(bench_format_comparison PRIVATE -Wall -Wextra)
    endif()

    add_executable(bench_cross_language benchmarks/cross_language.c)
    target_link_libraries(bench_cross_language PRIVATE measflow)
    if(MSVC)
        target_compile_definitions(bench_cross_language PRIVATE _CRT_SECURE_NO_WARNINGS)
        target_compile_options(bench_cross_language PRIVATE /W4)
    else()
        target_compile_options(bench_cross_language PRIVATE -Wall -Wextra)
    endif()

    find_package(hdf5 CONFIG QUIET)
    if(hdf5_FOUND)
        target_compile_definitions(bench_format_comparison PRIVATE MEAS_HAVE_HDF5)
        # Prefer shared, fall back to static (vcpkg may provide either)
        if(TARGET hdf5::hdf5-shared)
            target_link_libraries(bench_format_comparison PRIVATE hdf5::hdf5-shared)
        else()
            target_link_libraries(bench_format_comparison PRIVATE hdf5::hdf5-static)
        endif()
        message(STATUS "Benchmarks: HDF5 comparison enabled")
    else()
        message(STATUS "Benchmarks: HDF5 not found, MeasFlow-only benchmarks")
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
