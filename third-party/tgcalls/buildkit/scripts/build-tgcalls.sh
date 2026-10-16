#!/usr/bin/env bash
# tgcalls 1c236c09 static lib (D:/tgcalls/cmake/tgcalls) + tgcalls_link_test.exe linking tgcalls, tg_owt and every
# dependency. ARCH=arm32 (default) or x86. Re-running resumes (configures only once).
. /d/tgcalls/scripts/env.sh
set -e
ARCH=${ARCH:-arm32}
case $ARCH in
  arm32) TC=$TC_ARM; PFX=$PREFIX_ARM; BUILTINS=F:/cr/rt21-cr/lib/clang/23/lib/windows/clang_rt.builtins-arm.lib ;;
  x86)   TC=$TC_X86; PFX=$PREFIX_X86; BUILTINS=F:/cr/rt21-cr/lib/clang/23/lib/windows/clang_rt.builtins-i386.lib ;;
esac
BD=$B/tgcalls-$ARCH
if [ ! -f $BD/build.ninja ]; then
  "$CMAKE" -G Ninja -S D:/tgcalls/cmake/tgcalls -B $BD -DCMAKE_MAKE_PROGRAM="$NINJA" \
    -DCMAKE_TOOLCHAIN_FILE=$TC -DCMAKE_BUILD_TYPE=Release \
    -DTGCALLS_DIR=D:/tgcalls/tgcalls -DTG_OWT_DIR=D:/tgcalls/tg_owt -DTG_OWT_LIB=$B/tg_owt-$ARCH/tg_owt.lib \
    -DDEPS_PREFIX=$PFX -DSSL_DIR=D:/tgcalls/deps/boringssl -DSSL_LIB_DIR=$B/boringssl-$ARCH \
    -DBUILTINS_LIB=$BUILTINS
fi
"$NINJA" -C $BD -j $JOBS -k 0 ${TARGETS:-tgcalls tgcalls_link_test}
echo "TGCALLS DONE"
