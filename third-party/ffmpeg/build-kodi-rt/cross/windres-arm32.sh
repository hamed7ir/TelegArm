#!/bin/sh
# kodi-rt: resource compiler for ARM32 (from ffmpeg-rt's cross/windres-arm32.sh), SDK headers 10.0.26100.0.
M="D:/Program Files/vs22buildtools/VC/Tools/MSVC/14.44.35207"
S="D:/Windows Kits/10/Include/10.0.26100.0"
exec "F:/kodi-rt/tc/rt24b-cr/bin/llvm-windres.exe" \
  --target=arm \
  --preprocessor-arg=--target=armv7-pc-windows-msvc \
  "--preprocessor-arg=-I$M/include" \
  "--preprocessor-arg=-I$S/ucrt" \
  "--preprocessor-arg=-I$S/um" \
  "--preprocessor-arg=-I$S/shared" \
  "$@"
