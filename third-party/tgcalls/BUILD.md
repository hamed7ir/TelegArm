# tgcalls_native.dll - how TelegArm's call library is built

`TelegArm\tgcalls\<ARM|x86>\tgcalls_native.dll` is TelegArm's own C interface over Telegram's call library
(`native/tgcalls_native/`, GPL-3.0-only like the app), linked **statically** with tgcalls, WebRTC (tg_owt) and their
dependencies. Licenses: THIRD-PARTY-NOTICES.txt, component 18.

## Corresponding source

| What | Where |
|---|---|
| TelegArm's C interface | `native/tgcalls_native/` (this repository) - `build.ps1` links it |
| Every library source tree, exactly as built (patches applied) | `tgcalls-sources-20261016.zip`, attached to each TelegArm release on GitHub; SHA-256 `e42a3e1dbbef6a651428b4e00f7d3cd3c56b08d43f251fc50473b79fb6b22cb6` |
| The upstream origin and commit of every tree | [`SOURCES.txt`](SOURCES.txt) |
| The four source patches (already applied in the snapshot) | [`patches/`](patches/) - tg_owt 0001-0003, BoringSSL 0001 |
| The scripts that built the static libraries | [`buildkit/`](buildkit/) |

## Toolchain

rt2.1 clang-cl 23.1.1 and its lld-link (the same toolchain as `third-party/ffmpeg` and `third-party/rlottie`), MSVC
14.44.35207 headers and static CRT (/MT), Windows SDK 10.0.19041.0 import libraries. ARM32 = armv7 (Thumb-2, NEON);
x86 = i686.

## Steps

1. Build the static libraries with `buildkit/scripts/` (`env.sh` sets the toolchain; one `build-*.sh` per library,
   in the order of `package.sh`) from the source snapshot. They produce the `include/` + `lib/` package per arch.
2. `powershell -ExecutionPolicy Bypass -File native\tgcalls_native\build.ps1` compiles TelegArm's C interface
   against that package and links `tgcalls_native.dll` (incremental; `-Full` rebuilds), then stages it into
   `TelegArm\tgcalls\<arch>\`.

## Shipped binaries (this release)

| File | Size | SHA-256 |
|---|---|---|
| `tgcalls\ARM\tgcalls_native.dll` | 12,384,256 | `90a0d1152b770e518c89bfdec3b131f835c946f4f65bed96f31920a82e425c9d` |
| `tgcalls\x86\tgcalls_native.dll` | 17,736,192 | `83bab4f96e403fa4d48ae1b2bde07d233aeb24586838d6a9f80b4e74aa251158` |
