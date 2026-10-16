#!/usr/bin/env bash
# NASM 3.01 (Chromium's third_party/nasm source, copied to D:/tgcalls/deps/nasm) as a host x64 tool for the x86
# deps' SIMD assembly (libvpx, mozjpeg, FFmpeg, BoringSSL). Installed to D:/tgcalls/tools/host/bin/nasm.exe.
. /d/tgcalls/scripts/env.sh
set -e
"$CMAKE" -G Ninja -S D:/tgcalls/cmake/nasm -B $B/nasm-host -DCMAKE_MAKE_PROGRAM="$NINJA" \
  -DCMAKE_TOOLCHAIN_FILE=D:/tgcalls/tc/x64-host-rt21.cmake -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=D:/tgcalls/tools/host
"$CMAKE" --build $B/nasm-host -- -j $JOBS
"$CMAKE" --install $B/nasm-host
D:/tgcalls/tools/host/bin/nasm.exe -v
