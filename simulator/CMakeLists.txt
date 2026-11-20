cmake_minimum_required(VERSION 3.20)
project(debs_simulator LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

enable_testing()

find_package(Threads REQUIRED)

add_executable(simulator_tests tests/simulator_tests.cpp)
target_include_directories(simulator_tests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src)
target_compile_options(simulator_tests PRIVATE -Wall -Wextra -Wpedantic $<$<CONFIG:Release>:-O3>)

add_executable(simulator_http_tests tests/http_sender_tests.cpp)
target_include_directories(simulator_http_tests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src)
target_link_libraries(simulator_http_tests PRIVATE Threads::Threads)
target_compile_options(simulator_http_tests PRIVATE -Wall -Wextra -Wpedantic $<$<CONFIG:Release>:-O3>)

add_executable(tick_simulator src/main.cpp)
target_include_directories(tick_simulator PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src)
target_link_libraries(tick_simulator PRIVATE Threads::Threads)
target_compile_options(tick_simulator PRIVATE -Wall -Wextra -Wpedantic $<$<CONFIG:Release>:-O3>)

add_test(NAME simulator_tests COMMAND simulator_tests)
add_test(NAME simulator_http_tests COMMAND simulator_http_tests)
