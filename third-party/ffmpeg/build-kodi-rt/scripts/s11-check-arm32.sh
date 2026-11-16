#!/bin/bash
# kodi-rt Stage 11: the Windows RT 8.1 checks for the ARM32 shared FFmpeg (scripts/s11-ffmpeg-shared.sh), per .dll and
# .exe in build/s11/dist-arm32-<variant>/bin, as Kodi's gate does them (scripts/s4-gate.sh, check 9):
#   machine ARMNT; every imported function exported by the tablet's own DLL copies (results/stage0-run5/dlls;
#   VERSION/RPCRT4 from run 1) or by the FFmpeg DLLs next to it; API sets resolved through the tablet's
#   apisetschema.dll; no UCRT/VC runtime DLL (RT 8.1 has none: static CRT); operating system and subsystem version
#   6.3 or lower (llvm-readobj --file-headers); toolchain-0031 (no call whose return address leaves its .pdata
#   function). The log names each file's sha256.
# Usage (Git Bash): bash scripts/s11-check-arm32.sh <variant>      Output: logs/s11-check-arm32-<variant>.log;
#   last line "FFMPEG ARM32 CHECK: PASS"
set -u
export MSYS2_ARG_CONV_EXCL='*'
cd /f/kodi-rt || exit 1
V=${1:?usage: s11-check-arm32.sh <instr|use|plain>}
T=F:/kodi-rt/tc/rt24d-cr/bin
DEVDLLS=results/stage0-run5/dlls
BIN=build/s11/dist-arm32-$V/bin
LOG=logs/s11-check-arm32-$V.log
fail=0; n=0
{
  echo "== kodi-rt shared FFmpeg ARM32 checks ($V) $(date '+%F %T'): $BIN"
  for b in $(ls "$BIN"/*.dll "$BIN"/*.exe 2>/dev/null | sort); do
    n=$((n+1)); ok=1; why=""
    python -I scripts/check-machine.py --want=0x1c4 "$b" > /dev/null || { ok=0; why="$why machine"; }
    python -I scripts/check-imports.py "$T/llvm-readobj.exe" "$DEVDLLS;results/stage0-run1/dlls;$BIN" "$b" > logs/s11-check-imp.txt 2>&1 || { ok=0; why="$why imports"; }
    grep -qiE 'api-ms-win-crt-|ucrtbase|vcruntime|msvcp1' logs/s11-check-imp.txt && { ok=0; why="$why vc-runtime"; }
    if grep -q "api-ms-win-" logs/s11-check-imp.txt; then
      python -I scripts/apiset-resolve.py "$DEVDLLS/apisetschema.dll" --check "$T/llvm-readobj.exe" "$DEVDLLS" "$b" >> logs/s11-check-imp.txt 2>&1 || { ok=0; why="$why apisets"; }
    fi
    hdr=$("$T/llvm-readobj.exe" --file-headers "$b" 2>/dev/null)
    osv=$(echo "$hdr" | awk '/MajorOperatingSystemVersion/{a=$2} /MinorOperatingSystemVersion/{b=$2} END{print a"."b}')
    ssv=$(echo "$hdr" | awk '/MajorSubsystemVersion/{a=$2} /MinorSubsystemVersion/{b=$2} END{print a"."b}')
    for v in $osv $ssv; do awk -v v="$v" 'BEGIN{split(v,p,"."); exit !((p[1]<6)||(p[1]==6&&p[2]<=3))}' || { ok=0; why="$why version:$v"; }; done
    python -I scripts/pdata-calls.py "$T/llvm-objdump.exe" "$b" > logs/s11-check-pdata.txt 2>&1 || { ok=0; why="$why pdata-run"; }
    bad=$(grep -m1 "calls whose return address leaves their function:" logs/s11-check-pdata.txt | grep -oE ": [0-9]+" | grep -oE "[0-9]+")
    [ "${bad:-x}" = 0 ] || { ok=0; why="$why 0031:${bad:-?}"; }
    dlls=$(grep -E "^   [A-Za-z0-9_.-]+\.(dll|DLL): " logs/s11-check-imp.txt | grep -oE "^   [A-Za-z0-9_.-]+\.(dll|DLL)" | sed 's/^ *//' | sort -u | tr '\n' ' ')
    if [ $ok = 1 ]; then
      echo "OK    $(basename "$b")  $(stat -c %s "$b") bytes, os $osv, subsystem $ssv, sha256 $(sha256sum "$b" | cut -d' ' -f1) [imports: $dlls]"
    else
      echo "FAIL  $(basename "$b"):$why (os $osv, subsystem $ssv)"; cat logs/s11-check-imp.txt; tail -3 logs/s11-check-pdata.txt; fail=1
    fi
  done
  [ $fail = 0 ] && echo "FFMPEG ARM32 CHECK: PASS ($n files)" || echo "FFMPEG ARM32 CHECK: FAIL"
} > $LOG 2>&1
grep -E "^OK|^FAIL|FFMPEG ARM32 CHECK|^== " $LOG
exit $fail
