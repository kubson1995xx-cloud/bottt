param([string]$Phase='package')
# Build the self-contained win-x64 local-edition ZIP, extract it independently and run the
# extracted-EXE tests against a loopback synthetic OKX DEMO (runner hosts file + runner-only
# trusted test root). The real exchange is never reachable from this job's EXE.
$ErrorActionPreference='Stop'
$repo=$env:GITHUB_WORKSPACE
$e=Join-Path $env:RUNNER_TEMP 'windows-local-package'
New-Item -ItemType Directory -Force -Path $e|Out-Null
. (Join-Path $repo 'tools/windows/TestEvidence.ps1')
$sdk=Join-Path $e 'sdk';New-Item -ItemType Directory -Force -Path $sdk|Out-Null
[IO.File]::WriteAllText((Join-Path $sdk 'global.json'),'{"sdk":{"version":"8.0.427","rollForward":"disable"}}')
$hosts=Join-Path $env:SystemRoot 'System32\drivers\etc\hosts';$hostsBackup=Join-Path $e 'hosts.backup';$thumb=$null
Push-Location $sdk
try {
 if((& dotnet --version) -cne '8.0.427'){throw 'Exact SDK selection failed'}
 $os=Get-CimInstance Win32_OperatingSystem
 [ordered]@{sourceCommit=(& git -C $repo rev-parse HEAD);osCaption=$os.Caption;osVersion=$os.Version;osBuild=$os.BuildNumber;sdk='8.0.427';powershell=$PSVersionTable.PSVersion.ToString();syntheticOnly=$true;brokerTested=$false;windows11=$false}|ConvertTo-Json|Set-Content (Join-Path $e 'environment.json')
 Push-Location $repo
 $old=$ErrorActionPreference
 try{$ErrorActionPreference='Continue';& npm.cmd ci --ignore-scripts --no-audit --no-fund *> (Join-Path $e 'panel-install.log');if($LASTEXITCODE -ne 0){throw 'Panel install failed'};& npm.cmd test *> (Join-Path $e 'panel-tests.log');if($LASTEXITCODE -ne 0){throw 'Polish panel tests failed'}}finally{$ErrorActionPreference=$old;Pop-Location}
 Test-ApexNavalAssets (Join-Path $repo 'src/Apex.Desktop/wwwroot/naval')|ConvertTo-Json|Set-Content (Join-Path $e 'naval-assets.json')
 & dotnet build (Join-Path $repo 'tools/Apex.Backtester/Apex.Backtester.csproj') -c Release -p:NuGetAudit=false *> (Join-Path $e 'presentation-build.log');if($LASTEXITCODE -ne 0){throw 'Presentation producer build failed'}
 $presentation=Join-Path $env:RUNNER_TEMP ('local-presentation-'+$env:GITHUB_RUN_ID)
 New-ApexPresentation $repo $presentation|ConvertTo-Json|Set-Content (Join-Path $e 'presentation-proof.json')
 $presentationDir=Join-Path $presentation 'presentation'
 # ZIP root layout: {Uruchom-APEX.cmd, INSTRUKCJA-LOKALNIE.pl.md, APEX-Desktop/ApexDesktop.exe + presentation}
 $package=Join-Path $env:RUNNER_TEMP 'APEX-Windows-Local-x64';New-Item -ItemType Directory -Path $package|Out-Null
 $publish=Join-Path $package 'APEX-Desktop'
 & dotnet publish (Join-Path $repo 'src/Apex.Desktop/Apex.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:NuGetAudit=false -o $publish *> (Join-Path $e 'publish.log')
 if($LASTEXITCODE -ne 0){throw 'Self-contained EXE publish failed'}
 Copy-Item $presentationDir (Join-Path $publish 'presentation') -Recurse
 Copy-Item (Join-Path $repo 'tools/windows/INSTRUKCJA-LOKALNIE.pl.md') (Join-Path $package 'INSTRUKCJA-LOKALNIE.pl.md')
 # Classic console host (conhost) so the greyed close button and Polish title work even where
 # Windows Terminal is the default console; falls back to a direct start if conhost is absent.
 [IO.File]::WriteAllText((Join-Path $package 'Uruchom-APEX.cmd'),"@echo off`r`nif exist `"%SystemRoot%\System32\conhost.exe`" (start `"`" `"%SystemRoot%\System32\conhost.exe`" `"%~dp0APEX-Desktop\ApexDesktop.exe`" %*) else (start `"`" `"%~dp0APEX-Desktop\ApexDesktop.exe`" %*)`r`n",[Text.Encoding]::ASCII)
 [ordered]@{schema='apex-windows-local-v1';sourceCommit=(& git -C $repo rev-parse HEAD);target='win-x64';sdk='8.0.427';selfContained=$true;edition='local-okx-demo';syntheticAcceptanceOnly=$true;brokerTested=$false;windows11Tested=$false;secureDesktopStartTestedOnExe=$false}|ConvertTo-Json|Set-Content (Join-Path $package 'apex-build.json') -Encoding UTF8
 $inventory=@(Get-ChildItem $package -File -Recurse|Sort-Object FullName|ForEach-Object{[ordered]@{path=$_.FullName.Substring($package.Length+1).Replace('\','/');sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant();bytes=$_.Length}})
 if(@($inventory|Where-Object{$_.path -match 'controller-ssh|controller-connection|authorized_keys|\.pub$|\.dpapi$|apex\.config|accounts/|credentials|\.pfx$'}).Count -ne 0){throw 'Private file in package layout'}
 [IO.File]::WriteAllLines((Join-Path $package 'manifest.sha256'),@($inventory|ForEach-Object{$_.sha256+'  '+$_.path}),[Text.UTF8Encoding]::new($false))
 $candidate=Join-Path $e 'candidate';New-Item -ItemType Directory -Path $candidate|Out-Null
 $zip=Join-Path $candidate 'APEX-Windows-Local-x64.zip'
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 [IO.Compression.ZipFile]::CreateFromDirectory($package,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
 $zipHash=(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
 $extractRoot=Join-Path $env:RUNNER_TEMP 'APEX-Windows-Local-extracted'
 & python (Join-Path $repo 'tests/package/safe_extract.py') $zip $extractRoot (Join-Path $e 'extraction.json') $zipHash
 if($LASTEXITCODE -ne 0){throw 'ZIP CRC/inventory/safe extraction failed'}
 $extracted=$extractRoot
 foreach($row in $inventory){if((Get-FileHash (Join-Path $extracted $row.path) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $row.sha256){throw 'Extracted file differs from package inventory'}}
 $exe=Join-Path $extracted 'APEX-Desktop/ApexDesktop.exe'
 # Runner-only synthetic OKX DEMO endpoint (never shipped): trusted test root + hosts redirect.
 Copy-Item $hosts $hostsBackup
 $cert=New-SelfSignedCertificate -DnsName 'eea.okx.com','wseeapap.okx.com' -CertStoreLocation 'Cert:\LocalMachine\My' -NotAfter (Get-Date).AddDays(1) -KeyExportPolicy Exportable
 $thumb=$cert.Thumbprint
 $pfxPassword=[Guid]::NewGuid().ToString('N');$pfx=Join-Path $env:RUNNER_TEMP 'synthetic-okx.pfx'
 Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $pfxPassword -AsPlainText -Force)|Out-Null
 $cer=Join-Path $env:RUNNER_TEMP 'synthetic-okx.cer';Export-Certificate -Cert $cert -FilePath $cer|Out-Null;Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\Root'|Out-Null
 Add-Content -LiteralPath $hosts -Value "`r`n127.0.0.1 eea.okx.com`r`n127.0.0.1 wseeapap.okx.com`r`n127.0.0.1 www.okx.com`r`n" -Encoding ASCII
 & ipconfig /flushdns|Out-Null
 $env:APEX_SYNTHETIC_OKX_PFX=$pfx;$env:APEX_SYNTHETIC_OKX_PFX_PASSWORD=$pfxPassword
 $project=Join-Path $repo 'tests/Apex.ControllerComposition.Tests/Apex.ControllerComposition.Tests.csproj'
 & dotnet build $project -c Release -p:NuGetAudit=false *> (Join-Path $e 'extracted-tests-build.log');if($LASTEXITCODE -ne 0){throw 'Extracted-EXE test build failed'}
 $env:APEX_COMPOSITION_PACKAGE_EXE=$exe;$env:APEX_COMPOSITION_EVIDENCE=$e
 $old=$ErrorActionPreference
 try{$ErrorActionPreference='Continue';& dotnet test $project -c Release --no-build --filter 'FullyQualifiedName~Apex.ControllerComposition.Tests.LocalPackagedTests' --logger 'trx;LogFileName=extracted-local.trx' --results-directory $e -- xUnit.ParallelizeTestCollections=false *> (Join-Path $e 'extracted-local-tests.log');$code=$LASTEXITCODE}finally{$ErrorActionPreference=$old}
 foreach($row in $inventory){if((Get-FileHash (Join-Path $extracted $row.path) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $row.sha256){throw 'Extracted package changed during native execution'}}
 if((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() -cne $zipHash){throw 'ZIP changed during native execution'}
 [ordered]@{zipSha256=$zipHash;exeSha256=(Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant();zipBytes=(Get-Item $zip).Length;extractedTestsExit=$code;extractedTests=(Read-ApexTestCounts (Join-Path $e 'extracted-local.trx'));actualExtractedExe=$true;syntheticOkxLoopback=$true;secureDesktopStartTestedOnExe=$false;brokerTested=$false;windows11Tested=$false}|ConvertTo-Json -Depth 4|Set-Content (Join-Path $e 'package-summary.json')
 if($code -ne 0){throw 'Extracted-EXE tests failed; ZIP and TRX retained'}
}
finally{
 Pop-Location
 if(Test-Path -LiteralPath $hostsBackup){Copy-Item $hostsBackup $hosts -Force;& ipconfig /flushdns|Out-Null}
 if($thumb){Get-ChildItem 'Cert:\LocalMachine\Root','Cert:\LocalMachine\My'|Where-Object{$_.Thumbprint -eq $thumb}|Remove-Item -Force -ErrorAction SilentlyContinue}
 Remove-Item -LiteralPath (Join-Path $env:RUNNER_TEMP 'synthetic-okx.pfx') -Force -ErrorAction SilentlyContinue
}
