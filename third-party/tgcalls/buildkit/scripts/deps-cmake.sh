#!/usr/bin/env bash
# Rebuild every CMake-built dependency from clean build dirs with the current toolchain (TC/PFX/ARCH overridable).
ARCH=${ARCH:-arm32}
for d in ${DEPS_LIST:-opus boringssl mozjpeg openh264 zlib rnnoise}; do
  echo "##### $d ($ARCH)"
  rm -rf D:/tgcalls/build/$d-$ARCH
  BD=D:/tgcalls/build/$d-$ARCH bash /d/tgcalls/scripts/build-$d.sh || { echo "##### $d FAILED"; exit 1; }
done
echo "##### deps-cmake $ARCH DONE"
