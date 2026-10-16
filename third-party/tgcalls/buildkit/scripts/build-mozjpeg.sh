#!/usr/bin/env bash
# mozjpeg v4.1.5 (tdesktop's pin), static libjpeg API only (WITH_JPEG8 like tdesktop), ARM32 without SIMD (its aarch32
# NEON needs GAS .S under CMake ASM); x86: MOZJPEG_SIMD="-DWITH_SIMD=ON -DCMAKE_ASM_NASM_COMPILER=<nasm>"
. /d/tgcalls/scripts/env.sh
set -e
TC=${TC:-$TC_ARM}; PFX=${PFX:-$PREFIX_ARM}; BD=${BD:-$B/mozjpeg-arm32}
"$CMAKE" -G Ninja -S D:/tgcalls/deps/mozjpeg -B $BD -DCMAKE_MAKE_PROGRAM="$NINJA" \
  -DCMAKE_TOOLCHAIN_FILE=$TC -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=$PFX \
  -DCMAKE_POLICY_VERSION_MINIMUM=3.5 -DWITH_JPEG8=ON -DPNG_SUPPORTED=OFF -DENABLE_SHARED=OFF -DENABLE_STATIC=ON \
  ${MOZJPEG_SIMD--DWITH_SIMD=OFF} -DWITH_TURBOJPEG=OFF -DWITH_CRT_DLL=OFF
"$CMAKE" --build $BD -- -j $JOBS
"$CMAKE" --install $BD
