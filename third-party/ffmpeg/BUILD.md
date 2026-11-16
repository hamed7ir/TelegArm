# FFmpeg in TelegArm: what ships and how it was built

TelegArm plays all audio and video through FFmpeg. What ships, per architecture, in `TelegArm\ffmpeg\ARM\` (Windows
RT 8.1 / Windows 10 ARM32) and `TelegArm\ffmpeg\x86\` (every Intel/AMD PC, where the AnyCPU + Prefer32Bit exe runs as a
32-bit process):

| file | what |
|---|---|
| `avcodec-63.dll`, `avformat-63.dll`, `avutil-61.dll`, `avfilter-12.dll`, `swresample-7.dll`, `swscale-10.dll` | FFmpeg **9.0.2** with the patches below, LGPL-2.1-or-later |
| `tgav.dll` | TelegArm's own shim over them (`native\tgav`, GPL-3.0-only with the app) |

Since TelegArm 2.1.0 (2026-11-16). Up to 2.0.x it was FFmpeg 8.1.2 from [ffmpeg-rt](https://github.com/hamed7ir/ffmpeg-rt),
unpatched (git history of this file).

## FFmpeg 9.0.2 from kodi-rt

Built by kodi-rt (Hamed's Kodi port to Windows RT) for TelegArm, delivered 2026-11-16 as
`kodi-rt-ffmpeg-9.0.2-telegarm-20261116.zip` with a handoff. In short:

- **ARM32** is built with **PGO + ThinLTO** from a profile trained on a Surface RT (Tegra 3, Windows RT 8.1). **x86**
  is a plain build of the same source and components (the profile is ARM32 code), with x86 assembly (nasm 2.15.05,
  `--cpu=i686`, the SIMD level picked at run time).
- **Source**: the `ffmpeg-9.0.2.tar.xz` release tarball (sha256
  `8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e`), the FFmpeg Kodi master links.
- **Patches**, in this order (all in `patches/` here):
  1. Kodi's Windows patches `patches/kodi/002` to `007` (configure detection of openssl, zlib and libdav1d, static
     builds, a DXVA2 null check);
  2. Kodi's `patches/kodi/001-ffmpeg-all-libpostproc-plugin.patch` and `008-ffmpeg-all-pgssubdec-use-caller-colorspace.patch`;
  3. kodi-rt's `patches/kodi-rt/0001-arm-hevc-10bit-mc-neon.patch`: NEON for 10-bit HEVC motion compensation on
     32-bit ARM (160 functions; FFmpeg's checkasm passed all 1300 tests on the Surface RT).
- **Toolchain**: rt2.4 (LLVM 23.1.1-rt2.4, f6e5f8e0; clang-cl sha256
  `f24b35ac6539a117afca2cf67a87e7d80b0e3c9d5ed981dbe7884664040f1d27`), MSVC 14.44.35207, Windows SDK 10.0.26100.0
  headers, ARM32 libraries from SDK 10.0.19041.0, static CRT (`/MT`). rt2.4 rather than rt2.1: it carries the fix
  for rt2.x bug 0031 (a noreturn call at the end of a function put its return address outside the function's .pdata
  range, which breaks unwinding on Windows ARM32). `tgav.dll` is still built with rt2.1 (below); a C DLL needs no
  compiler runtime, so the two mix.
- **Static inside**: dav1d 1.5.3 (BSD-2-Clause, software AV1) in avcodec, zlib 1.3.2 (zlib licence) in avformat.
- The build scripts and compiler wrappers are in `build-kodi-rt/` (paths are the build server's).

Configure line (`avutil_configuration()`; only the `--cc` wrapper differs between the instrumented, PGO and plain
builds):

```
--enable-cross-compile --target-os=win32 --arch=arm --enable-asm --enable-neon --enable-thumb --enable-shared
--disable-static --enable-w32threads --disable-everything --disable-doc --disable-avdevice --disable-network
--disable-autodetect --disable-programs --enable-ffmpeg --enable-zlib --enable-libdav1d --enable-dxva2
--enable-hwaccel=h264_dxva2 --enable-decoder='libdav1d,h264,hevc,av1,vp8,vp9,opus,aac,flac,mp3,mp3float,vorbis,mpeg4,h263,alac'
--enable-decoder='pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_f32le,pcm_f64le,pcm_u8,pcm_alaw,pcm_mulaw'
--enable-demuxers --enable-parsers --enable-bsfs --enable-protocols --enable-muxer='mp4,matroska,null'
--enable-encoder='wrapped_avframe,pcm_s16le' --enable-avfilter
--enable-filter='atempo,aformat,aresample,format,scale,null,anull' --enable-swscale --enable-swresample
--extra-version=kodi-rt
```

(plus the cross tool paths). x86: the same with `--arch=x86 --cpu=i686` and nasm instead of `--as`/`--enable-neon`/
`--enable-thumb`.

**Licence**: `--enable-gpl`, `--enable-nonfree` and `--enable-version3` are all off: LGPL-2.1-or-later (config.h:
CONFIG_GPL 0, CONFIG_NONFREE 0, CONFIG_VERSION3 0). The licence texts are in `licenses/`.

**Components** (more than TelegArm uses): the decoders above (libdav1d is FFmpeg's choice for AV1 in software: its own
`av1` decoder needs a hardware accelerator), 355 of 367 demuxers (dash and imf need libxml2; rtp, rtsp, sap and sdp are
network; avisynth, vapoursynth, dvdvideo, libgme, libmodplug and libopenmpt need external libraries), 67 of 68 parsers
and 50 of 51 bitstream filters (ahx needs `--enable-gpl`), the non-network protocols, muxers mp4, mov, matroska and null,
and avfilter with atempo, aformat and aresample (abuffer and abuffersink are always built in FFmpeg 9).

**PGO + ThinLTO**: an instrumented build (`-fprofile-generate`) decoded 7 free clips 4 ways each on the Surface RT
(all threads; one thread; converted to BGRA; audio through aresample + aformat + atempo): Sintel H.264 High at 480p,
720p and 1080p, a Big Buck Bunny H.264 Baseline 480x270, Big Buck Bunny VP9 854x480 60 fps with Opus 5.1, an Opus
speech sample and a 320 kb/s MP3. Then `-fprofile-use` + `-flto=thin` (lld-link ThinLTO), `-mllvm -pgso=false`.
kodi-rt measured it against a plain build of the same source on the Surface RT (ffmpeg.exe, best of 3, alternating):
single-threaded H.264 +3.6 to +6.4%, AAC +2 to +6%, threads + BGRA 0 to +2%, VP9 -0.7 to +1.1%.

## Verified here (2026-11-16)

- SHA-256 of every DLL equals the handoff's.
- dumpbin 14.44 `/headers /imports`, every DLL: ARM32 machine 1C4 (ARMNT), x86 14C; OS and subsystem version 6.00
  (the RT 8.1 loader takes 6.3 or lower); imports only KERNEL32, ole32 (avcodec), USER32 and bcrypt (avutil) and each
  other: no C runtime DLL. kodi-rt checked every imported function against the Surface RT's own system DLLs.
- `tgav.c` compiles against the 9.0.2 headers unchanged (rt2.1, `/W3`, no warning), for both architectures.
- **RUN** on the x86 box: TelegArm.SelfTest `media:` 4/4 (FFmpeg loads as avcodec 63, an OGG/Opus voice note decodes
  whole and keeps its pitch at 2x, a 640x360 H.264 clip decodes in order and seeks). The decode test
  (`TELEGARM_LIVE=decode`, ffmpeg-rt's `h264_360p.mp4` / `h264_480p.mp4`, 600 frames, best of 3): 360p 3798 fps as
  playback (threads + BGRA) and 1754 fps bare (one thread), 480p 2655 / 990 fps, AAC 882x real time; FFmpeg 8.1.2
  (C only, no x86 assembly) gave 1630 / 819, 927 / 446 and 38x.
- **RUN** on the Surface RT (Windows RT 8.1, Tegra 3): the media rig (`TELEGARM_LIVE=media`): the H.264 clip plays,
  keeps time (2018 ms per 2000), seeks, pauses, resumes, follows a new size and loops; the voice note plays at 1x and
  ends on time at 2x; VideoProbe, its poster and GrabFirstFrame work. The one miss, "WebmAnimator delivered 23-24
  frames in 1.5 s" against a threshold of 25, is the same with FFmpeg 8.1.2 (23): the check's threshold, not FFmpeg.
  The decode test, against 8.1.2 on the same device: 360p 215.1 fps as playback (8.1.2: 221.7) and 187.0 bare
  (185.9), 480p 114.9 (117.2) and 106.3 (105.5), AAC 49x (50x): the same speed within 3% for H.264 and AAC. (kodi-rt's
  +3 to +6% is PGO against a plain 9.0.2; 9.0.2 itself is a little slower than 8.1.2 on these clips.)

## tgav

`powershell -ExecutionPolicy Bypass -File native\tgav\build.ps1` compiles `tgav.c` with rt2.1 (it checks the hash),
links the import libraries of an FFmpeg dist in ffmpeg-rt's layout (`FFRT\shared` = ARM32, `FFRT\shared-x86` = x86,
each with `include\` and `bin\` holding the DLLs and their `.lib` files), and stages `tgav.dll` with the FFmpeg DLLs
into `TelegArm\ffmpeg\<arch>\`. For 9.0.2 that layout was made from the zip's `arm32|x86\{bin,lib,include}` (the six
DLLs and their import libraries; not ffmpeg.exe or the PDBs). ARM32 `tgav.dll`: ARMNT, OS/subsystem 6.2; x86: I386,
6.0; both import only the FFmpeg DLLs and KERNEL32.

## SHA-256 of what ships

```
ARM
627b85262f3f652351948e64581573ca91c0c979ba1cd3c8e2171fc64a413bb8    4758016  avcodec-63.dll
1b30e3e80c3a1324bdc2e31c9b13835304f47fffd82e9bc413730b3559489789     479232  avfilter-12.dll
8f11146dbaa5c8117325bce8b0e5d8a94b22630e93f12910c693c272d9f5178e    1764864  avformat-63.dll
0a5504fb138b95011bc4c40049f45d18cd0db30775a91e492bdd07fd02ef05a0    1021440  avutil-61.dll
47fec0c1a57f25ff5c155f7e623f52c9c4216964c0974f5df27f4a20b387e79e     385024  swresample-7.dll
c921dcaadf0549e0bc40c3b8aaf395233e9654221bfa2e0707e52ad3f648911c     861696  swscale-10.dll
dffbe4bae0d9fe1b45bb3c464ddd02a61817f7c0321f2f563cc45df600610520      92672  tgav.dll
x86
aec7b6439c6983bec0366087ea12df2a4fe4db20de56c35994fed36e135c0dde    8210944  avcodec-63.dll
a64253f46abf7ec1fb0dbc0052250c8d90f6365e47e3be4c0a59644a54141b12     594432  avfilter-12.dll
41dda3b12882cd716bae779fd88facbb160afe7349deb16619c5e67f6b949a88    2189312  avformat-63.dll
dd6a368eb68ebb61dd6c8fc008672feae37486bed9893104e495c067dd371521    1241088  avutil-61.dll
230f99c8af5740b7483bb25ae119fa3079a0e1b41149d42251d5a73352fa9030     488960  swresample-7.dll
3717c3eef6b8be25bc90cf2c19fb55642358c70fa83263a91fd1451a2df45349    1771520  swscale-10.dll
ba44fbc9f27f620881ddb747cb25e6b26461c599c27bbfb4f2554c3ac65c3364     108544  tgav.dll
```
