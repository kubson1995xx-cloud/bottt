"""Nonshipped independent ZIP readback. No writes until all members validate."""
import hashlib
import json
import pathlib
import re
import stat
import sys
import zipfile

archive, destination, evidence = map(pathlib.Path, sys.argv[1:])
assert not destination.exists(), 'Existing destination refused'
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
expected = '2ebcfa68834e8750831dc65877edbd07c5b1e0728e8250ba5e11868c5fc12aec'
assert sha(archive) == expected and archive.stat().st_size == 43206842, 'Wrong Task5 ZIP'
files = {}
seen = set()
total = 0
with zipfile.ZipFile(archive) as z:
    entries = z.infolist()
    assert 0 < len(entries) <= 10000
    for entry in entries:
        # Compress-Archive used Windows separators. Normalize only separators,
        # BEFORE all containment/collision checks, never traversal or other tokens.
        name = entry.filename.replace('\\', '/')
        parts = name.rstrip('/').split('/')
        assert name and not name.startswith('/') and ':' not in name
        assert all(p not in ('', '.', '..') and p.strip() == p and not p.endswith('.') for p in parts)
        assert all(not re.match(r'(?i)^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)', p) for p in parts)
        key = name.rstrip('/').casefold()
        assert key not in seen, 'Case/separator collision'
        seen.add(key)
        assert stat.S_IFMT(entry.external_attr >> 16) in (0, stat.S_IFDIR, stat.S_IFREG)
        assert not entry.flag_bits & 1
        assert entry.file_size <= 256*1024*1024
        assert entry.file_size <= max(1024*1024, entry.compress_size*500)
        total += entry.file_size
        assert total <= 512*1024*1024
        assert (destination / name).resolve().is_relative_to(destination.resolve())
        if not entry.is_dir():
            files[name] = entry
    keys = {f.casefold() for f in files}
    for name in files:
        parts = name.split('/')
        assert all('/'.join(parts[:i]).casefold() not in keys for i in range(1, len(parts)))
    assert z.testzip() is None, 'CRC failed'
    destination.mkdir()
    for name, entry in files.items():
        target = destination / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(z.read(entry))
    actual = {p.relative_to(destination).as_posix(): sha(p) for p in destination.rglob('*') if p.is_file()}
    assert set(actual) == set(files)
    for name, entry in files.items():
        assert actual[name] == hashlib.sha256(z.read(entry)).hexdigest()
manifest = {}
for line in (destination / 'manifest.sha256').read_text(encoding='utf-8-sig').splitlines():
    digest, name = line.split('  ', 1)
    assert re.fullmatch('[0-9a-f]{64}', digest) and name not in manifest
    manifest[name] = digest
assert manifest == {n: h for n, h in actual.items() if n != 'manifest.sha256'}
metadata = json.loads((destination / 'apex-build.json').read_text(encoding='utf-8-sig'))
assert metadata['sourceCommit'] == 'eed2fdf0bb4625260025e52a06e257c00fa38299'
evidence.write_text(json.dumps(dict(zipSha256=expected, zipBytes=archive.stat().st_size, crc='PASS', regularFiles=len(files), manifestEntries=len(manifest), productCommit=metadata['sourceCommit'], sourceInputsSha256=metadata['sourceInputsSha256'], extractedSha256=actual), indent=2))
print('Exact Task5 ZIP: CRC, paths, case, bytes, outer manifest, source identity PASS')
