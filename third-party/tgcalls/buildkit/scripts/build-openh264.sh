#!/usr/bin/env bash
# openh264 v2.6.0 (tdesktop's pin) via our generated CMakeLists (meson not available), static, no NEON asm (first pass)
. /d/tgcalls/scripts/env.sh
set -e
TC=${TC:-$TC_ARM}; PFX=${PFX:-$PREFIX_ARM}; BD=${BD:-$B/openh264-arm32}
"$CMAKE" -G Ninja -S D:/tgcalls/cmake/openh264 -B $BD -DCMAKE_MAKE_PROGRAM="$NINJA" \
  -DCMAKE_TOOLCHAIN_FILE=$TC -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=$PFX
"$CMAKE" --build $BD -- -j $JOBS
"$CMAKE" --install $BD
