cmake_minimum_required(VERSION 3.20)
project(debs_simulator LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

enable_testing()

add_executable(simulator_tests tests/simulator_tests.cpp)
target_include_directories(simulator_tests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src)
target_compile_options(simulator_tests PRIVATE -Wall -Wextra -Wpedantic)

add_test(NAME simulator_tests COMMAND simulator_tests)
