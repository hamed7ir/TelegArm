#!/bin/sh
# rt2.1 clang++ GNU driver -> armv7-pc-windows-msvc. -std=gnu++17 goes LAST: libvpx's configure adds -std=gnu++11,
# which the MSVC 14.44 STL headers reject (they need C++14+: "constexpr function's return type 'void'...").
exec F:/cr/rt21-cr/bin/clang++.exe @D:/tgcalls/tc/arm32-gnu.rsp "$@" -std=gnu++17
