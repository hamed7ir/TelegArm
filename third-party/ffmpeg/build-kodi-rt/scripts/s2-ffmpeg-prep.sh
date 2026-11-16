#!/bin/bash
# kodi-rt Stage 2: a patched FFmpeg 9.0.2 source tree, the way Kodi's Windows build patches it.
# tools/buildsteps/windows/buildhelpers.sh:139 applies tools/depends/target/ffmpeg/*-ffmpeg-windows-*.patch, then
# *-ffmpeg-all-*.patch, each with "patch -p1 -N -r -". 002-ffmpeg-libavutil-common-h-cpp11-constant-macros.patch
# matches neither pattern, so Kodi's Windows build does not apply it, and neither does this.
# Source: src/deps/ffmpeg-9.0.2 (extracted from the hash-checked tarball, left pristine). Output: build/s2/ffmpeg-9.0.2-kodi.
# Run from Git Bash (it has GNU patch; MSYS2 here doesn't): bash scripts/s2-ffmpeg-prep.sh
set -u
P=/f/kodi-rt/src/xbmc/tools/depends/target/ffmpeg
S=/f/kodi-rt/src/deps/ffmpeg-9.0.2
D=/f/kodi-rt/build/s2/ffmpeg-9.0.2-kodi
[ -d "$S" ] || { echo "no source at $S"; exit 1; }
rm -rf "$D"; mkdir -p "$(dirname "$D")"; cp -r "$S" "$D" || exit 1
n=0
for p in "$P"/*-ffmpeg-windows-*.patch "$P"/*-ffmpeg-all-*.patch; do
  [ -f "$p" ] || continue
  echo "== $(basename "$p")"
  patch -p1 -d "$D" -i "$p" -N -r - > "/tmp/s2-patch.out" 2>&1; rc=$?
  cat /tmp/s2-patch.out | sed 's/^/   /' | head -40
  [ $rc -eq 0 ] || { echo "PATCH FAILED: $(basename "$p") (exit $rc)"; exit 1; }
  grep -qiE 'FAILED|reject|Reversed' /tmp/s2-patch.out && { echo "PATCH NOT CLEAN: $(basename "$p")"; exit 1; }
  n=$((n+1))
done
echo "applied $n patches to $D"
# kodi-rt's own FFmpeg patches (Stage 5: 0001 = NEON for 10-bit HEVC motion compensation on 32-bit ARM)
m=0
for p in /f/kodi-rt/patches-ffmpeg/[0-9][0-9][0-9][0-9]-*.patch; do
  [ -f "$p" ] || continue
  echo "== $(basename "$p")"
  patch -p1 -d "$D" -i "$p" -N -r - > "/tmp/s2-patch.out" 2>&1; rc=$?
  cat /tmp/s2-patch.out | sed 's/^/   /' | head -40
  [ $rc -eq 0 ] || { echo "PATCH FAILED: $(basename "$p") (exit $rc)"; exit 1; }
  grep -qiE 'FAILED|reject|Reversed' /tmp/s2-patch.out && { echo "PATCH NOT CLEAN: $(basename "$p")"; exit 1; }
  m=$((m+1))
done
echo "applied $m kodi-rt patches to $D"
echo "PREP DONE"
