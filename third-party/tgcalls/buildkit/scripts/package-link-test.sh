#!/usr/bin/env bash
# Prove a package is self-contained: build src/tgcalls_link_test.cpp with ONLY the package's include/ and lib/ and the
# defines from BUILD-INFO.txt (no build-tree paths), then check machine + imports. Usage: bash package-link-test.sh <arch>
set -e
export MSYS2_ARG_CONV_EXCL='*'
ARCH=$1
PK=D:/tgcalls/package/tgcalls-1c236c09-win-$ARCH
O=D:/tgcalls/scratch/pkgtest-$ARCH
rm -rf $O && mkdir -p $O
R=F:/cr/rt21-cr/bin
VC="D:/Program Files/vs22buildtools/VC/Tools/MSVC/14.44.35207"; SDK="D:/Windows Kits/10"
case $ARCH in
  arm32) T=thumbv7-windows-msvc; M=arm; L=arm; WANT=IMAGE_FILE_MACHINE_ARMNT ;;
  x86)   T=i686-pc-windows-msvc; M=x86; L=x86; WANT=IMAGE_FILE_MACHINE_I386 ;;
esac
DEFS="-DTGCALLS_USE_STD_OPTIONAL -DWEBRTC_APP_TDESKTOP -DRTC_ENABLE_H265 -DRTC_ENABLE_VP9 -DWEBRTC_ENABLE_PROTOBUF=0
 -DWEBRTC_INCLUDE_INTERNAL_AUDIO_DEVICE -DHAVE_SCTP -DWEBRTC_USE_H264 -DWEBRTC_USE_BUILTIN_ISAC_FLOAT -DWEBRTC_LIBRARY_IMPL
 -DWEBRTC_NON_STATIC_TRACE_EVENT_HANDLERS=1 -DWEBRTC_HAVE_DCSCTP -DWEBRTC_HAVE_SCTP -DABSL_ALLOCATOR_NOTHROW=1 -DWEBRTC_WIN
 -DRTC_ENABLE_WIN_WGC -DZLIB_WINAPI -DWIN32 -DWIN32_LEAN_AND_MEAN -D_WINDOWS -DNOMINMAX -DUNICODE -D_UNICODE -DNDEBUG"
$R/clang-cl.exe --target=$T -vctoolsdir "$VC" -winsdkdir "$SDK" -winsdkversion 10.0.26100.0 /nologo /MT /O2 /std:c++20 \
  /EHsc /permissive- /utf-8 /Zc:__cplusplus /W1 $DEFS -I$PK/include/tgcalls -I$PK/include/tg_owt \
  -I$PK/include/tg_owt/third_party/abseil-cpp -I$PK/include/tg_owt/third_party/libyuv/include \
  /c $PK/src/tgcalls_link_test.cpp /Fo$O/test.obj
$R/lld-link.exe /nologo /machine:$M /out:$O/tgcalls_link_test.exe $O/test.obj \
  /libpath:"$VC/lib/$L" /libpath:"$SDK/Lib/10.0.19041.0/ucrt/$L" /libpath:"$SDK/Lib/10.0.19041.0/um/$L" /libpath:$PK/lib \
  tgcalls.lib tg_owt.lib ssl.lib crypto.lib avformat.lib avcodec.lib swresample.lib swscale.lib avutil.lib opus.lib \
  vpx.lib openh264.lib jpeg-static.lib rnnoise.lib zlibstatic.lib secur32.lib d3d11.lib dxgi.lib dwmapi.lib shcore.lib \
  strmiids.lib winmm.lib ws2_32.lib iphlpapi.lib crypt32.lib bcrypt.lib ole32.lib oleaut32.lib uuid.lib user32.lib \
  gdi32.lib advapi32.lib shell32.lib shlwapi.lib version.lib /OPT:REF /INCREMENTAL:NO
if [ $ARCH = arm32 ]; then
  python -I D:/tgcalls/tools/check_imports.py $O/tgcalls_link_test.exe $WANT \
    F:/cr/arm-1/device-dlls/kernel32-exports.txt F:/cr/arm-1/device-dlls/ntdll-exports.txt
else
  python -I D:/tgcalls/tools/check_imports.py $O/tgcalls_link_test.exe $WANT
fi
