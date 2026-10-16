"""Write D:/tgcalls/package/BUILD-INFO-<arch>.txt (consumer notes for one package). Usage: python -I gen_buildinfo.py <arch>"""
import os
import sys

arch = sys.argv[1]
machine = {'arm32': 'ARMNT (thumbv7, IMAGE_FILE_MACHINE_ARMNT 0x1C4)', 'x86': 'I386 (i686, IMAGE_FILE_MACHINE_I386 0x14C)'}[arch]
simd = {
    'arm32': 'NEON: tg_owt (WEBRTC_HAS_NEON, libyuv NEON, pffft), opus (presumed NEON intrinsics), libvpx (intrinsics + '
             'asm, no runtime detection), FFmpeg (asm + intrinsics, HAVE_VFP_ARGS=1). No SIMD: mozjpeg, openh264, BoringSSL.',
    'x86': 'SSE2 baseline (clang i686 default pentium4). Runtime-dispatched SSE4.1/AVX2: tg_owt (sse2/avx2 sublibs), opus, '
           'libvpx (NASM asm), FFmpeg (NASM x86asm), mozjpeg (NASM), BoringSSL (NASM asm). No SIMD: openh264.',
}[arch]
text = f"""tgcalls + tg_owt static libraries for Windows {arch} - {machine}
=====================================================================================
Built 2026-10-15 on the Venetium build server (side session of the v1.2 build). Full write-up, exact commands and
patches: C:\\handoff\\TGCALLS-ARM32.md and C:\\handoff\\TGCALLS-ARM32-patches\\.

Sources (pins of Telegram Desktop v7.3.1, commit 6fed91ffab9861771a75f29df65031b3c80b6941)
  tgcalls   TelegramMessenger/tgcalls  1c236c09f8d8569fead14bd68000618a52051225
  tg_owt    desktop-app/tg_owt        d1cf250ea73de26c4c1f0a3c8173eb2648efbb04 (WebRTC M123) + patches 0001-0003
            abseil d7aaad83 (20240116.2), crc32c 21fc8ef3, libsrtp a566a9cf (v2.5), libyuv 04821d1e (+ patch 0002)
  BoringSSL a0cb538b0d0d08371d3bd5712bbc8d474d28090a (hamed7ir/adb-rt's pin; replaces tdesktop's OpenSSL 3) + patch
  FFmpeg    8.1.2 release tarball, sha256 464beb5e7bf0c311e68b45ae2f04e9cc2af88851abb4082231742a74d97b524c
            (hamed7ir/ffmpeg-rt harness 60d58463; tdesktop pins n8.1.3)
  opus v1.5.2 ddbe4838, libvpx v1.14.1 12f3a2ac, openh264 v2.6.0 652bdb77, mozjpeg v4.1.5 6c9f0897,
  zlib e3dc0a85 (ZLIB_WINAPI), rnnoise desktop-app d8ea2b0e

Toolchain
  rt2.1 clang-cl "Varan clang version 23.1.1-rt2.1 (e240ad76)", sha256 d572c493...; lld-link sha256 6e203038...
  MSVC 14.44.35207 headers + libs, static CRT (/MT: libcmt, libcpmt, libucrt, libvcruntime)
  Windows SDK headers 10.0.26100.0, import libraries 10.0.19041.0. Release (-O2, NDEBUG). C++20, /EHsc, RTTI on.
  SIMD: {simd}

Using the libraries (ABI: a consumer TU that includes tgcalls/tg_owt headers MUST use the same settings)
  /MT, /std:c++20, /EHsc, RTTI on, NDEBUG, and these defines:
    TGCALLS_USE_STD_OPTIONAL WEBRTC_APP_TDESKTOP RTC_ENABLE_H265 RTC_ENABLE_VP9 WEBRTC_ENABLE_PROTOBUF=0
    WEBRTC_INCLUDE_INTERNAL_AUDIO_DEVICE HAVE_SCTP WEBRTC_USE_H264 WEBRTC_USE_BUILTIN_ISAC_FLOAT WEBRTC_LIBRARY_IMPL
    WEBRTC_NON_STATIC_TRACE_EVENT_HANDLERS=1 WEBRTC_HAVE_DCSCTP WEBRTC_HAVE_SCTP ABSL_ALLOCATOR_NOTHROW=1 WEBRTC_WIN
    RTC_ENABLE_WIN_WGC ZLIB_WINAPI WIN32 WIN32_LEAN_AND_MEAN _WINDOWS NOMINMAX UNICODE _UNICODE
  Include dirs: include/tgcalls  include/tg_owt  include/tg_owt/third_party/abseil-cpp
                include/tg_owt/third_party/libyuv/include
  Like tdesktop, include only the public headers (tgcalls/Instance.h, tgcalls/group/GroupInstanceCustomImpl.h, ...)
  and forward-declare InstanceImpl / InstanceV2Impl / InstanceV2ReferenceImpl for tgcalls::Register<> (see
  src/tgcalls_link_test.cpp).
  Link: tgcalls.lib tg_owt.lib ssl.lib crypto.lib avformat.lib avcodec.lib swresample.lib swscale.lib avutil.lib
        opus.lib vpx.lib openh264.lib jpeg-static.lib rnnoise.lib zlibstatic.lib
        secur32 d3d11 dxgi dwmapi shcore strmiids winmm ws2_32 iphlpapi crypt32 bcrypt ole32 oleaut32 uuid user32
        gdi32 advapi32 shell32 shlwapi version (+ avrt via #pragma in tg_owt)
"""
if arch == 'arm32':
    text += """  ARM32: link with the fixed rt2.1 lld-link (sha256 6e203038...; lld B5/B7 branch fixes). MSVC link.exe untested.
  ARM32: objects use rt2.1's ARM32 C++ EH funclets (/EHsc); not yet exercised on a device.
"""
text += """
Not included / caller's job
  Call WSAStartup(MAKEWORD(2,2), ...) before creating any tgcalls instance. WebRTC's socket server does not (tdesktop
  gets it from Qt). Without it every UDP socket fails ("UDP socket creation failed") and no ICE candidate is gathered:
  measured with the x86 link test on the build server (8 failures/0 candidates -> 0 failures/8 candidates).
  No audio device: tg_owt is built like tdesktop's (TG_OWT_BUILD_AUDIO_BACKENDS=OFF, WEBRTC_DUMMY_AUDIO_BUILD). Pass a
  real AudioDeviceModule via Descriptor::createAudioDeviceModule (tdesktop uses its own OpenAL-based ADM).
  rnnoise.lib's objects carry dllexport (desktop-app/rnnoise builds with DLL_EXPORT, as in tdesktop), so any exe/dll
  linking it exports rnnoise_*.

bin/tgcalls_link_test.exe: plain run prints the registered protocol versions; "--construct" creates a 1:1 instance
(numerically newest version, 13.0.0) and a group instance on a fake audio device and stops them (device smoke test,
no network peer). x86 result on the build server: versions 2.7.7 5.0.0 7.0.0 8.0.0 9.0.0 10.0.0 11.0.0 12.0.0 13.0.0,
max layer 92, p2p created/signaling/stopped, group join payload received/stopped, 8 host ICE candidates.
"""
os.makedirs('D:/tgcalls/package', exist_ok=True)
with open(f'D:/tgcalls/package/BUILD-INFO-{arch}.txt', 'w', encoding='utf-8', newline='\r\n') as f:
    f.write(text)
print('ok', arch)
