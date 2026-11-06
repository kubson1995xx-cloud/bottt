param([ValidateSet('red','green')][string]$Phase='green')
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$root=Join-Path $env:RUNNER_TEMP 'windows-advisory-prerequisite'
if (Test-Path $root) { throw 'Immutable evidence directory already exists' }
New-Item -ItemType Directory -Path $root | Out-Null
foreach ($part in @('source','build','closure','evidence')) { New-Item -ItemType Directory -Path (Join-Path $root $part) | Out-Null }
$os=Get-CimInstance Win32_OperatingSystem
[ordered]@{sourceCommit=(& git -C $repo rev-parse HEAD);osCaption=$os.Caption;osVersion=$os.Version;osBuild=$os.BuildNumber;architecture=$os.OSArchitecture;python=(& python --version);phase=$Phase;inferenceAccepted=$false;brokerTested=$false;userPcTested=$false} | ConvertTo-Json | Set-Content (Join-Path $root 'evidence/environment.json')
$files=@(Get-ChildItem (Join-Path $repo 'tools/advisory') -Recurse -File | Where-Object { $_.FullName -notmatch '__pycache__' })
$files+=Get-Item $PSCommandPath
$files+=Get-Item (Join-Path $PSScriptRoot 'advisory-prerequisite-job.yml')
$map=@($files | ForEach-Object {
  $rel=[IO.Path]::GetRelativePath($repo,$_.FullName); $out=Join-Path (Join-Path $root 'source') $rel
  New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
  Copy-Item -LiteralPath $_.FullName -Destination $out
  [ordered]@{path=$rel;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$map | ConvertTo-Json | Set-Content (Join-Path $root 'source/input-map.json')
Push-Location $repo
try {
  & python -m unittest discover -s tools/advisory/tests -v *> (Join-Path $root 'evidence/unit-tests.log')
  $tests=$LASTEXITCODE
  [ordered]@{exitCode=$tests;phase=$Phase} | ConvertTo-Json | Set-Content (Join-Path $root 'evidence/unit-exit.json')
  if ($tests -ne 0) { throw "Python behavioral tests failed: $tests" }
  if ($Phase -eq 'red') { throw 'RED unexpectedly passed; do not claim calibration' }
  & python tools/advisory/native_setup.py --root $root *> (Join-Path $root 'evidence/setup.log')
  if ($LASTEXITCODE -ne 0) { throw 'Native runtime/setup prerequisite failed; retained setup.log' }
} finally { Pop-Location }
