#Requires -Version 5.1
# Original extracted payload only: no product rebuild or engine execution.
param([Parameter(Mandatory=$true)][string]$Download)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'TestEvidence.ps1')
$e=Join-Path $env:RUNNER_TEMP 'ui-repair-evidence';New-Item -ItemType Directory -Path $e|Out-Null
$zip=Join-Path $Download 'APEX-NAVAL-COMMAND-Windows-x64.zip'
if ((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() -cne '87deacbf67fd9de196511951ed95c50ff3c1f2595e8c769037824252d8732005') {throw 'Original ZIP identity mismatch'}
# Existing validated extraction helper, not Expand-Archive over unchecked members.
$extract=Join-Path $env:RUNNER_TEMP 'ui-original-extracted'
& python (Join-Path $repo 'tests/package/safe_extract.py') $zip $extract (Join-Path $e 'archive-readback.json') '87deacbf67fd9de196511951ed95c50ff3c1f2595e8c769037824252d8732005'
if ($LASTEXITCODE -ne 0) {throw 'Safe extraction failed'}
& (Join-Path $extract 'Sprawdz-Pakiet.ps1') -Pakiet $extract
$dotnet=(Get-Command dotnet).Source
$sdk=Join-Path $env:RUNNER_TEMP 'ui-sdk';New-Item -ItemType Directory -Path $sdk|Out-Null
[IO.File]::WriteAllText((Join-Path $sdk 'global.json'),'{"sdk":{"version":"8.0.427","rollForward":"disable"}}')
$env:APEX_PACKAGE_ROOT=$extract;$env:APEX_WINDOWS_EVIDENCE=$e;$env:DOTNET_HOST_PATH=$dotnet
$os=Get-CimInstance Win32_OperatingSystem
@{testCommit=(& git -C $repo rev-parse HEAD);zipSha256=(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant();os=$os.Caption;osVersion=$os.Version;kind='original-payload-pointer-RED'}|ConvertTo-Json|Set-Content (Join-Path $e 'identity.json') -Encoding UTF8
Push-Location $sdk
try {
 & $dotnet build (Join-Path $repo 'tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj') -c Release
 if ($LASTEXITCODE -ne 0) {throw 'Test compilation failed; not behavioral RED'}
 & $dotnet test (Join-Path $repo 'tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj') -c Release --no-build --no-restore --filter 'Gate=UiRepair' --logger 'trx;LogFileName=ui-original.trx' --results-directory $e
 $result=$LASTEXITCODE
} finally {Pop-Location}
& (Join-Path $extract 'Sprawdz-Pakiet.ps1') -Pakiet $extract
exit $result
