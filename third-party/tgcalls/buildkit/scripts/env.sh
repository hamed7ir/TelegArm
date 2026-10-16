# tgcalls ARM32 build environment (sourced by every build script)
export MSYS2_ARG_CONV_EXCL='*'
export PYTHONDONTWRITEBYTECODE=1
CMAKE="D:/Program Files/vs22buildtools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"
NINJA="D:/Program Files/vs22buildtools/Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe"
RT21=F:/cr/rt21-cr/bin
TC_ARM=D:/tgcalls/tc/arm32-rt21.cmake
PREFIX_ARM=D:/tgcalls/prefix/arm32
B=D:/tgcalls/build
JOBS=2
TC_X86=D:/tgcalls/tc/x86-rt21.cmake
PREFIX_X86=D:/tgcalls/prefix/x86
NASM=D:/tgcalls/tools/host/bin/nasm.exe
