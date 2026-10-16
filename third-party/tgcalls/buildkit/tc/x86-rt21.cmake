# CMake toolchain: Windows x86 (i686, IMAGE_FILE_MACHINE_I386) with the fixed rt2.1 clang-cl / lld-link.
# Same pins as arm32-rt21.cmake: MSVC 14.44.35207, SDK headers 10.0.26100.0, import libraries 10.0.19041.0, static CRT.
# clang's default CPU for i686-pc-windows-msvc is pentium4 (SSE2 baseline).
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR x86)
set(CMAKE_SYSTEM_VERSION 10.0)

set(RT21 "F:/cr/rt21-cr/bin")
set(_VCTOOLS "D:/Program Files/vs22buildtools/VC/Tools/MSVC/14.44.35207")
set(_WINSDK "D:/Windows Kits/10")
set(_SDKINC "10.0.26100.0")
set(_SDKLIB "10.0.19041.0")

set(CMAKE_C_COMPILER   "${RT21}/clang-cl.exe")
set(CMAKE_CXX_COMPILER "${RT21}/clang-cl.exe")
set(CMAKE_ASM_COMPILER "${RT21}/clang.exe")
set(CMAKE_AR           "${RT21}/llvm-lib.exe")
set(CMAKE_LINKER       "${RT21}/lld-link.exe")
set(CMAKE_RC_COMPILER  "${RT21}/llvm-rc.exe")
set(CMAKE_MT           "${RT21}/llvm-mt.exe")

set(_PIN "--target=i686-pc-windows-msvc -vctoolsdir \"${_VCTOOLS}\" -winsdkdir \"${_WINSDK}\" -winsdkversion ${_SDKINC}")
set(CMAKE_C_FLAGS_INIT   "${_PIN}")
set(CMAKE_CXX_FLAGS_INIT "${_PIN}")
set(CMAKE_ASM_FLAGS_INIT "--target=i686-pc-windows-msvc")

set(_LIBPATHS "/libpath:\"${_VCTOOLS}/lib/x86\" /libpath:\"${_WINSDK}/Lib/${_SDKLIB}/ucrt/x86\" /libpath:\"${_WINSDK}/Lib/${_SDKLIB}/um/x86\"")
set(CMAKE_EXE_LINKER_FLAGS_INIT    "${_LIBPATHS}")
set(CMAKE_SHARED_LINKER_FLAGS_INIT "${_LIBPATHS}")
set(CMAKE_MODULE_LINKER_FLAGS_INIT "${_LIBPATHS}")

set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
cmake_policy(SET CMP0091 NEW)

set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
