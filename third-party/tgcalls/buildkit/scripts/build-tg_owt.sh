#!/usr/bin/env bash
# tg_owt d1cf250e (tdesktop v7.3.1's pin) + patches/tg_owt/0001,0002, static, Release, single-config Ninja.
# Non-packaged mode like tdesktop (TG_OWT_SPECIAL_TARGET non-empty: deps by include path only), audio backends off
# like tdesktop, BoringSSL instead of OpenSSL 3 (TG_OWT_USE_BORINGSSL from patch 0001).
# Configures only when the build dir has no build.ninja, so re-running resumes the build (chunk.sh).
. /d/tgcalls/scripts/env.sh
set -e
ARCH=${ARCH:-arm32}
TC=${TC:-$TC_ARM}; PFX=${PFX:-$PREFIX_ARM}; BD=${BD:-$B/tg_owt-$ARCH}
if [ ! -f $BD/build.ninja ]; then
  "$CMAKE" -G Ninja -S D:/tgcalls/tg_owt -B $BD -DCMAKE_MAKE_PROGRAM="$NINJA" \
    -DCMAKE_TOOLCHAIN_FILE=$TC -DCMAKE_BUILD_TYPE=Release \
    -DTG_OWT_SPECIAL_TARGET=win -DTG_OWT_BUILD_AUDIO_BACKENDS=OFF -DTG_OWT_USE_BORINGSSL=ON \
    -DTG_OWT_OPENSSL_INCLUDE_PATH=D:/tgcalls/deps/boringssl/include \
    -DTG_OWT_OPUS_INCLUDE_PATH=$PFX/include/opus \
    -DTG_OWT_LIBVPX_INCLUDE_PATH=$PFX/include \
    -DTG_OWT_OPENH264_INCLUDE_PATH=$PFX/include \
    -DTG_OWT_FFMPEG_INCLUDE_PATH=$PFX/include \
    -DTG_OWT_LIBJPEG_INCLUDE_PATH=$PFX/include \
    -DPython_EXECUTABLE=C:/Python313/python.exe
fi
"$NINJA" -C $BD -j $JOBS -k 0 tg_owt
echo "TG_OWT DONE"
