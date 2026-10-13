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

# GCC/Clang export every global symbol from a shared library by default, which is all this shim's
# public prdp_* functions ever relied on (no __declspec(dllexport) in polyterm_rdp_shim.h) - fine on
# Linux/macOS, but MSVC does the opposite: nothing is exported unless explicitly annotated. Without
# this, the DLL builds and loads cleanly (NativeLibrary.TryLoad succeeds) but every P/Invoke call
# fails at runtime with "Unable to find an entry point named 'prdp_connect'" - confirmed the hard way
# against a real build. WINDOWS_EXPORT_ALL_SYMBOLS makes MSVC auto-export every public symbol,
# matching GCC/Clang's behavior instead of annotating every function in the header. No-op on
# non-Windows.
set_target_properties(polyterm_rdp_shim PROPERTIES WINDOWS_EXPORT_ALL_SYMBOLS ON)

# VCPKG_TOOLCHAIN is set by vcpkg's own toolchain file - macOS builds go through vcpkg too
# (packaging/macos/build-native-shims.sh), since Homebrew no longer supports the Intel Mac CI runner
# and vcpkg can cross-build arm64-osx from it. Its osx triplets link statically, so the resulting
# shim has no runtime dependency on a FreeRDP install.
if(WIN32 OR VCPKG_TOOLCHAIN)
    # vcpkg's freerdp port (feature "client") exports CONFIG-mode packages, not pkg-config .pc files.
    # Verified against a real local build: the installed configs are FreeRDP-ClientConfig.cmake and
    # WinPRConfig.cmake (package names "FreeRDP-Client"/"WinPR", no "3" suffix - that only appears in
    # the share/ directory names, e.g. share/winpr3/), exporting plain (non-namespaced) imported
    # targets `freerdp-client` and `winpr`. No pthread link needed here: the shim uses WinPR's own
    # CreateThread/WaitForSingleObject (a thin wrapper over the real Win32 API on this platform), not
    # pthread directly.
    find_package(FreeRDP-Client CONFIG REQUIRED)
    find_package(WinPR CONFIG REQUIRED)

    target_link_libraries(polyterm_rdp_shim
        PRIVATE freerdp-client winpr
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
