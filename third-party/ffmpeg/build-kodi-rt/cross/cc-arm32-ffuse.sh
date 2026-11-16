#!/bin/sh
# kodi-rt Stage 11: TARGET C compiler for the PGO + ThinLTO shared FFmpeg (scripts/s11-ffmpeg-shared.sh VARIANT=use):
# rt2.4 f6e5f8e0 with the profile build/s11/prof/ffshared-arm32.profdata = the shared build's own device run
# (kit kodi-rt-ffpgo-run1; Kodi's profile left out: its counts would swamp it), as Kodi's phase 2:
#  -flto=thin:    the objects are bitcode; lld-link does the codegen when it links each DLL
#  -pgso=false:   code the profile never ran keeps -O2 instead of size optimization
#  -Wno-profile-*: functions without, or with changed, profile data are expected (Chromium's set)
exec "F:/kodi-rt/tc/rt24d-cr/bin/clang-cl.exe" "@F:/kodi-rt/cross/arm32-msvc.rsp" -MT \
  -fprofile-use=F:/kodi-rt/build/s11/prof/ffshared-arm32.profdata -flto=thin -mllvm -pgso=false \
  -Wno-profile-instr-unprofiled -Wno-profile-instr-out-of-date -Wno-backend-plugin "$@"
