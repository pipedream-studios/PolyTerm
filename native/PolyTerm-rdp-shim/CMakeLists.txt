cmake_minimum_required(VERSION 3.16)
project(polyterm_rdp_shim C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

add_library(polyterm_rdp_shim SHARED
    polyterm_rdp_shim.c
)

target_include_directories(polyterm_rdp_shim
    PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}
)

if(WIN32)
    # vcpkg's freerdp port (feature "client") exports CONFIG-mode packages, not pkg-config .pc files -
    # confirmed via the port's own vcpkg_cmake_config_fixup calls (lib/cmake/FreeRDP-Client3,
    # lib/cmake/WinPR3). No pthread link needed here: the shim uses WinPR's own CreateThread/
    # WaitForSingleObject (a thin wrapper over the real Win32 API on this platform), not pthread
    # directly.
    find_package(FreeRDP-Client3 CONFIG REQUIRED)
    find_package(WinPR3 CONFIG REQUIRED)

    target_link_libraries(polyterm_rdp_shim
        PRIVATE FreeRDP-Client3::freerdp-client3 WinPR3::winpr3
    )
else()
    find_package(PkgConfig REQUIRED)
    # freerdp3 alone only covers the core protocol library; freerdp_client_context_new and friends
    # (the high-level client entry-point API this shim is built on) live in libfreerdp-client3
    # instead - found the hard way via an undefined-reference link error against the real installed
    # libraries. pthread is linked explicitly here for WinPR's own CreateThread emulation on this
    # platform, which pkg-config's .pc file doesn't reliably declare as a transitive dependency.
    pkg_check_modules(FREERDP_CLIENT REQUIRED IMPORTED_TARGET freerdp-client3)
    pkg_check_modules(WINPR REQUIRED IMPORTED_TARGET winpr3)

    target_link_libraries(polyterm_rdp_shim
        PRIVATE PkgConfig::FREERDP_CLIENT PkgConfig::WINPR pthread
    )
endif()
