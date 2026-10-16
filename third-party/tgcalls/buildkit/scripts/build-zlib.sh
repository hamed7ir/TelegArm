#!/usr/bin/env bash
# zlib e3dc0a85 (tdesktop's pin), static, ZLIB_WINAPI like tdesktop (consumers must define it too), injected by
# D:/tgcalls/cmake/zlib-winapi.cmake (CMAKE_PROJECT_INCLUDE; -DCMAKE_C_FLAGS would drop the toolchain's --target and
# an env CFLAGS breaks CMake's arch detection).
. /d/tgcalls/scripts/env.sh
set -e
TC=${TC:-$TC_ARM}; PFX=${PFX:-$PREFIX_ARM}; BD=${BD:-$B/zlib-arm32}
"$CMAKE" -G Ninja -S D:/tgcalls/deps/zlib -B $BD -DCMAKE_MAKE_PROGRAM="$NINJA" \
  -DCMAKE_TOOLCHAIN_FILE=$TC -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=$PFX \
  -DZLIB_BUILD_SHARED=OFF -DZLIB_BUILD_TESTING=OFF -DZLIB_BUILD_MINIZIP=OFF -DCMAKE_PROJECT_INCLUDE=D:/tgcalls/cmake/zlib-winapi.cmake
"$CMAKE" --build $BD -- -j $JOBS
"$CMAKE" --install $BD
