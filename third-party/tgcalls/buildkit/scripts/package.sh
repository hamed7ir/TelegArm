#!/usr/bin/env bash
# Assemble and zip one package: bash package.sh <arch>  ->  C:/handoff/tgcalls/tgcalls-1c236c09-win-<arch>.zip (+ .sha256)
set -e
ARCH=$1
NAME=tgcalls-1c236c09-win-$ARCH
STAGE=D:/tgcalls/package/$NAME
rm -rf "$STAGE"
python -I D:/tgcalls/tools/gen_buildinfo.py $ARCH
python -I D:/tgcalls/tools/package.py $ARCH "$STAGE"
mkdir -p /c/handoff/tgcalls
python -I -c "
import os, sys, zipfile, hashlib
stage, out = sys.argv[1], sys.argv[2]
base = os.path.dirname(stage)
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for root, _, files in os.walk(stage):
        for f in sorted(files):
            p = os.path.join(root, f)
            z.write(p, os.path.relpath(p, base))
h = hashlib.sha256(open(out, 'rb').read()).hexdigest()
open(out + '.sha256', 'w', newline='\n').write(h + '  ' + os.path.basename(out) + '\n')
print(out, os.path.getsize(out), h)
" "$STAGE" "C:/handoff/tgcalls/$NAME.zip"
