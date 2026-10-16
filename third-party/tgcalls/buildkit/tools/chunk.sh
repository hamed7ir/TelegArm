#!/usr/bin/env bash
# Run one build command for at most LIMIT seconds at BelowNormal priority, then stop it (ninja resumes on the
# next call). Meant for the harness's run_in_background (which stops a shell at ~30 min), so LIMIT <= 1500.
# This shell lowers ITS OWN priority class first; every process it starts afterwards (cmake, ninja, clang-cl)
# inherits BelowNormal. Stops early if F: or D: drops below 20 GB free. Kills only the PID tree it started.
# Usage: bash chunk.sh <name> <limit-seconds> <command> [args...]
# Log: D:/tgcalls/logs/<name>.log (appended); result line: D:/tgcalls/logs/<name>.result
NAME=${1:?name}; LIMIT=${2:?limit}; shift 2
L=/d/tgcalls/logs; mkdir -p $L
LOG=$L/$NAME.log; RES=$L/$NAME.result
MYWP=$(cat /proc/$$/winpid)
powershell -NoProfile -Command "(Get-Process -Id $MYWP).PriorityClass = 'BelowNormal'; (Get-Process -Id $MYWP).PriorityClass" > $L/$NAME.prio 2>&1
t0=$(date +%s)
echo "=== $NAME start $(date '+%F %T') limit ${LIMIT}s prio=$(tr -d '\r\n' < $L/$NAME.prio): $*" >> $LOG
"$@" >> $LOG 2>&1 &
NP=$!
WP=$(cat /proc/$NP/winpid 2>/dev/null)
echo $WP > $L/$NAME.pid
how=FINISHED
while kill -0 $NP 2>/dev/null; do
  for d in /f /d; do
    free=$(df -BG $d | awk 'NR==2{gsub("G","",$4); print $4}')
    if [ -n "$free" ] && [ "$free" -lt 20 ]; then
      echo "GUARD: $d free ${free} GB < 20 at $(date '+%F %T')" >> $LOG
      taskkill /F /T /PID $WP > /dev/null 2>&1 || taskkill //F //T //PID $WP > /dev/null 2>&1; how=DISK-GUARD; break 2
    fi
  done
  if [ $(( $(date +%s) - t0 )) -ge $LIMIT ]; then
    taskkill /F /T /PID $WP > /dev/null 2>&1 || taskkill //F //T //PID $WP > /dev/null 2>&1; how=CHUNK-LIMIT; break
  fi
  sleep 10
done
wait $NP; rc=$?
echo "=== $NAME end $(date '+%F %T') $how rc=$rc wall=$(( $(date +%s)-t0 ))s" >> $LOG
echo "$NAME: $how rc=$rc wall=$(( $(date +%s)-t0 ))s last=$(grep -oE '^\[[0-9]+/[0-9]+\]' $LOG | tail -1) end $(date '+%F %T')" > $RES
cat $RES
