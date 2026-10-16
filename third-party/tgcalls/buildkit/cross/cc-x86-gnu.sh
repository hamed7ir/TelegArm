#!/bin/sh
# rt2.1 clang GNU driver -> i686-pc-windows-msvc (MSVC ABI, static CRT). For configure-based deps (libvpx).
exec F:/cr/rt21-cr/bin/clang.exe @D:/tgcalls/tc/x86-gnu.rsp "$@"
