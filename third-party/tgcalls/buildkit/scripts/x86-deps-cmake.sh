#!/usr/bin/env bash
# x86: host NASM first, then every CMake-built dependency with the x86 toolchain and NASM-backed SIMD where the
# project has it (BoringSSL asm, mozjpeg SIMD); opus uses its default x86 intrinsics + runtime CPU detection.
. /d/tgcalls/scripts/env.sh
bash /d/tgcalls/scripts/build-nasm.sh || { echo "##### nasm FAILED"; exit 1; }
export ARCH=x86 TC=$TC_X86 PFX=$PREFIX_X86 OPUS_ARCH_ARGS="-DCMAKE_PROJECT_INCLUDE=D:/tgcalls/cmake/opus-x86-sse41.cmake"
export BSSL_ARGS="-DCMAKE_ASM_NASM_COMPILER=$NASM"
export MOZJPEG_SIMD="-DWITH_SIMD=ON -DREQUIRE_SIMD=ON -DCMAKE_ASM_NASM_COMPILER=$NASM"
bash /d/tgcalls/scripts/deps-cmake.sh
