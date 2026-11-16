#!/bin/sh
# kodi-rt Stage 11: TARGET C compiler for the INSTRUMENTED shared FFmpeg (scripts/s11-ffmpeg-shared.sh VARIANT=instr):
# rt2.4 f6e5f8e0 (tc/rt24d-cr, the toolchain of the final PGO build) with IR-level instrumentation, as Kodi's
# phase 1 (cross/cc-arm32-pgoinstr.sh). Every object asks for clang_rt.profile-arm.lib by /DEFAULTLIB; the script puts
# tc/rt24d-cr/lib/clang/23/lib/windows on the link path. Never put -fprofile-generate into --extra-cflags: configure
# feeds those to the assembler.
exec "F:/kodi-rt/tc/rt24d-cr/bin/clang-cl.exe" "@F:/kodi-rt/cross/arm32-msvc.rsp" -MT -fprofile-generate "$@"
