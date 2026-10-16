#!/usr/bin/env bash
# opus v1.5.2 (tdesktop's pin), static. ARM32: NEON intrinsics presumed (mandatory on Windows ARM), no runtime
# detection (its MSVC path probes with __try/__emit) - the define set of Chromium's ARM32 GN build. opus's CMake alone
# (PRESUME_NEON without MAY_HAVE_NEON) leaves celt_inner_prod_neon undeclared, so the MAY_HAVE defines are injected
# by D:/tgcalls/cmake/opus-arm-neon.cmake (CMAKE_PROJECT_INCLUDE; an env CFLAGS breaks CMake's arch detection).
. /d/tgcalls/scripts/env.sh
set -e
OPUS_ARCH_ARGS=${OPUS_ARCH_ARGS--DOPUS_MAY_HAVE_NEON=OFF -DOPUS_PRESUME_NEON=ON -DCMAKE_PROJECT_INCLUDE=D:/tgcalls/cmake/opus-arm-neon.cmake}
TC=${TC:-$TC_ARM}; PFX=${PFX:-$PREFIX_ARM}; BD=${BD:-$B/opus-arm32}
"$CMAKE" -G Ninja -S D:/tgcalls/deps/opus -B $BD -DCMAKE_MAKE_PROGRAM="$NINJA" \
  -DCMAKE_TOOLCHAIN_FILE=$TC -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=$PFX \
  -DOPUS_STATIC_RUNTIME=ON -DOPUS_BUILD_TESTING=OFF -DOPUS_BUILD_PROGRAMS=OFF $OPUS_ARCH_ARGS
"$CMAKE" --build $BD -- -j $JOBS
"$CMAKE" --install $BD
