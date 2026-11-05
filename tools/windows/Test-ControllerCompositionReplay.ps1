param([string]$Phase)
$ErrorActionPreference='Stop'
$repo=$env:GITHUB_WORKSPACE;$e=Join-Path $env:RUNNER_TEMP 'controller-windows-source'
. (Join-Path $repo 'tools/windows/TestEvidence.ps1')
$retained=Join-Path $env:RUNNER_TEMP ('controller-retained-'+$env:GITHUB_RUN_ID)
& gh run download 42121399200 --repo kubson1995xx-cloud/bottt --name Controller-Windows-Source-42121399200-1 --dir $retained
if($LASTEXITCODE -ne 0){throw 'Pinned retained candidate unavailable'}
$zip=Join-Path $retained 'candidate/APEX-Controller-Windows-x64-candidate.zip'
$zipHash='ae589e9550ae8ab8e48602d4683af60db3320bd897402e654ad794762388fcb3'
if((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() -cne $zipHash){throw 'Pinned candidate hash mismatch'}
$oldSource=Get-Content (Join-Path $retained 'source-inputs-before-build.json') -Raw|ConvertFrom-Json
foreach($row in $oldSource|Where-Object{$_.path.StartsWith('src/')}){
 if((Get-FileHash (Join-Path $repo $row.path) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $row.sha256){throw 'Product input changed; immutable reuse refused'}
}
$oldHost=Join-Path $retained 'composition-closure/host'
$hostHash=(Get-FileHash (Join-Path $oldHost 'ApexDesktop.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
$oldMap=Get-Content (Join-Path $retained 'binary-closures.json') -Raw|ConvertFrom-Json
foreach($row in $oldMap|Where-Object{$_.path.Replace('\','/').StartsWith('composition-closure/host/')}){
 if((Get-FileHash (Join-Path $retained $row.path) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $row.sha256){throw 'Retained generic product closure changed'}
}
$extracted=Join-Path $env:RUNNER_TEMP ('controller-retained-extracted-'+$env:GITHUB_RUN_ID)
& python (Join-Path $repo 'tests/package/safe_extract.py') $zip $extracted (Join-Path $e 'candidate-extraction.json') $zipHash
if($LASTEXITCODE -ne 0){throw 'Pinned retained package safe extraction failed'}
$candidate=Join-Path $e 'candidate';New-Item -ItemType Directory -Path $candidate|Out-Null
Copy-Item $zip (Join-Path $candidate 'APEX-Controller-Windows-x64-candidate.zip')
Copy-Item (Join-Path $retained 'composition.trx') (Join-Path $e 'prior-composition.trx')
Copy-Item (Join-Path $retained 'extracted-package.trx') (Join-Path $e 'prior-extracted-package.trx')
$presentation=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('ApexControllerReplayPresentation-'+[Guid]::NewGuid().ToString('N'))
New-ApexPrivateBuildDirectory $presentation
Copy-Item (Join-Path $retained 'public-presentation/*') $presentation
$env:APEX_PRESENTATION_TEST_DIRECTORY=$presentation
$env:APEX_COMPOSITION_PACKAGE_EXE=Join-Path $extracted 'APEX-Desktop/ApexDesktop.exe'
$env:APEX_COMPOSITION_EVIDENCE=$e
$projectDir=Join-Path $e 'replay-project';New-Item -ItemType Directory -Path $projectDir|Out-Null
$project=Join-Path $projectDir 'Replay.csproj'
$xml=@"
<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Apex.Windows.Tests</AssemblyName><OutputType>Exe</OutputType><GenerateProgramFile>false</GenerateProgramFile><StartupObject>Apex.ControllerComposition.Tests.CompositionPeer</StartupObject><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems><IsPackable>false</IsPackable><NoWarn>CA1416</NoWarn></PropertyGroup>
<ItemGroup>
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1"/><PackageReference Include="xunit" Version="2.9.2"/><PackageReference Include="xunit.runner.visualstudio" Version="2.8.2"/>
<FrameworkReference Include="Microsoft.AspNetCore.App"/>
<Reference Include="ApexDesktop"><HintPath>$oldHost/ApexDesktop.dll</HintPath></Reference><Reference Include="Apex.Core"><HintPath>$oldHost/Apex.Core.dll</HintPath></Reference><Reference Include="Apex.Adapters.Okx"><HintPath>$oldHost/Apex.Adapters.Okx.dll</HintPath></Reference>
<Compile Include="$repo/tests/Apex.ControllerComposition.Tests/*.cs"/><Compile Include="$repo/tests/Apex.ControllerTransport.Tests/*.cs"/><Compile Include="$repo/tests/Apex.ControllerClient.Tests/*.cs"/><Compile Include="$repo/tests/Apex.ControllerAdmission.Tests/NativeAdmissionTests.cs"/><Compile Include="$repo/tests/Apex.Windows.Tests/WindowsCdpBrowser.cs"/>
</ItemGroup></Project>
"@
[IO.File]::WriteAllText($project,$xml,[Text.UTF8Encoding]::new($false))
$items=Join-Path $e 'replay-build-inputs';New-Item -ItemType Directory -Path $items|Out-Null
Push-Location (Join-Path $e 'sdk')
try {
 & dotnet build $project -c Release -p:NuGetAudit=false "-p:CustomAfterMicrosoftCommonTargets=$e/compile-evidence.targets" "-p:ControllerEvidenceRoot=$items" *> (Join-Path $e 'replay-build.log')
 if($LASTEXITCODE -ne 0){throw 'Replay tester compilation failed; no product build attempted'}
 & dotnet msbuild $project -p:Configuration=Release "-preprocess:$e/replay-project-imports.xml" *> (Join-Path $e 'replay-imports.log')
 if($LASTEXITCODE -ne 0){throw 'Replay import expansion failed'}
 $testRoot=Join-Path $projectDir 'bin/Release/net8.0'
 if((Get-FileHash (Join-Path $testRoot 'ApexDesktop.dll') -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hostHash){throw 'Replay did not consume retained product'}
 $closure=Join-Path $e 'composition-closure';New-Item -ItemType Directory -Path $closure|Out-Null
 Copy-Item $testRoot (Join-Path $closure 'tests') -Recurse
 Copy-Item $oldHost (Join-Path $closure 'host') -Recurse
 $old=$ErrorActionPreference
 $filter='FullyQualifiedName~PackagedTests.Extracted_EXE_native_SSH_pointer_lifecycle_five_views_and_close_after_START'
 if(-not $Phase.Contains('completion')){$filter+='|FullyQualifiedName~NativePairingTests.Extracted_native_pairing_trust_cancel_preserves_existing_profile'}
 try{$ErrorActionPreference='Continue';& dotnet test $project -c Release --no-build --no-restore --filter $filter --logger 'trx;LogFileName=extracted-package-replay.trx' --results-directory $e *> (Join-Path $e 'extracted-package-replay.log');$code=$LASTEXITCODE}finally{$ErrorActionPreference=$old}
 if((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() -cne $zipHash){throw 'Retained ZIP mutated'}
 [ordered]@{phase=$Phase;packageExit=$code;productRun=42121399200;productCommit='0df508c1eb1f608f058005ae71bdf92a4675a3a8';testCommit=(& git -C $repo rev-parse HEAD);zipSha256=$zipHash;exeSha256=(Get-FileHash $env:APEX_COMPOSITION_PACKAGE_EXE -Algorithm SHA256).Hash.ToLowerInvariant();retainedGenericDesktopSha256=$hostHash;productInputsIdentical=$true;productRebuilt=$false;compositionSuiteRerun=$false;brokerTested=$false;vpsTested=$false;windows11Tested=$false}|ConvertTo-Json|Set-Content (Join-Path $e 'candidate-summary.json') -Encoding UTF8
 if($code -ne 0){throw 'Retained EXE native UI replay failed; diagnostic layer retained'}
}finally{Pop-Location;Remove-Item -LiteralPath $presentation -Recurse -Force}
