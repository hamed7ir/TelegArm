# FFmpeg in TelegArm — what ships and how it was built

Since 2026-10-16 TelegArm plays all audio and video through FFmpeg (it replaced libVLC). What ships, per architecture,
in `TelegArm\ffmpeg\ARM\` (Windows RT 8.1 / Windows 10 ARM32) and `TelegArm\ffmpeg\x86\` (every Intel/AMD PC, where the
AnyCPU + Prefer32Bit exe runs as a 32-bit process):

| file | what |
|---|---|
| `avcodec-62.dll`, `avformat-62.dll`, `avutil-60.dll`, `avfilter-11.dll`, `swresample-6.dll`, `swscale-9.dll` | FFmpeg **8.1.2**, unmodified (zero source patches), LGPL-2.1-or-later |
| `tgav.dll` | TelegArm's own shim over them (`native\tgav`, GPL-3.0-only with the app) |

## FFmpeg: ffmpeg-rt

Built by [ffmpeg-rt](https://github.com/hamed7ir/ffmpeg-rt) (`D:\repo\ffmpeg-rt`), with **rt2.1** (LLVM 23.1.1-rt2.1,
`clang-cl.exe` sha256 `d572c49356dd897ef499131d8914bf6581a8a9a62dcf928220b7db786a1dfed2`, the packaged release in
`llvm-rt\dist\rt2.1`), MSVC 14.44.35207 and SDK 10.0.19041.0, static CRT (no CRT DLL dependency).

```
sh scripts/gen-rsp.sh
sh scripts/configure-arm32.sh && sh scripts/gate-config.sh && sh scripts/make-install.sh msys2-shared        # ARM32 -> dist/shared
sh scripts/configure-x86.sh && FF_BUILD_DIR=msys2-shared-x86 FF_ARCH=x86 sh scripts/gate-config.sh \
  && sh scripts/make-install.sh msys2-shared-x86                                                              # x86   -> dist/shared-x86
sh scripts/verify-build.sh; sh scripts/verify-x86.sh
```

Components (`scripts/components.sh`, the same for both architectures):
- decoders: h264, hevc, av1, vp8, vp9, opus, aac, flac, mp3, mp3float, vorbis, mpeg4, h263, alac, and PCM (s16le/be,
  s24le/be, s32le, f32le, f64le, u8, alaw, mulaw);
- every demuxer, parser, bitstream filter and non-network protocol; muxers mp4 + matroska;
- avfilter with only `atempo`, `aformat`, `aresample` (+ the always-built abuffer/abuffersink): playback at 1.5x/2x
  without a pitch shift;
- **no** `--enable-gpl`, **no** `--enable-nonfree`: every component is LGPL-2.1-or-later.

ARM32 carries FFmpeg's NEON assembly (assembled by rt2.1's integrated assembler) and DXVA2 (unused by TelegArm).
x86 is C only (no nasm on the build box: `--disable-x86asm --disable-inline-asm`).

The full configure line is embedded in every build (`avutil_configuration()`, and `FFMPEG_CONFIGURATION` in the build
tree's `config.h`).

## Verified on the build box (2026-10-16)

- `gate-config.sh` (reads `config.h` / `config_components.h`, never the configure summary): ARM32 47/47, x86 44/44.
- ARM32 (`verify-build.sh`): objects found = 996, inspected = 996, ARMNT = 996 (x64 control reads AMD64); NEON in every
  shipped DLL (avcodec-62.dll: 5018 NEON instructions); undefined `__aeabi_*` = 0 (seeded control matches); every DLL
  OS/subsystem version 6.0 (the RT 8.1 loader takes <= 6.3). **STRUCTURAL**: ARM32 code cannot run on the x64 box.
- x86 (`verify-x86.sh`): objects found = 911, inspected = 911, I386 = 911; imports KERNEL32, bcrypt and each other only.
- **RUN** on the x86 box: TelegArm.SelfTest `media:` checks (an OGG/Opus note decodes whole, 2x keeps the pitch; a
  640x360 H.264 clip decodes in order, seeks, all 900 frames) and the live rig `TELEGARM_LIVE=media` (17 checks).

## tgav

`powershell -ExecutionPolicy Bypass -File native\tgav\build.ps1` (or `-Arch ARM` / `-Arch x86`) compiles `tgav.c`
with the same rt2.1 (it checks the hash), links the import libraries from ffmpeg-rt's `dist`, and stages `tgav.dll`
with the FFmpeg DLLs into `TelegArm\ffmpeg\<arch>\`. ARM32 `tgav.dll`: ARMNT, OS/subsystem 6.2; x86: I386, 6.0; both
import only the FFmpeg DLLs and KERNEL32.

## SHA-256 of what ships

```
ARM
359fb357e367517974245de5eeee86e480e469c830f1b327aa0420c9a16d63a3    4247040  avcodec-62.dll
0c0ed31d1d09d5a80512e4cca4bd7e6088712a630d9cd7bd58a0ee5207e0d15f     443392  avfilter-11.dll
a9042d3349b475d5a9e9dec536376d2253db32e50fd67efef509be41fbae081d    1748480  avformat-62.dll
12254c4b3cf25fb2d83909644e8e7cfadaae5693ac20e651726aa4a2c8335bde    1010688  avutil-60.dll
fae5520d5ee6d20427a5d9717f126444a7a18a81733b650cb84d8d27cc7b8d40     384512  swresample-6.dll
b404e5e36aa80699fe5d69ad664e8363e7a6b651cb120a2a9b25b25e26b5a42a    1065472  swscale-9.dll
564c5b273a290df2283fd14cb06ed4d6a9affe3dc195cd85485b93eb8a094c51      92160  tgav.dll
x86
700cd12776107fdbf4bf9c2a09f80823663d54c915e22a2eb41880a6d593e3ea    6054400  avcodec-62.dll
e87018deb510a55b4837e430bf0a32301a36e0890244030935d02d61ef642661     466432  avfilter-11.dll
f1a786c09912054a5064015ebf7ea898ac15b91331e312ee98588e36f3366b95    2059776  avformat-62.dll
92cb08b4cd805cd0bbfc91dfb09e2f328f4d868bc42e8feea4d860ff4572cebf    1147904  avutil-60.dll
1eca8dd084d9b06acab0eb863128b44a83dfc8551c55af495b0a346d2a5aa1f3     399360  swresample-6.dll
5e4d3ccbe50ec3ae52d911e0732a6d22040490a3acd23865d9fc8763f169094e    1580544  swscale-9.dll
db3029c64e600ee580055307dd4a84b43f010dd3f5731d110e3ae51b605cad83     107520  tgav.dll
```
