# kodi-rt Stage 11: the "dumpbin /headers and /imports" checks TelegArm asked for, per .dll and .exe of a shared
# FFmpeg dist folder (build/s11/dist-<arch>-<variant>/bin). Writes <out>/<tag>-dumpbin.txt (dumpbin's full output for
# every file) and <out>/<tag>-summary.txt: machine, OS and subsystem version, the DLLs each file imports, the C runtime
# check (no vcruntime, ucrtbase, msvcp or api-ms-win-crt), the PDB check (the GUID and age in the file's debug
# directory against the PDB next to it) and, for ARM32, a count of NEON instructions in the disassembly.
# Exit 1 if a file fails a check.
# Usage: python -I scripts/s11-shared-checks.py <bin folder> <arm32|x86|x64> <out folder> <tag>
import os
import re
import struct
import subprocess
import sys
import uuid

DUMPBIN = r'D:\Program Files\vs22buildtools\VC\Tools\MSVC\14.44.35207\bin\Hostx64\x64\dumpbin.exe'
OBJDUMP = r'F:\kodi-rt\tc\rt24d-cr\bin\llvm-objdump.exe'
WANT = {'arm32': '1C4', 'x86': '14C', 'x64': '8664'}
CRT = re.compile(r'vcruntime|ucrtbase|msvcp\d|api-ms-win-crt-', re.I)

bindir, arch, out, tag = sys.argv[1:5]
os.makedirs(out, exist_ok=True)


def pdb_guid_age(path):
    """GUID and age from a PDB's info stream (stream 1) through the MSF 7.00 block map."""
    with open(path, 'rb') as f:
        data = f.read()
    if not data.startswith(b'Microsoft C/C++ MSF 7.00\r\n\x1aDS\x00\x00\x00'):
        raise ValueError('not an MSF 7.00 PDB')
    bs, _, _, ndir, _, bmap = struct.unpack_from('<6I', data, 32)
    nblk = (ndir + bs - 1) // bs
    dblocks = struct.unpack_from(f'<{nblk}I', data, bmap * bs)
    d = b''.join(data[b * bs:(b + 1) * bs] for b in dblocks)[:ndir]
    nstreams = struct.unpack_from('<I', d, 0)[0]
    sizes = struct.unpack_from(f'<{nstreams}I', d, 4)
    pos = 4 + 4 * nstreams
    blocks = []
    for s in sizes:
        n = 0 if s == 0xFFFFFFFF else (s + bs - 1) // bs
        blocks.append(struct.unpack_from(f'<{n}I', d, pos))
        pos += 4 * n
    s1 = b''.join(data[b * bs:(b + 1) * bs] for b in blocks[1])[:sizes[1]]
    _, _, age = struct.unpack_from('<3I', s1, 0)
    return str(uuid.UUID(bytes_le=s1[12:28])).upper(), age


def neon_count(path):
    p = subprocess.run([OBJDUMP, '-d', path], capture_output=True, text=True, errors='replace')
    n = 0
    for line in p.stdout.splitlines():
        m = re.match(r'\s*[0-9a-f]+:\s+(?:[0-9a-f]{2,4} )+\s*(v[a-z0-9]+(?:\.[a-z0-9]+)*)\s+(.*)', line)
        if not m:
            continue
        mn, ops = m.group(1), m.group(2)
        if (re.search(r'\bq\d+\b', ops) or re.search(r'\.(?:[isup](?:8|16|32|64)|8|16|32|64)$', mn)
                or (mn.endswith('.f32') and re.search(r'\bd\d+\b', ops))):
            n += 1
    return n


files = sorted(f for f in os.listdir(bindir) if f.lower().endswith(('.dll', '.exe')))
dump = open(os.path.join(out, f'{tag}-dumpbin.txt'), 'w', encoding='utf-8', newline='\r\n')
summary = []
bad = 0
for f in files:
    path = os.path.join(bindir, f)
    p = subprocess.run([DUMPBIN, '-nologo', '-headers', '-imports', path], capture_output=True, text=True,
                       errors='replace')
    text = p.stdout.replace(os.path.abspath(bindir) + os.sep, '').replace(bindir.replace('/', '\\') + '\\', '')
    dump.write(f'==== dumpbin /headers /imports {f}\n{text}\n')
    machine = re.search(r'^\s*([0-9A-F]+) machine', text, re.M).group(1)
    osv = re.search(r'^\s*([\d.]+) operating system version', text, re.M).group(1)
    ssv = re.search(r'^\s*([\d.]+) subsystem version', text, re.M).group(1)
    imports = []
    sect = text.split('Section contains the following imports:', 1)
    if len(sect) == 2:
        imports = re.findall(r'^\s{4}(\S+\.(?:dll|DLL))\s*$', sect[1], re.M)
    crt = [i for i in imports if CRT.search(i)]
    rsds = re.search(r'Format: RSDS, \{([0-9A-F-]+)\}, (\d+), (.+?)\s*$', text, re.M)
    probs = []
    if machine != WANT[arch]:
        probs.append(f'machine {machine}')
    for v in (osv, ssv):
        a, b = (int(x) for x in v.split('.'))
        if (a, b) > (6, 3):
            probs.append(f'version {v}')
    if crt:
        probs.append('CRT DLL ' + ' '.join(crt))
    pdbnote = 'no debug directory'
    if rsds:
        pdbname = rsds.group(3).replace('/', '\\').split('\\')[-1]
        pdbpath = os.path.join(bindir, pdbname)
        if os.path.exists(pdbpath):
            g, a = pdb_guid_age(pdbpath)
            if g == rsds.group(1) and a == int(rsds.group(2)):
                pdbnote = f'{pdbname} matches ({{{g}}}, age {a})'
            else:
                pdbnote = f'{pdbname} DOES NOT MATCH (file {rsds.group(1)}/{rsds.group(2)}, pdb {g}/{a})'
                probs.append('pdb mismatch')
        else:
            pdbnote = f'{pdbname} missing'
            probs.append('pdb missing')
    line = (f'{"OK  " if not probs else "FAIL"} {f}: machine {machine}, os {osv}, subsystem {ssv}, '
            f'imports {" ".join(imports)}; C runtime DLLs: {"none" if not crt else " ".join(crt)}; PDB: {pdbnote}')
    if arch == 'arm32':
        line += f'; NEON instructions: {neon_count(path)}'
    if probs:
        line += '  <- ' + ', '.join(probs)
        bad = 1
    summary.append(line)
dump.close()
with open(os.path.join(out, f'{tag}-summary.txt'), 'w', encoding='utf-8', newline='\r\n') as s:
    s.write('\n'.join(summary) + '\n')
    s.write(f'{tag}: {"PASS" if not bad else "FAIL"} ({len(files)} files)\n')
print('\n'.join(summary))
print(f'{tag}: {"PASS" if not bad else "FAIL"} ({len(files)} files)')
sys.exit(bad)
