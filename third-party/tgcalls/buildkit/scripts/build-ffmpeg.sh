#!/usr/bin/env bash
# FFmpeg 8.1.2 (hamed7ir/ffmpeg-rt's verified pin; tdesktop uses n8.1.3) as STATIC libraries.
#   ARCH=arm32 (default): ffmpeg-rt's cross wrappers (cc-arm32.sh = clang-cl -MT, as-arm32.sh = clang GNU driver for
#     the GAS .S NEON files) with rt2.1 instead of rt2.
#   ARCH=x86: D:/tgcalls/cross/cc-x86.sh (clang-cl i686 -MT), NASM 3.01 (built from Chromium's copy) as x86asm.
# Differences from ffmpeg-rt/scripts/configure-arm32.sh: --enable-static --disable-shared, --ar=llvm-ar (FFmpeg passes
# GNU "rc" flags, which llvm-lib cannot take), no DXVA2 hwaccel (tgcalls/tg_owt never use it), no muxers, and instead
# of ffmpeg-rt's all-demuxers/parsers/bsfs/protocols: tdesktop's demuxer list (tgcalls demuxes ogg audio parts and
# mp4/matroska video parts through its own AVIO) + parsers for the enabled codecs + the file protocol; decoders =
# ffmpeg-rt's set + vorbis; swscale/swresample.
# Run with MSYS2's bash -l (GNU make). Usage: [ARCH=x86] bash build-ffmpeg.sh
set -u
unset MSYS2_ARG_CONV_EXCL
ARCH=${1:-${ARCH:-arm32}}  # argument: env vars set on the command line do not reach MSYS2 bash started from Git Bash
export RT2=F:/cr/rt21-cr/bin
FR=/d/tgcalls/deps/ffmpeg-rt
FF_ROOT=$FR
. $FR/config.sh
rt_check_config || exit 1
sh $FR/scripts/gen-rsp.sh || exit 1
export PATH="$FR/cross/bin:/usr/bin:/usr/local/bin:$PATH"
X=$FR/cross
case $ARCH in
  arm32)
    ARCH_ARGS=(--arch=arm --cc="$X/cc-arm32.sh" --cxx="$X/cc-arm32.sh" --as="$X/as-arm32.sh"
               --windres="$X/windres-arm32.sh" --enable-asm --enable-neon --enable-thumb
               --extra-ldflags="@$FF_ROOT_W/cross/arm32-link.rsp")
    # HAVE_VFP_ARGS=1: Windows ARM32 is hard-float; without it the VFP/NEON float asm assumes soft-float argument
    # passing and silently corrupts float math (Venetium patch 0023 forced it for its GN build).
    GATE_ARCH="HAVE_NEON=1 HAVE_VFP_ARGS=1 CONFIG_THUMB=1" ;;
  x86)
    # --disable-inline-asm: libavcodec/x86/cabac.h's GNU inline asm needs more registers than clang can give it on
    # i686 ("inline assembly requires more registers than available"); an MSVC-built FFmpeg (tdesktop's) has no
    # inline asm either. The NASM (x86asm) SIMD is unaffected.
    ARCH_ARGS=(--arch=x86 --cc=/d/tgcalls/cross/cc-x86.sh --cxx=/d/tgcalls/cross/cc-x86.sh
               --x86asmexe=D:/tgcalls/tools/host/bin/nasm.exe --enable-asm --enable-x86asm --disable-inline-asm
               --extra-ldflags=@D:/tgcalls/tc/x86-link.rsp)
    GATE_ARCH="HAVE_X86ASM=1 HAVE_SSE2=1 HAVE_SSSE3=1 HAVE_INLINE_ASM=0" ;;
  *) echo "bad ARCH"; exit 2 ;;
esac
B=/d/tgcalls/build/ffmpeg-$ARCH
mkdir -p $B && cd $B || exit 1
export TMPDIR=.
export TMP="${TMP:-$MSYS2/tmp}" TEMP="${TMP:-$MSYS2/tmp}"
export NM="$RT2/llvm-nm.exe"
if [ ! -f ffbuild/config.mak ]; then
  $FR/deps/ffmpeg-8.1.2/configure \
    --prefix=/d/tgcalls/prefix/$ARCH \
    --enable-cross-compile --target-os=win32 "${ARCH_ARGS[@]}" \
    --ld="$RT2/lld-link.exe" --ar="$RT2/llvm-ar.exe" --nm="$RT2/llvm-nm.exe" \
    --ranlib="$RT2/llvm-ranlib.exe" --strip="$RT2/llvm-strip.exe" \
    --host-cc="$X/hostcc-x64.sh" --host-ld="$RT2/lld-link.exe" \
    --host-ldflags="@$FF_ROOT_W/cross/x64-link.rsp" \
    --enable-static --disable-shared \
    --disable-everything --disable-programs --disable-doc \
    --disable-avdevice --disable-avfilter --disable-network --disable-autodetect \
    --enable-decoder=h264,hevc,av1,vp8,vp9,opus,aac,flac,pcm_s16le,vorbis \
    --enable-demuxer=ogg,mov,matroska,aac,flac,h264,hevc,m4v,mp3,wav \
    --enable-parser=h264,hevc,av1,vp8,vp9,opus,aac,flac,vorbis,mpegaudio \
    --enable-protocol=file \
    --enable-avformat --enable-swresample --enable-swscale || { echo "CONFIGURE FAILED"; exit 1; }
fi
# gate (adapted from ffmpeg-rt/scripts/gate-config.sh for the static variant): read config.h, not the summary
H=config.h; C=config_components.h
val () { grep -hE "define $1( |\$)" "$H" "$C" 2>/dev/null | head -1 | awk '{print $3}'; }
FAIL=0
for kv in $GATE_ARCH CONFIG_STATIC=1 CONFIG_SHARED=0 CONFIG_H264_DECODER=1 CONFIG_OPUS_DECODER=1 \
          CONFIG_VP8_DECODER=1 CONFIG_VP9_DECODER=1 CONFIG_VORBIS_DECODER=1 CONFIG_OGG_DEMUXER=1 CONFIG_MOV_DEMUXER=1 \
          CONFIG_MATROSKA_DEMUXER=1 CONFIG_H264_PARSER=1 CONFIG_SWSCALE=1 CONFIG_SWRESAMPLE=1; do
  k=${kv%=*}; want=${kv#*=}; v=$(val $k); [ -z "$v" ] && v="<ABSENT>"
  if [ "$v" = "$want" ]; then echo "  OK    $k = $v"; else echo "  FAIL  $k = $v (want $want)"; FAIL=$((FAIL+1)); fi
done
[ $FAIL -eq 0 ] || { echo "GATE FAILED"; exit 1; }
echo "GATE PASSED"
make -j${JOBS:-2} || { echo "MAKE FAILED"; exit 1; }
make install || { echo "INSTALL FAILED"; exit 1; }
echo "FFMPEG DONE"
