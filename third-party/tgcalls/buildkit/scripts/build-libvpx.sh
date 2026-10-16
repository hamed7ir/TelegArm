#!/usr/bin/env bash
# libvpx v1.14.1 (tdesktop's pin) driven by the rt2.1 clang GNU driver (MSVC ABI, static CRT) -> Makefiles.
#   ARCH=arm32 (default): target armv7-win32-gcc, NEON intrinsics + ads2gas-converted .S via clang's integrated
#                         assembler; --disable-runtime-cpu-detect: NEON is mandatory on Windows ARM (Venetium's
#                         win/arm-neon config does the same) and it avoids aarch32_cpudetect.c's MSVC __try/__emit probe.
#   ARCH=x86: target x86-win32-gcc, SIMD .asm through NASM (D:/tgcalls/tools/host/bin), runtime CPU detection (cpuid).
# Run with MSYS2's bash (make); Git's perl (rtcd.pl, ads2gas.pl) appended to PATH.
# --disable-dependency-tracking: gen_asm_deps.sh runs `perl -e "s;...;<backslash>1;"`, and Git's perl started from
#   MSYS2 bash loses that backslash, writing "1" into every .S.d (make: "No rule to make target '1'").
unset MSYS2_ARG_CONV_EXCL
export PATH="$PATH:/c/Program Files/Git/usr/bin"
set -e
ARCH=${1:-${ARCH:-arm32}}  # argument: env vars set on the command line do not reach MSYS2 bash started from Git Bash
S=/d/tgcalls/deps/libvpx
O=/d/tgcalls/build/libvpx-$ARCH
case $ARCH in
  arm32) TARGET=armv7-win32-gcc; W=arm32; EXTRA="--disable-runtime-cpu-detect" ;;
  x86)   TARGET=x86-win32-gcc;   W=x86;   EXTRA="--as=nasm"; export PATH="/d/tgcalls/tools/host/bin:$PATH" ;;
  *) echo "bad ARCH"; exit 2 ;;
esac
mkdir -p $O && cd $O
export CC=/d/tgcalls/cross/cc-$W-gnu.sh CXX=/d/tgcalls/cross/cxx-$W-gnu.sh LD=/d/tgcalls/cross/cc-$W-gnu.sh
export AR=F:/cr/rt21-cr/bin/llvm-ar.exe NM=F:/cr/rt21-cr/bin/llvm-nm.exe STRIP=F:/cr/rt21-cr/bin/llvm-strip.exe
if [ ! -f Makefile ]; then
  $S/configure --target=$TARGET --prefix=/d/tgcalls/prefix/$ARCH \
    --disable-examples --disable-unit-tests --disable-tools --disable-docs \
    --enable-vp8 --enable-vp9 --disable-webm-io --size-limit=4096x4096 \
    --disable-dependency-tracking $EXTRA
fi
make -j${JOBS:-2}
make install
