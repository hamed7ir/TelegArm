"""Check a PE's machine type and its kernel32/ntdll imports against a device's export lists.

Usage: python -I check_imports.py <exe> <expected-machine> [<kernel32-exports.txt> <ntdll-exports.txt>]
  expected-machine: IMAGE_FILE_MACHINE_ARMNT or IMAGE_FILE_MACHINE_I386
Export lists are `llvm-readobj --coff-exports` dumps (F:/cr/arm-1/device-dlls/*-exports.txt, Windows 10 15035 ARM32).
Prints every imported DLL with its symbol count, then the kernel32/ntdll imports missing from the device lists.
Exit 0 = machine matches and nothing missing; 1 = otherwise. A reader self-test runs first: a broken parse must not
look like "no imports, nothing missing".
"""
import re
import subprocess
import sys

READOBJ = 'F:/cr/rt21-cr/bin/llvm-readobj.exe'


def readobj(*args):
    return subprocess.run([READOBJ, *args], capture_output=True, text=True, check=True).stdout


def parse_imports(text):
    """{dll_lower: set(symbols)}; ordinal imports recorded as '#<n>'."""
    imports = {}
    cur = None
    for line in text.splitlines():
        s = line.strip()
        m = re.match(r'Name: (\S+)$', s)
        if m and (s.endswith('.dll') or s.endswith('.DLL') or '.' in m.group(1)) and cur is None:
            cur = imports.setdefault(m.group(1).lower(), set())
            continue
        m = re.match(r'Symbol: (\S*) \((\d+)\)', s)
        if m and cur is not None:
            cur.add(m.group(1) if m.group(1) else '#' + m.group(2))
            continue
        if s == '}':
            cur = None
    return imports


def parse_exports(path):
    names = set()
    for line in open(path, encoding='utf-8', errors='replace'):
        m = re.match(r'\s*Name: (\S+)\s*$', line)
        if m:
            names.add(m.group(1))
    return names


def main():
    exe, want = sys.argv[1], sys.argv[2]
    k32 = sys.argv[3] if len(sys.argv) > 3 else None
    ntd = sys.argv[4] if len(sys.argv) > 4 else None

    hdr = readobj('--file-headers', exe)
    m = re.search(r'Machine: (\S+)', hdr)
    machine = m.group(1) if m else '<none>'
    print('machine:', machine, 'OK' if machine == want else 'MISMATCH (want %s)' % want)
    ok = machine == want

    imports = parse_imports(readobj('--coff-imports', exe))
    total = sum(len(v) for v in imports.values())
    # self-test: any real Windows exe imports kernel32 with a few dozen symbols
    if 'kernel32.dll' not in imports or len(imports['kernel32.dll']) < 10:
        print('READER SELF-TEST FAILED: kernel32.dll imports not parsed:', {k: len(v) for k, v in imports.items()})
        return 1
    print('imported DLLs: %d, symbols: %d' % (len(imports), total))
    for dll in sorted(imports):
        print('  %-40s %4d' % (dll, len(imports[dll])))

    if k32 and ntd:
        for dll, path in (('kernel32.dll', k32), ('ntdll.dll', ntd)):
            exports = parse_exports(path)
            if len(exports) < 500:
                print('EXPORT LIST SELF-TEST FAILED:', path, len(exports))
                return 1
            used = imports.get(dll, set())
            missing = sorted(s for s in used if s not in exports)
            print('%s: %d imported, %d in device list (%d exports), missing %d%s' % (
                dll, len(used), len(used) - len(missing), len(exports), len(missing),
                ': ' + ' '.join(missing) if missing else ''))
            ok = ok and not missing
        api_sets = sorted(d for d in imports if d.startswith(('api-ms-', 'ext-ms-')))
        if api_sets:
            print('API-set imports (resolve via the OS apiset schema, check on device):', ' '.join(api_sets))
    print('RESULT:', 'PASS' if ok else 'FAIL')
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
