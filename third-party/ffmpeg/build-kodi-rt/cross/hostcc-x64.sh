#!/bin/sh
# kodi-rt: HOST C compiler (x64) for the tools FFmpeg builds and runs during its own build (from ffmpeg-rt).
exec "F:/kodi-rt/tc/rt24b-cr/bin/clang-cl.exe" "@F:/kodi-rt/cross/x64-msvc.rsp" -MT "$@"
