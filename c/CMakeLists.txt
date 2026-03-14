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
