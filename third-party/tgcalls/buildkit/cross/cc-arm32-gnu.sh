#!/bin/sh
# rt2.1 clang GNU driver -> armv7-pc-windows-msvc (ARMNT, MSVC ABI, static CRT). For configure-based deps (libvpx).
exec F:/cr/rt21-cr/bin/clang.exe @D:/tgcalls/tc/arm32-gnu.rsp "$@"
