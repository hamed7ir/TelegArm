#!/bin/sh
# -std=gnu++17 last: libvpx adds -std=gnu++11, which the MSVC 14.44 STL headers reject.
exec F:/cr/rt21-cr/bin/clang++.exe @D:/tgcalls/tc/x86-gnu.rsp "$@" -std=gnu++17
