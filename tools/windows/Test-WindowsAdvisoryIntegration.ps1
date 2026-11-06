param([ValidateSet('red','green')][string]$Phase='green')
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$e=Join-Path $env:RUNNER_TEMP 'windows-advisory-integration';New-Item -ItemType Directory -Path $e|Out-Null
$source=Join-Path $e 'source';New-Item -ItemType Directory -Path $source|Out-Null
$files=@(Get-ChildItem (Join-Path $repo 'src'),(Join-Path $repo 'tests'),(Join-Path $repo 'tools/advisory') -File -Recurse|Where-Object{$_.FullName -notmatch '[\\/](bin|obj|node_modules|__pycache__)[\\/]'});
$map=@($files|ForEach-Object{$rel=[IO.Path]::GetRelativePath($repo,$_.FullName);$out=Join-Path $source $rel;New-Item -ItemType Directory -Force -Path (Split-Path $out)|Out-Null;Copy-Item $_.FullName $out;[ordered]@{path=$rel;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}});$map|ConvertTo-Json|Set-Content (Join-Path $e 'input-map.json')
$os=Get-CimInstance Win32_OperatingSystem
[ordered]@{source=(& git -C $repo rev-parse HEAD);os=$os.Caption;osBuild=$os.BuildNumber;genuineInference=$false;syntheticBroker=$true;userPcAuth=$false}|ConvertTo-Json|Set-Content (Join-Path $e 'environment.json')
& dotnet build (Join-Path $repo 'tests/Apex.Tests/Apex.Tests.csproj') -c Release -p:NuGetAudit=false *> (Join-Path $e 'build.log');if($LASTEXITCODE -ne 0){throw 'build failed'}
$bin=Join-Path $repo 'tests/Apex.Tests/bin/Release/net8.0';Copy-Item $bin (Join-Path $e 'tested-closure') -Recurse
$old=$ErrorActionPreference
try{$ErrorActionPreference='Continue';& dotnet test (Join-Path $repo 'tests/Apex.Tests/Apex.Tests.csproj') -c Release --no-build --filter 'FullyQualifiedName~OkxWindowsAdvisory|Gate=WindowsAdvisoryIntegration' --logger 'trx;LogFileName=integration.trx' --results-directory $e -- xUnit.ParallelizeTestCollections=false *> (Join-Path $e 'tests.log');$code=$LASTEXITCODE}finally{$ErrorActionPreference=$old}
[ordered]@{exit=$code;phase=$Phase}|ConvertTo-Json|Set-Content (Join-Path $e 'result.json')
if($Phase -eq 'green' -and $code -ne 0){throw 'native integration failed'}
if($Phase -eq 'red' -and $code -eq 0){throw 'RED unexpectedly passed'}
