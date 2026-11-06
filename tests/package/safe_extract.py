"""Nonshipped bounded archive readback; hash must be explicitly pinned."""
import hashlib, json, pathlib, re, stat, sys, zipfile
archive, destination, evidence = map(pathlib.Path, sys.argv[1:4])
expected = sys.argv[4]
advisory = len(sys.argv)==6 and sys.argv[5]=='--advisory-resources'
assert len(sys.argv)==5 or advisory
assert re.fullmatch('[0-9a-f]{64}', expected)
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
assert sha(archive) == expected
assert not destination.exists()
files, seen, total = {}, set(), 0
with zipfile.ZipFile(archive) as z:
    assert 0 < len(z.infolist()) <= (50000 if advisory else 10000)
    for entry in z.infolist():
        name = entry.filename.replace('\\', '/')
        parts = name.rstrip('/').split('/')
        assert name and not name.startswith('/') and ':' not in name
        assert all(p not in ('', '.', '..') and p.strip() == p and not p.endswith('.') for p in parts)
        assert all(not re.match(r'(?i)^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)', p) for p in parts)
        key = name.rstrip('/').casefold()
        assert key not in seen
        seen.add(key)
        assert stat.S_IFMT(entry.external_attr >> 16) in (0, stat.S_IFDIR, stat.S_IFREG)
        assert not entry.flag_bits & 1
        assert entry.file_size <= 256*1024*1024 and entry.file_size <= max(1024*1024, entry.compress_size*500)
        total += entry.file_size
        assert total <= (1536 if advisory else 512)*1024*1024
        assert (destination/name).resolve().is_relative_to(destination.resolve())
        if not entry.is_dir(): files[name] = entry
    keys = {f.casefold() for f in files}
    for name in files:
        parts = name.split('/')
        assert all('/'.join(parts[:i]).casefold() not in keys for i in range(1, len(parts)))
    assert z.testzip() is None
    if advisory:
        bundle=json.loads(z.read('AI/bundle.json'))
        assert bundle['Schema']=='apex-advisory-bundle-v1' and isinstance(bundle['Files'],list)
    destination.mkdir()
    for name, entry in files.items():
        target = destination/name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(z.read(entry))
    actual = {p.relative_to(destination).as_posix(): sha(p) for p in destination.rglob('*') if p.is_file()}
    assert set(actual) == set(files)
    for name, entry in files.items(): assert actual[name] == hashlib.sha256(z.read(entry)).hexdigest()
manifest = {}
for line in (destination/'manifest.sha256').read_text(encoding='utf-8-sig').splitlines():
    digest, name = line.split('  ', 1)
    assert re.fullmatch('[0-9a-f]{64}', digest) and name not in manifest
    manifest[name] = digest
assert manifest == {n:h for n,h in actual.items() if n != 'manifest.sha256'}
metadata = json.loads((destination/'apex-build.json').read_text(encoding='utf-8-sig'))
evidence.write_text(json.dumps(dict(zipSha256=expected, crc='PASS', regularFiles=len(files), productCommit=metadata['sourceCommit'], extractedSha256=actual), indent=2))
print('Pinned archive safe extraction, CRC and exact manifest PASS')
