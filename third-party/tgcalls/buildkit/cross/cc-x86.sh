#!/bin/sh
# FFmpeg x86 TARGET compiler: clang-cl, i686 MSVC ABI, static CRT (-MT); sysroot pins in the rsp (see ffmpeg-rt's
# cross/README: never via --extra-cflags).
exec F:/cr/rt21-cr/bin/clang-cl.exe @D:/tgcalls/tc/x86-msvc.rsp -MT "$@"
