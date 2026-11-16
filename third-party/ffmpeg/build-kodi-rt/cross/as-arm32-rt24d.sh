#!/bin/sh
# kodi-rt: cross/as-arm32.sh with rt2.4 f6e5f8e0 (tc/rt24d-cr): the assembler for FFmpeg's GAS-syntax ARM .S files in
# PGO phase 2 (native objects; only the C goes through ThinLTO).
exec "F:/kodi-rt/tc/rt24d-cr/bin/clang.exe" --target=armv7-pc-windows-msvc "$@"
