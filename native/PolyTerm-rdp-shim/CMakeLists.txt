cmake_minimum_required(VERSION 3.16)
project(polyterm_rdp_shim C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

find_package(PkgConfig REQUIRED)
# freerdp3 alone only covers the core protocol library; freerdp_client_context_new and friends
# (the high-level client entry-point API this shim is built on) live in libfreerdp-client3 instead -
# found the hard way via an undefined-reference link error against the real installed libraries.
pkg_check_modules(FREERDP_CLIENT REQUIRED IMPORTED_TARGET freerdp-client3)
pkg_check_modules(WINPR REQUIRED IMPORTED_TARGET winpr3)

add_library(polyterm_rdp_shim SHARED
    polyterm_rdp_shim.c
)

target_link_libraries(polyterm_rdp_shim
    PRIVATE PkgConfig::FREERDP_CLIENT PkgConfig::WINPR pthread
)

target_include_directories(polyterm_rdp_shim
    PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}
)
