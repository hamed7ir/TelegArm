#!/bin/bash
# kodi-rt Stage 11: FFmpeg 9.0.2 as SHARED DLLs for other Windows RT apps (TelegArm, the ffmpeg-rt release), from the
# source Kodi links: build/s7/ffmpeg-src (FFmpeg 9.0.2 + Kodi's Windows patches + patches-ffmpeg/0001, NEON 10-bit
# HEVC motion compensation; the frozen PGO tree: the profile names static functions by source path).
# ffmpeg-rt's shape (--disable-everything + a component list, --enable-shared, no network, LGPL: no --enable-gpl, no
# --enable-nonfree) with TelegArm's components, plus libdav1d (software AV1; FFmpeg's own av1 decoder needs a GPU),
# zlib (static), and ffmpeg.exe with the null output (the device kit's workload; also a test tool).
#   ARCH     arm32 | x86 | x64
#   VARIANT  instr  arm32: -fprofile-generate (cross/cc-arm32-ffinstr.sh), for the device profiling kit
#            use    arm32: -fprofile-use (build/s11/prof/ffshared-arm32.profdata) + ThinLTO (cross/cc-arm32-ffuse.sh)
#            plain  any arch: no PGO (arm32: cross/cc-arm32-ffplain.sh; x86/x64: cross/cc-<arch>.sh with nasm)
# All with rt2.4 f6e5f8e0 (tc/rt24d-cr), static CRT (/MT): each DLL imports only Windows system DLLs and the other
# FFmpeg DLLs. Output: build/s11/dist-<arch>-<variant> (bin: DLLs, ffmpeg.exe and their PDBs; lib: import libraries;
# include). Gate: config.h/config_components.h (configure turns off a component whose dependency is missing and
# still exits 0); VARIANT=use also must equal the instr build's configuration (paths aside), so the profile fits.
# Run with MSYS2's bash (GNU make), detached: powershell -File scripts/launch-detached.ps1 -Script <frozen copy>
#   -Shell msys2 -ScriptArgs "<arch> <variant>"
set -u
unset MSYS2_ARG_CONV_EXCL
ARCH=${1:?usage: s11-ffmpeg-shared.sh <arm32|x86|x64> <instr|use|plain>}; VARIANT=${2:?usage: s11-ffmpeg-shared.sh <arch> <variant>}
LOG=/f/kodi-rt/logs/s11-ffmpeg-$ARCH-$VARIANT.log
exec > "$LOG" 2>&1
echo "start $(date '+%F %T'): FFmpeg 9.0.2 shared, $ARCH $VARIANT"
LANE=/f/kodi-rt/logs/s9-lane.status
echo "LANE original FFmpeg shared $ARCH $VARIANT (TelegArm / ffmpeg-rt) since $(date '+%F %T')" > $LANE
trap 'echo "LANE FREE since $(date "+%F %T")" > $LANE' EXIT
. /f/kodi-rt/cross/arch.sh
RT=F:/kodi-rt/tc/rt24d-cr/bin
CRT=F:/kodi-rt/tc/rt24d-cr/lib/clang/23/lib/windows
X=/f/kodi-rt/cross
SRC=/f/kodi-rt/build/s7/ffmpeg-src
B=/f/kodi-rt/build/s11/ffshared-$ARCH-$VARIANT
DIST=/f/kodi-rt/build/s11/dist-$ARCH-$VARIANT
PFXW=F:/kodi-rt/prefix/$ARCH
JOBS=8
case $ARCH-$VARIANT in
  arm32-instr) CC=$X/cc-arm32-ffinstr.sh; LIBPATH="-libpath:$CRT" ;;
  arm32-use)   CC=$X/cc-arm32-ffuse.sh;   LIBPATH=""
               [ -f /f/kodi-rt/build/s11/prof/ffshared-arm32.profdata ] || { echo "no build/s11/prof/ffshared-arm32.profdata"; exit 1; } ;;
  arm32-plain) CC=$X/cc-arm32-ffplain.sh; LIBPATH="" ;;
  x86-plain|x64-plain) CC=$X/cc-$ARCH.sh; LIBPATH="" ;;
  *) echo "unsupported: $ARCH $VARIANT (PGO is arm32 only)"; exit 1 ;;
esac
case $ARCH in
  arm32) TARGET="--enable-cross-compile --target-os=win32 --arch=arm --as=$X/as-arm32-rt24d.sh --windres=$X/windres-arm32.sh
                 --enable-asm --enable-neon --enable-thumb"
         LINKRSP=F:/kodi-rt/cross/arm32-link.rsp
         GATEARCH="ARCH_ARM=1 HAVE_NEON=1 HAVE_ARMV6T2=1 CONFIG_THUMB=1" ;;
  x86)   TARGET="--enable-cross-compile --arch=x86 --cpu=i686 --target-os=win32 --windres=$X/windres-x86.sh --x86asmexe=$A_NASM"
         LINKRSP=F:/kodi-rt/cross/x86-target-link.rsp
         GATEARCH="ARCH_X86_32=1 HAVE_X86ASM=1" ;;
  x64)   TARGET="--enable-cross-compile --arch=x86_64 --target-os=win64 --windres=$X/windres-x64.sh --x86asmexe=$A_NASM"
         LINKRSP=F:/kodi-rt/cross/x64-target-link.rsp
         GATEARCH="ARCH_X86_64=1 HAVE_X86ASM=1" ;;
esac
for f in z.lib libdav1d.lib; do [ -f "/f/kodi-rt/prefix/$ARCH/lib/$f" ] || { echo "no prefix/$ARCH/lib/$f"; exit 1; }; done
export PATH="$X/ffshared-bin:/usr/bin:/usr/local/bin:$PATH"
"$RT/clang-cl.exe" --version | head -1
rm -rf "$B" "$DIST"; mkdir -p "$B"; cd "$B" || exit 1
export TMPDIR=. TMP=/d/MSYS2/tmp TEMP=/d/MSYS2/tmp
export NM="$RT/llvm-nm.exe"

# TelegArm's components (its request, 2026-11-16) on ffmpeg-rt's base; +libdav1d, zlib; ffmpeg.exe needs the null
# muxer, wrapped_avframe/pcm_s16le and the format/scale/null filters (and selects a few more itself). FFmpeg 9 builds
# the buffer source and sink (abuffer/abuffersink, buffer/buffersink) into libavfilter always: no switch for them.
"$SRC/configure" \
  --prefix="$DIST" \
  $TARGET \
  --cc="$CC" --cxx="$CC" \
  --ld="$RT/lld-link.exe" --ar="$RT/llvm-lib.exe" --nm="$RT/llvm-nm.exe" \
  --ranlib="$RT/llvm-ranlib.exe" --strip="$RT/llvm-strip.exe" \
  --host-cc="$X/hostcc-x64.sh" --host-ld="$RT/lld-link.exe" --host-ldflags="@F:/kodi-rt/cross/x64-link.rsp" \
  --enable-shared --disable-static --enable-w32threads \
  --disable-everything --disable-doc --disable-avdevice --disable-network --disable-autodetect \
  --disable-programs --enable-ffmpeg \
  --enable-zlib --enable-libdav1d \
  --enable-dxva2 --enable-hwaccel=h264_dxva2 \
  --enable-decoder=libdav1d,h264,hevc,av1,vp8,vp9,opus,aac,flac,mp3,mp3float,vorbis,mpeg4,h263,alac \
  --enable-decoder=pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_f32le,pcm_f64le,pcm_u8,pcm_alaw,pcm_mulaw \
  --enable-demuxers --enable-parsers --enable-bsfs --enable-protocols \
  --enable-muxer=mp4,matroska,null --enable-encoder=wrapped_avframe,pcm_s16le \
  --enable-avfilter --enable-filter=atempo,aformat,aresample,format,scale,null,anull \
  --enable-swscale --enable-swresample \
  --extra-version=kodi-rt \
  --extra-cflags="-I$PFXW/include -DWIN32_LEAN_AND_MEAN" \
  --extra-ldflags="@$LINKRSP -libpath:$PFXW/lib $LIBPATH"
rc=$?
echo "===CONFIGURE EXIT=$rc==="
[ $rc -eq 0 ] || { tail -40 ffbuild/config.log; echo "CONFIGURE FAILED"; exit 1; }

H=config.h; C=config_components.h
val () { grep -hE "define $1( |\$)" "$H" "$C" 2>/dev/null | head -1 | awk '{print $3}'; }
FAIL=0
for kv in $GATEARCH HAVE_W32THREADS=1 CONFIG_SHARED=1 CONFIG_STATIC=0 CONFIG_GPL=0 CONFIG_NONFREE=0 CONFIG_VERSION3=0 \
          CONFIG_ZLIB=1 CONFIG_LIBDAV1D=1 CONFIG_NETWORK=0 CONFIG_AVFILTER=1 CONFIG_SWSCALE=1 CONFIG_SWRESAMPLE=1 \
          CONFIG_AVDEVICE=0 CONFIG_FFMPEG=1 CONFIG_DXVA2=1 CONFIG_H264_DXVA2_HWACCEL=1 \
          CONFIG_H264_DECODER=1 CONFIG_HEVC_DECODER=1 CONFIG_AV1_DECODER=1 CONFIG_LIBDAV1D_DECODER=1 CONFIG_VP8_DECODER=1 \
          CONFIG_VP9_DECODER=1 CONFIG_OPUS_DECODER=1 CONFIG_AAC_DECODER=1 CONFIG_FLAC_DECODER=1 CONFIG_MP3_DECODER=1 \
          CONFIG_MP3FLOAT_DECODER=1 CONFIG_VORBIS_DECODER=1 CONFIG_MPEG4_DECODER=1 CONFIG_H263_DECODER=1 CONFIG_ALAC_DECODER=1 \
          CONFIG_PCM_S16LE_DECODER=1 CONFIG_PCM_S16BE_DECODER=1 CONFIG_PCM_S24LE_DECODER=1 CONFIG_PCM_S24BE_DECODER=1 \
          CONFIG_PCM_S32LE_DECODER=1 CONFIG_PCM_F32LE_DECODER=1 CONFIG_PCM_F64LE_DECODER=1 CONFIG_PCM_U8_DECODER=1 \
          CONFIG_PCM_ALAW_DECODER=1 CONFIG_PCM_MULAW_DECODER=1 \
          CONFIG_MOV_DEMUXER=1 CONFIG_MATROSKA_DEMUXER=1 CONFIG_OGG_DEMUXER=1 CONFIG_MP3_DEMUXER=1 CONFIG_AAC_DEMUXER=1 \
          CONFIG_FLAC_DEMUXER=1 CONFIG_WAV_DEMUXER=1 CONFIG_H264_PARSER=1 CONFIG_HEVC_PARSER=1 CONFIG_VP9_PARSER=1 \
          CONFIG_OPUS_PARSER=1 CONFIG_AAC_PARSER=1 CONFIG_H264_MP4TOANNEXB_BSF=1 CONFIG_FILE_PROTOCOL=1 CONFIG_PIPE_PROTOCOL=1 \
          CONFIG_HTTP_PROTOCOL=0 CONFIG_MP4_MUXER=1 CONFIG_MATROSKA_MUXER=1 CONFIG_NULL_MUXER=1 \
          CONFIG_ATEMPO_FILTER=1 CONFIG_AFORMAT_FILTER=1 CONFIG_ARESAMPLE_FILTER=1 CONFIG_SCALE_FILTER=1; do
  k=${kv%=*}; want=${kv#*=}; v=$(val $k); [ -z "$v" ] && v="<ABSENT>"
  if [ "$v" = "$want" ]; then echo "  OK    $k = $v"; else echo "  FAIL  $k = $v (want $want)"; FAIL=$((FAIL+1)); fi
done
if [ "$ARCH-$VARIANT" = arm32-use ]; then
  # the profile fits only the configuration it counted: config.h/config_components.h equal to the instr build's,
  # apart from paths and the compiler/flags lines (under -flto configure's compile-only inline-asm tests cannot fail)
  I=/f/kodi-rt/build/s11/ffshared-arm32-instr
  d=$(for f in config.h config_components.h; do diff <(grep -vE 'FFMPEG_CONFIGURATION|CC_IDENT|FFMPEG_DATADIR|AVCONV_DATADIR' "$I/$f") \
        <(grep -vE 'FFMPEG_CONFIGURATION|CC_IDENT|FFMPEG_DATADIR|AVCONV_DATADIR' "$f"); done)
  [ -z "$d" ] && echo "  OK    configuration equal to the instrumented build's" || { echo "  FAIL  configuration differs from the instr build:"; echo "$d" | head -20; FAIL=$((FAIL+1)); }
fi
[ $FAIL -eq 0 ] || { echo "GATE FAILED ($FAIL)"; exit 1; }
echo "GATE PASSED"

echo "=== make -j$JOBS $(date '+%T')"
make -j$JOBS > /f/kodi-rt/logs/s11-ffmpeg-$ARCH-$VARIANT-make.log 2>&1 || { tail -40 /f/kodi-rt/logs/s11-ffmpeg-$ARCH-$VARIANT-make.log; echo "MAKE FAILED"; exit 1; }
echo "make warnings: $(grep -c 'warning:' /f/kodi-rt/logs/s11-ffmpeg-$ARCH-$VARIANT-make.log)"
echo "=== make install $(date '+%T')"
make install > /f/kodi-rt/logs/s11-ffmpeg-$ARCH-$VARIANT-install.log 2>&1 || { tail -20 /f/kodi-rt/logs/s11-ffmpeg-$ARCH-$VARIANT-install.log; echo "INSTALL FAILED"; exit 1; }
# the PDBs next to their DLLs (make install leaves them in the build tree)
for p in $(find "$B" -maxdepth 2 -name '*.pdb'); do cp -f "$p" "$DIST/bin/"; done
ls -la "$DIST/bin"
echo "FFMPEG SHARED DONE $(date '+%F %T')"
