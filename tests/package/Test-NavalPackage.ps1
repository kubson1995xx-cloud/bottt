#Requires -Version 5.1
<# Test tooling only; optional ZIP path performs independent bounded Python stdlib readback first. #>
param([Parameter(Mandatory=$true)][string]$Source,[Parameter(Mandatory=$true)][string]$Pakiet,[Parameter(Mandatory=$true)][string]$Evidence,[string]$Zip='')
$ErrorActionPreference='Stop'
$Source=(Resolve-Path -LiteralPath $Source).Path;$Pakiet=[IO.Path]::GetFullPath($Pakiet);$Evidence=[IO.Path]::GetFullPath($Evidence)
if (Test-Path -LiteralPath $Evidence) { throw 'Evidence directory must be new' }
if ($Evidence.StartsWith($Pakiet.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must be outside sealed package' }
New-Item -ItemType Directory -Path $Evidence|Out-Null
$oldRoot=$env:APEX_PACKAGE_ROOT;$oldDotnet=$env:DOTNET_HOST_PATH;$oldEvidence=$env:NAVAL_HOST_EVIDENCE_DIR;$oldWindowsEvidence=$env:APEX_WINDOWS_EVIDENCE
. (Join-Path $Source 'tools/windows/TestEvidence.ps1')
function Inventory {
    $prefix=$Pakiet.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    return @((Get-ChildItem -LiteralPath $Pakiet -Force -Recurse -File|Sort-Object FullName|ForEach-Object { $_.FullName.Substring($prefix.Length).Replace('\','/')+' '+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }))
}
try {
    if ($Zip) {
        if (Test-Path -LiteralPath $Pakiet) { throw 'Extraction destination must not exist' }
        $python=(Get-Command python3,python -ErrorAction SilentlyContinue|Select-Object -First 1).Source
        if (-not $python) { throw 'Python stdlib required for independent archive gate (builder/test tooling only)' }
        $script=@'
import sys,pathlib,zipfile,stat,hashlib,json,re,os
archive=pathlib.Path(sys.argv[1]);dest=pathlib.Path(sys.argv[2]);report=pathlib.Path(sys.argv[3])
assert not dest.exists(), 'nonempty/existing extraction target refused'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
ziphash=sha(archive)
sidecar=archive.with_name(archive.name+'.sha256')
assert re.fullmatch(r'[0-9a-f]{64}',sidecar.read_text().split()[0]) and sidecar.read_text().split()[0]==ziphash,'ZIP SHA sidecar mismatch'
with zipfile.ZipFile(archive) as z:
    entries=z.infolist();assert 0<len(entries)<=10000,'member count limit'
    seen=set();files={};total=0
    for e in entries:
        name=e.filename;parts=name.rstrip('/').split('/')
        assert name and '\\' not in name and not name.startswith('/') and ':' not in name and all(p not in ('','.','..') and p.strip()==p and not p.endswith('.') for p in parts),'unsafe archive path'
        key=name.rstrip('/').casefold();assert key not in seen,'duplicate/case-collision';seen.add(key)
        mode=e.external_attr>>16;assert stat.S_IFMT(mode) in (0,stat.S_IFREG,stat.S_IFDIR),'link/special archive member'
        assert not e.flag_bits&1,'encrypted member'
        assert e.file_size<=256*1024*1024 and e.file_size<=max(1024*1024,e.compress_size*500),'decompression limit'
        total+=e.file_size;assert total<=512*1024*1024,'total decompression limit'
        target=(dest/name).resolve();assert target.is_relative_to(dest.resolve()),'containment'
        if not e.is_dir():files[name]=e
    for name in files:
        parts=name.split('/')
        assert all('/'.join(parts[:i]).casefold() not in {f.casefold() for f in files} for i in range(1,len(parts))),'file/parent collision'
    assert z.testzip() is None,'CRC/decompression failure'
    # Only after validating ALL members and CRC is any member written.
    dest.mkdir(mode=0o700)
    for name,e in files.items():
        target=dest/name;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(z.read(e))
    extracted={str(p.relative_to(dest)).replace(os.sep,'/'):sha(p) for p in dest.rglob('*') if p.is_file()}
    assert set(extracted)==set(files),'extracted member set mismatch'
    for name,e in files.items():assert extracted[name]==hashlib.sha256(z.read(e)).hexdigest(),'ZIP/readback bytes mismatch'
    original=pathlib.Path(str(archive)[:-4])
    originalParity=None
    if original.is_dir():
        originalMap={str(p.relative_to(original)).replace(os.sep,'/'):sha(p) for p in original.rglob('*') if p.is_file()}
        assert originalMap==extracted,'source-package/extracted parity';originalParity=True
    listed={}
    for line in (dest/'manifest.sha256').read_text(encoding='utf-8-sig').splitlines():
        h,name=line.split('  ',1);assert re.fullmatch('[0-9a-f]{64}',h) and name not in listed,'invalid manifest';listed[name]=h
    assert listed=={n:h for n,h in extracted.items() if n!='manifest.sha256'},'outer manifest readback mismatch'
    result={'zip':str(archive),'zipBytes':archive.stat().st_size,'zipSha256':ziphash,'crc':'PASS: testzip returned None','members':len(entries),'regularFiles':len(files),'manifestEntries':len(listed),'uncompressedBytes':total,'originalPackageParity':originalParity,'extractedSha256':extracted,'safePathChecks':'all before extraction'}
    report.write_text(json.dumps(result,indent=2)+'\n');print(json.dumps({k:v for k,v in result.items() if k!='extractedSha256'}))
'@
        $scriptPath=Join-Path $Evidence 'archive-readback.py';[IO.File]::WriteAllText($scriptPath,$script,[Text.UTF8Encoding]::new($false))
        & $python $scriptPath $Zip $Pakiet (Join-Path $Evidence 'archive-readback.json')
        if ($LASTEXITCODE -ne 0) { throw 'Independent archive verification failed' }
    }
    & (Join-Path $Pakiet 'Sprawdz-Pakiet.ps1') -Pakiet $Pakiet
    $meta=Get-Content (Join-Path $Pakiet 'apex-build.json') -Raw|ConvertFrom-Json
    $expected='linux-x64';if ($env:OS -eq 'Windows_NT') { $expected='win-x64' }
    if ($meta.target -cne $expected) { throw 'Packaged target does not match native test OS' }
    $dotnet=(Get-Command dotnet -ErrorAction Stop).Source
    $env:APEX_PACKAGE_ROOT=$Pakiet;$env:DOTNET_HOST_PATH=$dotnet;$env:NAVAL_HOST_EVIDENCE_DIR=Join-Path $Evidence 'host'
    $before=Inventory;[IO.File]::WriteAllLines((Join-Path $Evidence 'package-before.sha256'),$before)
    # Existing synthetic production-host tests only. No repository DLL may be used by NavalProductionHost.
    $filter='FullyQualifiedName~NavalReadAccessTests|FullyQualifiedName~NavalReadEndpointTests|FullyQualifiedName~NavalFrontendHostTests'
    $args=@('test',(Join-Path $Source 'tests/Apex.Tests/Apex.Tests.csproj'),'-c','Release','--no-build','--no-restore','--filter',$filter,'--logger','trx;LogFileName=packaged.trx','--results-directory',$Evidence)
    [IO.File]::WriteAllText((Join-Path $Evidence 'command.json'),(@{executable=$dotnet;argv=$args;packageRoot=$Pakiet;target=$expected}|ConvertTo-Json -Depth 5))
    & $dotnet @args 2>&1 | Tee-Object -FilePath (Join-Path $Evidence 'test-output.txt') | Write-Host
    $exit=$LASTEXITCODE
    if ($exit -ne 0) { throw "Real packaged host tests failed ($exit)" }
    $counts=Read-ApexTestCounts (Join-Path $Evidence 'packaged.trx')
    $native=[ordered]@{};$nativeFailed=$false
    if ($expected -eq 'win-x64') {
        $env:APEX_WINDOWS_EVIDENCE=$Evidence
        foreach ($gate in @('Package','Browser')) {
            $trx='windows-'+$gate.ToLowerInvariant()+'.trx'
            & $dotnet test (Join-Path $Source 'tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj') -c Release --no-build --no-restore --filter ('Gate='+$gate) --logger ('trx;LogFileName='+$trx) --results-directory $Evidence
            $native[$gate]=@{trx=$trx;exitCode=$LASTEXITCODE}
            if ($LASTEXITCODE -ne 0) { $nativeFailed=$true } else { $native[$gate].counts=Read-ApexTestCounts (Join-Path $Evidence $trx) }
        }
        $owned=@(Get-ChildItem -LiteralPath $Evidence -Filter 'owned-*.json'|ForEach-Object {Get-Content $_.FullName -Raw|ConvertFrom-Json})
        $exits=@(Get-ChildItem -LiteralPath $Evidence -Filter 'exit-*.json'|ForEach-Object {Get-Content $_.FullName -Raw|ConvertFrom-Json})
        [IO.File]::WriteAllText((Join-Path $Evidence 'windows-process.json'),(@{owned=$owned;exits=$exits}|ConvertTo-Json -Depth 10))
    }
    $after=Inventory;[IO.File]::WriteAllLines((Join-Path $Evidence 'package-after.sha256'),$after)
    if (($before -join "`n") -cne ($after -join "`n")) { throw 'Package mutated during tests' }
    & (Join-Path $Pakiet 'Sprawdz-Pakiet.ps1') -Pakiet $Pakiet
    $proof=@(Get-ChildItem -LiteralPath (Join-Path $Evidence 'host') -Filter 'process-*.json'|ForEach-Object {Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json})
    if ($proof.Count -eq 0 -or @($proof|Where-Object {-not $_.packaged -or $_.desktop -cne (Join-Path $Pakiet 'APEX-Desktop')}).Count -gt 0) { throw 'Missing actual package process proof' }
    foreach ($p in $proof) { if (Get-Process -Id $p.pid -ErrorAction SilentlyContinue) { throw 'Owned host still running after tests' } }
    [IO.File]::WriteAllText((Join-Path $Evidence 'summary.json'),(@{status=$(if($nativeFailed){'FAIL'}else{'PASS'});nativeGates=$native;evidenceFiles=@('windows-package.trx','windows-browser.trx','packaged.trx','archive-readback.json','package-before.sha256','package-after.sha256','windows-browser.json','windows-process.json');testCounts=$counts;packageUnchanged=$true;processCount=$proof.Count;target=$expected;zipSha256= $(if ($Zip) {(Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLowerInvariant()} else {$null});windowsNativeAclTested=($expected -eq 'win-x64');brokerTested=$false}|ConvertTo-Json -Depth 5))
    if ($nativeFailed) { throw 'Native Package/Browser gate failed; see individual TRX' }
    Write-Host "EXTRACTED PACKAGE TESTS PASS: $($counts.total) actual tests; $($proof.Count) packaged processes exited; package unchanged."
} finally {
    # Test harness owns/kills each production child in DisposeAsync. On interrupted tests kill only recorded owned PIDs whose payload path still matches.
    if (Test-Path -LiteralPath (Join-Path $Evidence 'host')) {
        foreach ($f in Get-ChildItem -LiteralPath (Join-Path $Evidence 'host') -Filter 'process-*.json') {
            $p=Get-Content -LiteralPath $f.FullName -Raw|ConvertFrom-Json
            if ($env:OS -eq 'Windows_NT') { $live=Get-CimInstance Win32_Process -Filter ("ProcessId="+$p.pid) -ErrorAction SilentlyContinue; if ($live -and $live.CommandLine.Contains((Join-Path $Pakiet 'APEX-Desktop'))) { Stop-Process -Id $p.pid -Force } }
            elseif (Test-Path -LiteralPath ('/proc/'+$p.pid+'/cmdline')) { $cmd=[IO.File]::ReadAllText(('/proc/'+$p.pid+'/cmdline'));if ($cmd.Contains((Join-Path $Pakiet 'APEX-Desktop/ApexDesktop.dll'))) { Stop-Process -Id $p.pid -Force } }
        }
    }
    $env:APEX_PACKAGE_ROOT=$oldRoot;$env:DOTNET_HOST_PATH=$oldDotnet;$env:NAVAL_HOST_EVIDENCE_DIR=$oldEvidence;$env:APEX_WINDOWS_EVIDENCE=$oldWindowsEvidence
}
