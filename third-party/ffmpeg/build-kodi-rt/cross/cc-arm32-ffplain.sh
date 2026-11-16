#!/bin/sh
# kodi-rt Stage 11: TARGET C compiler for the PLAIN shared FFmpeg (scripts/s11-ffmpeg-shared.sh VARIANT=plain): the same
# rt2.4 f6e5f8e0 and flags as the PGO build without the profile and ThinLTO, so the two can be measured against each other.
exec "F:/kodi-rt/tc/rt24d-cr/bin/clang-cl.exe" "@F:/kodi-rt/cross/arm32-msvc.rsp" -MT "$@"
