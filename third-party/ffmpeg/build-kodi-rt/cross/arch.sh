# kodi-rt: the target architecture of a build, for the stage scripts. Set ARCH before running a script (default
# arm32), and every script that sources this file builds for it:
#   arm32  Windows RT 8.1 on ARM32 (Thumb-2): the original target, unchanged (its files and folders keep their names)
#   x64    desktop Windows, 64-bit x86 (Kodi master's floor: Windows 8.1, _WIN32_WINNT_WINBLUE)
#   x86    desktop Windows, 32-bit x86 (the same floor)
# ARM64 is not possible on this server: rt2.4 has no AArch64 backend and the VS build tools have no ARM64 component.
# Variables (A_ = architecture):
#   A_TRIPLE    clang target         A_LIB      MSVC/SDK library folder   A_SDKLIB   SDK version of the import libs
#   A_MACHINE   lld-link -machine:   A_RT       toolchain root (F:/...)   A_RTP      the same as a /f/... path (PATH)
#   A_PFX       install prefix       A_TCFILE   CMake toolchain           A_MESON    meson cross file
#   A_HOST      autotools --host     A_DXARCH   Effects11's DIRECTX_ARCH  A_LIBASSARCH  libass's ARCH
#   A_SFX       suffix of build roots and logs ("" for arm32)
#   A_B3        Stage 3 build root: patched source copies in $A_B3/src, build dirs in $A_B3/<name>
#   A_LOG3      Stage 3 log prefix: $A_LOG3-<name>.log
#   A_ASM       yes when the SIMD assembly can be built (ARM32: NEON through clang's assembler; x64/x86: nasm, built
#               from Kodi's pinned nasm 2.15.05 by scripts/s9-nasm.sh into tools/nasm; without it, no x86 assembly)
#   A_NASM      nasm.exe for x64/x86 (F:/... path); A_NASMP the same as a /f/... folder for PATH
ARCH=${ARCH:-arm32}
case $ARCH in
  arm32) A_TRIPLE=armv7-pc-windows-msvc;  A_LIB=arm; A_SDKLIB=10.0.19041.0; A_MACHINE=arm; A_RT=F:/kodi-rt/tc/rt24b-cr
         A_HOST=armv7-w64-mingw32;  A_DXARCH=arm; A_LIBASSARCH=arm; A_SFX=""; A_ASM=yes ;;
  x64)   A_TRIPLE=x86_64-pc-windows-msvc; A_LIB=x64; A_SDKLIB=10.0.26100.0; A_MACHINE=x64; A_RT=F:/kodi-rt/tc/rt24d-cr
         A_HOST=x86_64-w64-mingw32; A_DXARCH=x64; A_LIBASSARCH=x86_64; A_SFX="-x64"; A_ASM=nasm ;;
  x86)   A_TRIPLE=i686-pc-windows-msvc;   A_LIB=x86; A_SDKLIB=10.0.26100.0; A_MACHINE=x86; A_RT=F:/kodi-rt/tc/rt24d-cr
         A_HOST=i686-w64-mingw32;   A_DXARCH=x86; A_LIBASSARCH=x86; A_SFX="-x86"; A_ASM=nasm ;;
  *) echo "ARCH must be arm32, x64 or x86 (got '$ARCH')"; exit 1 ;;
esac
A_RTP=/f${A_RT#F:}
A_NASM=F:/kodi-rt/tools/nasm/nasm.exe; A_NASMP=/f/kodi-rt/tools/nasm
if [ "$A_ASM" = nasm ]; then [ -x "$A_NASMP/nasm.exe" ] && A_ASM=yes || A_ASM=no; fi
A_PFX=F:/kodi-rt/prefix/$ARCH
A_TCFILE=F:/kodi-rt/cross/$ARCH-rt24.cmake
A_MESON=F:/kodi-rt/cross/meson-$ARCH.ini
A_B3=F:/kodi-rt/build/s3$A_SFX
A_LOG3=/f/kodi-rt/logs/s3$A_SFX
export ARCH
