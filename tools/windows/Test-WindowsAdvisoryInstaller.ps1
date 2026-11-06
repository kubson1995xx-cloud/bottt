param([Parameter(Mandatory=$true)][string]$Prior)
# Narrow installer replay using retained delivery05 resources, not another EXE/package build.
$ErrorActionPreference='Stop'
$repo=$env:GITHUB_WORKSPACE;$e=Join-Path $env:RUNNER_TEMP 'windows-advisory-installer'
New-Item -ItemType Directory -Path $e|Out-Null
$source=Join-Path $e 'source';New-Item -ItemType Directory -Path $source|Out-Null
$files=@(Get-ChildItem (Join-Path $repo 'src'),(Join-Path $repo 'tests'),(Join-Path $repo 'tools/advisory'),(Join-Path $repo 'tools/windows') -Recurse -File|Where-Object{$_.FullName -notmatch '[\\/](bin|obj|node_modules|__pycache__)[\\/]'})
$rows=@($files|ForEach-Object{$rel=[IO.Path]::GetRelativePath($repo,$_.FullName);$out=Join-Path $source $rel;New-Item -ItemType Directory -Path (Split-Path $out) -Force|Out-Null;Copy-Item $_.FullName $out;[ordered]@{path=$rel;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
$rows|ConvertTo-Json|Set-Content (Join-Path $e 'input-map.json')
$os=Get-CimInstance Win32_OperatingSystem
[ordered]@{sourceCommit=(& git -C $repo rev-parse HEAD);os=$os.Caption;osBuild=$os.BuildNumber;resourceRun=42268838572;resourceZipSha256='4471eac2daca55bb48195b582999ecdf6caad0d7fe1537463f7a8c1cbbc11828';genuineInference=$false;brokerTested=$false;userPcAuth=$false}|ConvertTo-Json|Set-Content (Join-Path $e 'environment.json')
$sdk=Join-Path $e 'sdk';New-Item -ItemType Directory -Path $sdk|Out-Null
[IO.File]::WriteAllText((Join-Path $sdk 'global.json'),'{"sdk":{"version":"8.0.427","rollForward":"disable"}}')
Push-Location $sdk
try{
 if((& dotnet --version) -cne '8.0.427'){throw 'Exact SDK selection failed'}
 $extracted=Join-Path $env:RUNNER_TEMP 'installer-retained-extracted'
 & python (Join-Path $repo 'tests/package/safe_extract.py') (Join-Path $Prior 'candidate/APEX-Windows-Local-x64.zip') $extracted (Join-Path $e 'retained-extraction.json') '4471eac2daca55bb48195b582999ecdf6caad0d7fe1537463f7a8c1cbbc11828' --advisory-resources
 if($LASTEXITCODE -ne 0){throw 'Retained ZIP refused'}
 $helper=Join-Path $e 'helper'
 & dotnet publish (Join-Path $repo 'tools/advisory/StrategyExport/StrategyExport.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:NuGetAudit=false -o $helper *> (Join-Path $e 'helper-build.log')
 if($LASTEXITCODE -ne 0){throw 'Native helper build failed'}
 $bundle=Join-Path $extracted 'AI'
 Copy-Item (Join-Path $bundle 'ApexAdvisoryStoreGuard.exe') (Join-Path $e 'old-delivery05-guard.exe')
 $env:APEX_INSTALLER_OLD_GUARD=Join-Path $e 'old-delivery05-guard.exe'
 $env:APEX_INSTALLER_NEW_GUARD=Join-Path $helper 'StrategyExport.exe'
 $env:APEX_INSTALLER_PATH_EVIDENCE=Join-Path $e 'native-path-control.json'
 & python -B (Join-Path $repo 'tools/advisory/tests/test_native_installer_paths.py') -v *> (Join-Path $e 'native-path-control.log')
 if($LASTEXITCODE -ne 0){throw 'Same-root native old/new long-path control failed'}
 # Explicit diagnostic/correction overlay only; preserve the original retained ZIP byte-for-byte.
 Copy-Item (Join-Path $helper 'StrategyExport.exe') (Join-Path $bundle 'ApexAdvisoryStoreGuard.exe') -Force
 foreach($name in @('store.py','local_install.py')){Copy-Item (Join-Path $repo ('tools/advisory/'+$name)) (Join-Path $bundle ('bridge/'+$name)) -Force}
 $manifest=Get-Content (Join-Path $bundle 'bundle.json') -Raw|ConvertFrom-Json
 foreach($row in $manifest.Files){$row.Sha256=(Get-FileHash -LiteralPath (Join-Path $bundle $row.Path) -Algorithm SHA256).Hash.ToLowerInvariant()}
 $manifest|ConvertTo-Json -Depth 6 -Compress|Set-Content (Join-Path $bundle 'bundle.json') -Encoding utf8NoBOM
 Copy-Item (Join-Path $bundle 'bundle.json') (Join-Path $e 'replay-bundle.json')
 $env:APEX_ADVISORY_TEST_BUNDLE=$bundle
 $project=Join-Path $repo 'tests/Apex.Tests/Apex.Tests.csproj'
 & dotnet build $project -c Release -p:NuGetAudit=false *> (Join-Path $e 'tests-build.log');if($LASTEXITCODE -ne 0){throw 'Native installer test build failed'}
 Copy-Item (Join-Path $repo 'tests/Apex.Tests/bin/Release/net8.0') (Join-Path $e 'tested-closure') -Recurse
 $old=$ErrorActionPreference
 try{$ErrorActionPreference='Continue';& dotnet test $project -c Release --no-build --filter 'Gate=WindowsAdvisoryInstaller' --logger 'trx;LogFileName=installer.trx' --results-directory $e -- xUnit.ParallelizeTestCollections=false *> (Join-Path $e 'installer-tests.log');$code=$LASTEXITCODE}finally{$ErrorActionPreference=$old}
 [ordered]@{exit=$code;resourceZipUnchanged=((Get-FileHash (Join-Path $Prior 'candidate/APEX-Windows-Local-x64.zip') -Algorithm SHA256).Hash.ToLowerInvariant() -ceq '4471eac2daca55bb48195b582999ecdf6caad0d7fe1537463f7a8c1cbbc11828');patchedResourceFiles=@('ApexAdvisoryStoreGuard.exe','bridge/store.py','bridge/local_install.py','bundle.json')}|ConvertTo-Json|Set-Content (Join-Path $e 'result.json')
 if($code -ne 0){throw 'Native installer refused; exact diagnostic and RED closure retained'}
}finally{Pop-Location}
