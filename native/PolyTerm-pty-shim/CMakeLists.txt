cmake_minimum_required(VERSION 3.16)
project(polyterm_pty_shim C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

add_library(polyterm_pty_shim SHARED
    polyterm_pty_shim.c
)

target_include_directories(polyterm_pty_shim
    PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}
)
