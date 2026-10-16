# CMake toolchain: Windows ARM32 (thumbv7, IMAGE_FILE_MACHINE_ARMNT) with the fixed rt2.1 clang-cl / lld-link.
# Based on hamed7ir/adb-rt cross/arm32-clang-cl.cmake (rt2), switched to rt2.1 and allowed to link executables.
#  - -vctoolsdir/-winsdkdir/-winsdkversion PIN the MSVC toolset and SDK headers: 14.44 is the last toolset with ARM32
#    (14.51 #errors in vadefs.h). Headers come from SDK 10.0.26100.0 (tg_owt's WGC code needs IGraphicsCaptureSession3,
#    absent from 19041), import libraries from 10.0.19041.0, the last SDK with ARM32 libs - the same split as
#    Venetium's Chromium ARM32 build.
#  - CMAKE_SYSTEM_PROCESSOR is lowercase "arm": opus matches "(arm|aarch64)" case-sensitively to enable NEON.
#  - No _CountLeadingZeros shim (adb-rt needed one with rt2): rt2.1 clang has it as a builtin, and a shim
#    definition is then a hard error in C++ ("definition of builtin function").
#  - Static CRT (/MT): no UCRT DLL imports, same CRT as tdesktop's Windows build.
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR arm)
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

set(_PIN "--target=thumbv7-windows-msvc -vctoolsdir \"${_VCTOOLS}\" -winsdkdir \"${_WINSDK}\" -winsdkversion ${_SDKINC}")
set(CMAKE_C_FLAGS_INIT   "${_PIN}")
set(CMAKE_CXX_FLAGS_INIT "${_PIN}")
set(CMAKE_ASM_FLAGS_INIT "--target=thumbv7-windows-msvc")

set(_LIBPATHS "/libpath:\"${_VCTOOLS}/lib/arm\" /libpath:\"${_WINSDK}/Lib/${_SDKLIB}/ucrt/arm\" /libpath:\"${_WINSDK}/Lib/${_SDKLIB}/um/arm\"")
set(CMAKE_EXE_LINKER_FLAGS_INIT    "${_LIBPATHS}")
set(CMAKE_SHARED_LINKER_FLAGS_INIT "${_LIBPATHS}")
set(CMAKE_MODULE_LINKER_FLAGS_INIT "${_LIBPATHS}")

set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
cmake_policy(SET CMP0091 NEW)

set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
