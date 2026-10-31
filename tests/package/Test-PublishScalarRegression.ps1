#Requires -Version 5.1
# Nonshipped causal replay of Task5 reviewer's exact proof bytes. Never runs an EXE.
param([Parameter(Mandatory=$true)][string]$OriginalZip,[Parameter(Mandatory=$true)][string]$Evidence)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (Test-Path -LiteralPath $Evidence) { throw 'Fresh scalar evidence required' }
New-Item -ItemType Directory -Path $Evidence|Out-Null
$package=Join-Path $Evidence 'original-copy'
& python (Join-Path $PSScriptRoot 'extract_task6.py') $OriginalZip $package (Join-Path $Evidence 'original-archive.json')
if ($LASTEXITCODE -ne 0) { throw 'Original Task5 identity invalid' }
function Hash([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
function Seal {
    $root=(Get-Item -LiteralPath $package).FullName
    $lines=@(Get-ChildItem -LiteralPath $root -File -Recurse|Where-Object {$_.FullName -cne (Join-Path $root 'manifest.sha256')}|Sort-Object FullName|ForEach-Object {(Hash $_.FullName)+'  '+$_.FullName.Substring($root.Length+1).Replace('\','/')})
    [IO.File]::WriteAllLines((Join-Path $root 'manifest.sha256'),$lines,[Text.UTF8Encoding]::new($false))
}
$proof=Join-Path $package 'Weryfikacja/windows-publish.json'
$genuine=[IO.File]::ReadAllBytes($proof)
$originalHelper=Hash (Join-Path $package 'TestEvidence.ps1')
$pins=@{rid='d8b437141d178f2aeb723d4c72c2cf82b80014121877e71baa031f9e6b0eae8a';schema='185aa378f054002e5b3a88d997f0291404affaef91cd2ea51a0f3044d02175b2';tfm='226cf6c0c11f371cdba325d449934a2356915328a1ce7559f241488513580453'}
$results=New-Object Collections.Generic.List[object]
$pwsh=(Get-Process -Id $PID).Path
function Probe([string]$phase,[string]$name,[bool]$accepted) {
    Seal
    $old=$ErrorActionPreference
    try { $ErrorActionPreference='Continue';$text=& $pwsh -NoProfile -File (Join-Path $package 'Sprawdz-Pakiet.ps1') -Pakiet $package 2>&1;$code=$LASTEXITCODE } finally { $ErrorActionPreference=$old }
    # No arbitrary verifier diagnostics are published: fixed result + exact input identity only.
    $results.Add([ordered]@{phase=$phase;case=$name;proofSha256=(Hash $proof);helperSha256=(Hash (Join-Path $package 'TestEvidence.ps1'));exitCode=$code;expectedAccepted=$accepted;pass=(($code -eq 0) -eq $accepted)})
}
foreach ($phase in @('original','corrected')) {
    if ($phase -eq 'corrected') { Copy-Item -LiteralPath (Join-Path $repo 'tools/windows/TestEvidence.ps1') -Destination (Join-Path $package 'TestEvidence.ps1') -Force }
    [IO.File]::WriteAllBytes($proof,$genuine);Probe $phase 'scalar-positive' $true
    foreach ($field in @('rid','schema','tfm')) {
        $fixture=Join-Path $PSScriptRoot ('publish-scalar-originals/singleton-array-'+$field+'-proof.json')
        if ((Hash $fixture) -cne $pins[$field]) { throw 'Original reviewer proof bytes changed' }
        [IO.File]::WriteAllBytes($proof,[IO.File]::ReadAllBytes($fixture));Probe $phase ('singleton-array-'+$field) ($phase -eq 'original')
    }
    $wrong=[Text.Encoding]::UTF8.GetString($genuine)|ConvertFrom-Json;$wrong.rid='linux-x64'
    [IO.File]::WriteAllText($proof,($wrong|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false));Probe $phase 'wrong-scalar-rid' $false
    [IO.File]::WriteAllBytes($proof,$genuine);Probe $phase 'restored-positive' $true
}
$passed=@($results|Where-Object {-not $_.pass}).Count -eq 0
[ordered]@{status=$(if($passed){'PASS'}else{'FAIL'});scope='Original unchanged reviewer proof bytes; old/corrected copied shipped verifier; no Windows EXE execution';powershell=$PSVersionTable.PSVersion.ToString();originalHelperSha256=$originalHelper;results=$results.ToArray()}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $Evidence 'scalar-results.json') -Encoding UTF8
if (-not $passed) { throw 'Scalar causal replay failed' }
Write-Host 'SCALAR CAUSAL REPLAY PASS: 12 probes; original three acceptances, corrected three refusals, positive/wrong-scalar controls preserved.'
