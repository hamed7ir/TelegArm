#!/usr/bin/env bash
# desktop-app/rnnoise d8ea2b0 (tdesktop's pin), static
. /d/tgcalls/scripts/env.sh
set -e
TC=${TC:-$TC_ARM}; PFX=${PFX:-$PREFIX_ARM}; BD=${BD:-$B/rnnoise-arm32}
"$CMAKE" -G Ninja -S D:/tgcalls/deps/rnnoise -B $BD -DCMAKE_MAKE_PROGRAM="$NINJA" \
  -DCMAKE_TOOLCHAIN_FILE=$TC -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=$PFX
"$CMAKE" --build $BD -- -j $JOBS
"$CMAKE" --install $BD
