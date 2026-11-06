"""Manual fixture generation: python3 generate.py /absolute/path/to/7zz.

7-Zip 26.03; no game data. Tests use the checked-in bytes, never this script.
"""
from pathlib import Path
import binascii
import random
import struct
import subprocess
import sys
import tempfile
import zipfile

OUT = Path(__file__).resolve().parent
SEVENZIP = str(Path(sys.argv[1]).resolve())
COMMANDS = []


def number(n):
    for i in range(8):
        if n < (1 << (7 + 7 * i)):
            return bytes([(0xff << (8 - i) & 0xff) | (n >> (8 * i))]) + n.to_bytes(8, 'little')[:i]
    return b'\xff' + n.to_bytes(8, 'little')


def crc(data):
    return struct.pack('<I', binascii.crc32(data))


def archive(header, packed):
    start = struct.pack('<QQ', len(packed), len(header)) + crc(header)
    return bytes.fromhex('377abcaf271c0004') + crc(start) + start + packed + header


def synthetic(names, payloads, declared=None, coder=b'\x01\x00', empty_flags=None, anti_flags=None, attributes=None):
    """Re-encode a small Copy archive header for names 7-Zip refuses to create.

    The physical payload is the same synthetic data used by the Copy seed command.
    No CRC is omitted; changes are visible to a conforming archive parser.
    """
    header = b'\x01'
    if payloads:
        header += b'\x04\x06\x00' + number(len(payloads)) + b'\x09'
        header += b''.join(number(len(p)) for p in payloads) + b'\x00\x07\x0b'
        header += number(len(payloads)) + b'\x00' + b''.join(b'\x01' + coder for p in payloads)
        header += b'\x0c' + b''.join(number(declared if declared is not None else len(p)) for p in payloads)
        header += b'\x0a\x01' + b''.join(crc(p) for p in payloads) + b'\x00\x08\x00\x00'
    header += b'\x05' + number(len(names))
    if empty_flags is not None:
        header += b'\x0e\x01' + bytes([empty_flags])
        header += b'\x0f\x01\x80'  # empty file, as opposed to directory
    if anti_flags is not None:
        header += b'\x10\x01' + bytes([anti_flags])
    if attributes is not None:
        value = b'\x01\x00' + b''.join(struct.pack('<I', a) for a in attributes)
        header += b'\x15' + number(len(value)) + value
    name_bytes = b'\x00' + b''.join(n.encode('utf-16le') + b'\x00\x00' for n in names)
    header += b'\x11' + number(len(name_bytes)) + name_bytes + b'\x00\x00'
    return archive(header, b''.join(payloads))


with tempfile.TemporaryDirectory(prefix='romd-7z-fixtures-') as work:
    root = Path(work)
    (root / 'tree').mkdir()
    (root / 'emptydir').mkdir()
    (root / 'a.rom').write_bytes(b'alpha')
    (root / 'b.rom').write_bytes(b'beta')
    (root / 'tree/nested.rom').write_bytes(b'nested')
    (root / 'empty.rom').write_bytes(b'')
    (root / 'large.rom').write_bytes(random.Random(1234).randbytes(160_000))

    def make(name, inputs, *options):
        dest = OUT / name
        dest.unlink(missing_ok=True)
        args = ['a', str(dest), *inputs, '-mtm=off', '-mta=off', '-mtc=off', '-bso0', '-bsp0', *options]
        subprocess.run([SEVENZIP, *args], cwd=root, check=True)
        COMMANDS.append('7zz ' + ' '.join(['a', name, *inputs, '-mtm=off', '-mta=off', '-mtc=off', '-bso0', '-bsp0', *options]))

    inputs = ['a.rom', 'b.rom', 'tree', 'empty.rom', 'emptydir']
    make('non-solid.7z', inputs, '-m0=LZMA2', '-md=1m', '-ms=off', '-mhc=off')
    make('solid.7z', inputs, '-m0=LZMA2', '-md=1m', '-ms=on')
    make('lzma.7z', inputs, '-m0=LZMA', '-md=1m', '-ms=on')
    make('large.7z', ['large.rom'], '-m0=LZMA2', '-md=1m', '-ms=off')
    make('encrypted.7z', ['a.rom'], '-m0=LZMA2', '-md=1m', '-pfixture', '-mhe=off', '-mhc=off')
    make('encrypted-headers.7z', ['a.rom'], '-m0=LZMA2', '-md=1m', '-pfixture', '-mhe=on')
    make('copy-seed.7z', ['a.rom', 'b.rom'], '-m0=Copy', '-ms=off', '-mhc=off')
    for old_volume in OUT.glob('split.7z.*'):
        old_volume.unlink()
    make('split.7z', ['large.rom'], '-m0=Copy', '-v64k', '-mhc=off')
    # Keep only the first volume: ROMD must never look for sibling volumes.
    for part in OUT.glob('split.7z.*'):
        if part.name != 'split.7z.001':
            part.unlink()
    with zipfile.ZipFile(root / 'inner.zip', 'w') as z:
        z.writestr('inside.rom', b'zip payload')
    make('zip-in-7z.7z', ['inner.zip'], '-m0=LZMA2', '-md=1m')
    with zipfile.ZipFile(OUT / '7z-in-zip.zip', 'w') as z:
        z.writestr('inner.7z', (OUT / 'solid.7z').read_bytes())

# Malformed/security variants derived from Copy seed data; 7-Zip intentionally
# refuses several of these names. Re-encode header lengths and both header CRCs.
for filename, name in [('traversal.7z', '../escape.rom'), ('absolute.7z', '/escape.rom'),
                       ('backslash.7z', '..\\escape.rom'), ('drive.7z', 'C:\\escape.rom'),
                       ('nul.7z', 'a\x00evil.rom'), ('overlong.7z', '/'.join(['a' * 120] * 9) + '/a.rom')]:
    (OUT / filename).write_bytes(synthetic([name], [b'alpha']))
(OUT / 'duplicate.7z').write_bytes(synthetic(['a.rom', 'a.rom'], [b'alpha', b'beta']))
(OUT / 'case-collision.7z').write_bytes(synthetic(['a.rom', 'A.rom'], [b'alpha', b'beta']))
(OUT / 'parent-collision.7z').write_bytes(synthetic(['a.rom', 'A.rom/b.rom'], [b'alpha', b'beta']))
(OUT / 'implicit-parents.7z').write_bytes(synthetic(['one/two/a.rom'], [b'alpha']))
(OUT / 'declared-size.7z').write_bytes(synthetic(['a.rom'], [b'alpha'], declared=1 << 40))
(OUT / 'oversized-dictionary.7z').write_bytes(synthetic(['a.rom'], [b'alpha'], coder=bytes.fromhex('21210128')))
(OUT / 'oversized-lzma.7z').write_bytes(synthetic(['a.rom'], [b'alpha'], coder=bytes.fromhex('23030101055d00000080')))
(OUT / 'ppmd.7z').write_bytes(synthetic(['a.rom'], [b'alpha'], coder=bytes.fromhex('23030401050600000080')))
(OUT / 'symlink.7z').write_bytes(synthetic(['a.rom'], [b'alpha'], attributes=[0xa1ff8000]))
(OUT / 'backslash-directory.7z').write_bytes(synthetic(['one\\two\\a.rom'], [b'alpha']))
(OUT / 'parent-case.7z').write_bytes(synthetic(['one/a.rom', 'ONE/b.rom'], [b'alpha', b'beta']))
(OUT / 'normalized-collision.7z').write_bytes(synthetic(['a.rom', 'one/../a.rom'], [b'alpha', b'beta']))
(OUT / 'overflow-declared.7z').write_bytes(synthetic(['a.rom', 'b.rom'], [b'alpha', b'beta'], declared=1 << 62))
(OUT / 'unsupported-coder.7z').write_bytes(synthetic(['a.rom'], [b'alpha'], coder=bytes.fromhex('01ff')))
(OUT / 'anti.7z').write_bytes(synthetic(['a.rom', 'gone.rom'], [b'alpha'], empty_flags=0x40, anti_flags=0x80))
(OUT / 'truncated.7z').write_bytes((OUT / 'solid.7z').read_bytes()[:-10])
corrupt = bytearray((OUT / 'large.7z').read_bytes())
corrupt[40] ^= 0x80
(OUT / 'corrupt.7z').write_bytes(corrupt)
(OUT / 'commands.txt').write_text('\n'.join(COMMANDS) + '\n')

base = (OUT / 'solid.7z').read_bytes()
offset, size = struct.unpack_from('<QQ', base, 12)
encoded = base[32 + offset:]
pattern = bytes.fromhex('2303010105')
position = encoded.index(pattern) + len(pattern)
hostile = bytearray(encoded)
hostile[position + 1:position + 5] = (1 << 31).to_bytes(4, 'little')
(OUT / 'oversized-header-dictionary.7z').write_bytes(archive(hostile, base[32:32 + offset]))
