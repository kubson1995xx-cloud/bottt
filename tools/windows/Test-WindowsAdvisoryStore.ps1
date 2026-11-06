param([ValidateSet('red','green')][string]$Phase='green')
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$root=Join-Path $env:RUNNER_TEMP 'windows-advisory-store'
if(Test-Path $root){throw 'immutable evidence root already exists'}
foreach($part in @('source','closure','evidence')){New-Item -ItemType Directory -Path (Join-Path $root $part) -Force | Out-Null}
$os=Get-CimInstance Win32_OperatingSystem
[ordered]@{sourceCommit=(& git -C $repo rev-parse HEAD);osCaption=$os.Caption;osVersion=$os.Version;python=(& python --version);phase=$Phase;genuineModelOutput=$false;brokerTested=$false;upstreamReinstalled=$false;graphsRerun=$false} | ConvertTo-Json | Set-Content (Join-Path $root 'evidence/environment.json')
$files=@(Get-ChildItem (Join-Path $repo 'tools/advisory') -Recurse -File | Where-Object {$_.FullName -notmatch '[\\/](bin|obj|__pycache__)[\\/]'})
$files+=Get-ChildItem (Join-Path $repo 'src/Apex.Core') -Recurse -File | Where-Object {$_.FullName -notmatch '[\\/](bin|obj)[\\/]'}
foreach($rel in @('src/Apex.Desktop/Okx/OkxPublicMarketFeed.cs','src/Apex.Desktop/Okx/OkxAdvisoryCanonicalData.cs','src/Apex.Desktop/Services/WindowsPathSecurity.cs','Directory.Build.props','Directory.Build.targets','global.json')){if(Test-Path (Join-Path $repo $rel)){$files+=Get-Item (Join-Path $repo $rel)}}
$files+=Get-Item $PSCommandPath
$map=@($files | ForEach-Object {
 $rel=[IO.Path]::GetRelativePath($repo,$_.FullName);$out=Join-Path (Join-Path $root 'source') $rel
 New-Item -ItemType Directory -Path (Split-Path $out) -Force | Out-Null;Copy-Item -LiteralPath $_.FullName -Destination $out
 [ordered]@{path=$rel;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$map | ConvertTo-Json | Set-Content (Join-Path $root 'source/input-map.json')
Push-Location $repo
try {
 & dotnet build tools/advisory/StrategyExport/StrategyExport.csproj *> (Join-Path $root 'evidence/build.log')
 if($LASTEXITCODE -ne 0){throw 'native helper build failed'}
 Copy-Item -LiteralPath (Join-Path $repo 'tools/advisory/StrategyExport/bin/Debug/net8.0') -Destination (Join-Path $root 'closure/strategy-export') -Recurse
 if($Phase -eq 'red'){
  & python -X utf8 -m unittest discover -s tools/advisory/tests -p test_store_descendants.py -k test_pair_ -v *> (Join-Path $root 'evidence/tests.log');$code=$LASTEXITCODE
  & python -X utf8 -m unittest discover -s tools/advisory/tests -p test_store_descendants.py -k test_safe_existing -v *> (Join-Path $root 'evidence/control.log');$control=$LASTEXITCODE
  if($code -eq 0 -or $control -ne 0){throw 'RED/control gate invalid'}
 }else{
  $env:ADVISORY_PARITY_EVIDENCE=Join-Path $root 'evidence/parity'
  & python -X utf8 -m unittest discover -s tools/advisory/tests -v *> (Join-Path $root 'evidence/tests.log');$code=$LASTEXITCODE
 }
 [ordered]@{exit=$code;controlExit=$control} | ConvertTo-Json | Set-Content (Join-Path $root 'evidence/test-exit.json')
 if($Phase -eq 'green' -and $code -ne 0){throw 'native advisory suite failed'}
}finally{
 $binaryMap=@(Get-ChildItem (Join-Path $root 'closure') -Recurse -File | ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($root,$_.FullName);sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
 $binaryMap | ConvertTo-Json | Set-Content (Join-Path $root 'evidence/runtime-map.json');Pop-Location
}
