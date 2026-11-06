param([ValidateSet('red','green')][string]$Phase='green',[Parameter(Mandatory=$true)][string]$Prior)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$root=Join-Path $env:RUNNER_TEMP 'windows-advisory-refresh'
if(Test-Path $root){throw 'immutable evidence root already exists'}
foreach($part in @('source','build','closure','evidence')){New-Item -ItemType Directory -Path (Join-Path $root $part) -Force | Out-Null}
$os=Get-CimInstance Win32_OperatingSystem
[ordered]@{sourceCommit=(& git -C $repo rev-parse HEAD);osCaption=$os.Caption;osVersion=$os.Version;osBuild=$os.BuildNumber;python=(& python --version);phase=$Phase;genuineModelOutput=$false;syntheticPublicData=$true;brokerTested=$false;userPcTested=$false;priorRun=42236978414} | ConvertTo-Json | Set-Content (Join-Path $root 'evidence/environment.json')
$files=@(Get-ChildItem (Join-Path $repo 'tools/advisory') -Recurse -File | Where-Object {$_.FullName -notmatch '[\\/](bin|obj|__pycache__)[\\/]'})
$files+=Get-ChildItem (Join-Path $repo 'src/Apex.Core') -Recurse -File | Where-Object {$_.FullName -notmatch '[\\/](bin|obj)[\\/]'}
$files+=Get-Item (Join-Path $repo 'src/Apex.Desktop/Okx/OkxPublicMarketFeed.cs')
$files+=Get-Item (Join-Path $repo 'src/Apex.Desktop/Okx/OkxAdvisoryCanonicalData.cs')
$files+=Get-Item (Join-Path $repo 'src/Apex.Desktop/Services/WindowsPathSecurity.cs')
$files+=Get-Item $PSCommandPath
$files+=Get-Item (Join-Path $repo 'Directory.Build.props')
$map=@($files | ForEach-Object {
 $rel=[IO.Path]::GetRelativePath($repo,$_.FullName);$out=Join-Path (Join-Path $root 'source') $rel
 New-Item -ItemType Directory -Path (Split-Path $out) -Force | Out-Null
 Copy-Item -LiteralPath $_.FullName -Destination $out
 [ordered]@{path=$rel;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$map | ConvertTo-Json | Set-Content (Join-Path $root 'source/input-map.json')
Push-Location $repo
try {
 & python -X utf8 tools/advisory/native_reuse.py --root $root --prior $Prior *> (Join-Path $root 'evidence/reuse.log')
 if($LASTEXITCODE -ne 0){throw 'accepted native wheel/source reuse failed'}
 $python=Join-Path $root 'closure/hermes/env/Scripts/python.exe'
 $env:ADVISORY_PARITY_EVIDENCE=Join-Path $root 'evidence/parity'
 & $python -m unittest discover -s tools/advisory/tests -v *> (Join-Path $root 'evidence/unit-tests.log')
 $code=$LASTEXITCODE
 [ordered]@{exit=$code} | ConvertTo-Json | Set-Content (Join-Path $root 'evidence/unit-exit.json')
 if($code -ne 0){throw 'native Task2 unit/integration boundary tests failed'}
 Copy-Item -LiteralPath (Join-Path $repo 'tools/advisory/StrategyExport/bin/Debug/net8.0') -Destination (Join-Path $root 'closure/strategy-export') -Recurse
 if($Phase -eq 'red'){throw 'RED unexpectedly passed'}
 & $python -X utf8 tools/advisory/native_graphs.py $root (Join-Path $root 'closure/runtime-contract.json') *> (Join-Path $root 'evidence/graphs.log')
 if($LASTEXITCODE -ne 0){throw 'native genuine graph orchestration with fixture inference failed'}
} finally {
 $binaryMap=@(Get-ChildItem (Join-Path $root 'closure') -Recurse -File | Where-Object {$_.FullName -notmatch '__pycache__'} | ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($root,$_.FullName);sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
 $binaryMap | ConvertTo-Json | Set-Content (Join-Path $root 'evidence/runtime-map.json')
 Pop-Location
}
