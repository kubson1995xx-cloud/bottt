#Requires -Version 5.1
<# Behavioral notice tests: execute the actual builder block with private copies of genuine installed inputs. #>
param([Parameter(Mandatory=$true)][string]$Source,[Parameter(Mandatory=$true)][string]$Scratch)
$ErrorActionPreference='Stop'
$Source=(Resolve-Path -LiteralPath $Source).Path
if (Test-Path -LiteralPath $Scratch) { throw 'Scratch must be new' }
New-Item -ItemType Directory -Path $Scratch|Out-Null
function Hash([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
$text=[IO.File]::ReadAllText((Join-Path $Source 'tools/windows/Zbuduj-APEX.ps1'))
$start=$text.IndexOf('    # Genuine installed runtime closure;')
$end=$text.IndexOf("    foreach (`$name in @('Sprawdz-Pakiet.ps1'",$start)
if ($start -lt 0 -or $end -le $start) { throw 'Actual builder notice block not found' }
$block=[scriptblock]::Create($text.Substring($start,$end-$start))
$seed=Join-Path $Scratch 'genuine-inputs';New-Item -ItemType Directory -Path (Join-Path $seed 'frontend/naval') -Force|Out-Null
Copy-Item (Join-Path $Source 'frontend/naval/package.json') (Join-Path $seed 'frontend/naval/package.json')
$queue=New-Object Collections.Generic.Queue[string];$seen=@{}
$direct=Get-Content (Join-Path $Source 'frontend/naval/package.json') -Raw|ConvertFrom-Json
foreach ($p in $direct.dependencies.PSObject.Properties) { $queue.Enqueue($p.Name) }
while ($queue.Count -gt 0) {
    $name=$queue.Dequeue();if ($seen.ContainsKey($name)) { continue };$seen[$name]=$true
    $dir=Join-Path $Source ('frontend/naval/node_modules/'+$name);$dest=Join-Path $seed ('frontend/naval/node_modules/'+$name)
    New-Item -ItemType Directory -Path $dest -Force|Out-Null
    Copy-Item (Join-Path $dir 'package.json') $dest
    Get-ChildItem -LiteralPath $dir -File|Where-Object {$_.Name -match '^(LICENSE|COPYING|NOTICE)'}|ForEach-Object {Copy-Item $_.FullName $dest}
    $manifest=Get-Content (Join-Path $dir 'package.json') -Raw|ConvertFrom-Json
    if ($null -ne $manifest.dependencies) { foreach ($p in $manifest.dependencies.PSObject.Properties) {$queue.Enqueue($p.Name)} }
}
New-Item -ItemType Directory -Path (Join-Path $seed 'docs')|Out-Null
Copy-Item (Join-Path $Source 'docs/third-party') (Join-Path $seed 'docs/third-party') -Recurse
$cases=@('ExactGenuineClosure','WrongFiberVersion','WrongFiberName','WrongRepository','ChangedManifestBytes','AbsentLicense','AlteredLicense','WrongProvenanceCommit','WrongProvenanceVersion','WrongProvenanceHash','WrongProvenanceLength','WrongProvenanceKind','AbsentProvenance','OtherPackageMissingNotice')
$results=@()
foreach ($case in $cases) {
    $repo=Join-Path $Scratch $case;Copy-Item $seed $repo -Recurse
    $docs=Join-Path $repo 'out';New-Item -ItemType Directory -Path $docs|Out-Null
    $fiber=Join-Path $repo 'frontend/naval/node_modules/@react-three/fiber/package.json'
    $license=Join-Path $repo 'docs/third-party/react-three-fiber-9.6.1-LICENSE.txt'
    $prov=Join-Path $repo 'docs/third-party/provenance.json'
    switch ($case) {
        'WrongFiberVersion' {(Get-Content $fiber -Raw).Replace('"version": "9.6.1"','"version": "9.6.2"')|Set-Content $fiber}
        'WrongFiberName' {(Get-Content $fiber -Raw).Replace('"name": "@react-three/fiber"','"name": "@other/fiber"')|Set-Content $fiber}
        'WrongRepository' {(Get-Content $fiber -Raw).Replace('pmndrs/react-three-fiber.git','other/react-three-fiber.git')|Set-Content $fiber}
        'ChangedManifestBytes' {[IO.File]::AppendAllText($fiber,"`n")}
        'AbsentLicense' {Remove-Item $license}
        'AlteredLicense' {[IO.File]::AppendAllText($license,'ALTERED')}
        'WrongProvenanceCommit' {(Get-Content $prov -Raw).Replace('2a528745e9aa7c9e6cca41e404b59d45cf0d0cc7','0000000000000000000000000000000000000000')|Set-Content $prov}
        'WrongProvenanceVersion' {(Get-Content $prov -Raw).Replace('"version": "9.6.1"','"version": "9.6.2"')|Set-Content $prov}
        'WrongProvenanceHash' {(Get-Content $prov -Raw).Replace('9c35b5de7b7493a707fffe4eb23bd2f7f449153c1911f7c6eefb4e591fd5349a',('0'*64))|Set-Content $prov}
        'WrongProvenanceLength' {(Get-Content $prov -Raw).Replace('"license_byte_length": 1072','"license_byte_length": 1073')|Set-Content $prov}
        'WrongProvenanceKind' {(Get-Content $prov -Raw).Replace('upstream_repository_license_not_npm_package_file','npm_file')|Set-Content $prov}
        'AbsentProvenance' {Remove-Item $prov}
        'OtherPackageMissingNotice' {Get-ChildItem (Join-Path $repo 'frontend/naval/node_modules/react') -File|Where-Object {$_.Name -match '^(LICENSE|COPYING|NOTICE)'}|Remove-Item}
    }
    $errorText=$null
    Push-Location $repo
    try { & $block } catch {$errorText=$_.Exception.Message} finally {Pop-Location}
    $output=Join-Path $docs 'THIRD_PARTY_NAVAL.txt';$pass=$false
    if ($case -eq 'ExactGenuineClosure') {
        if (-not $errorText -and (Test-Path $output)) {
            $notice=[IO.File]::ReadAllText($output);$genuine=[IO.File]::ReadAllText($license)
            $pass=$notice.Contains($genuine) -and $notice.Contains('upstream repository; NOT npm-shipped') -and $notice.Contains('2a528745e9aa7c9e6cca41e404b59d45cf0d0cc7') -and $notice.Contains('=== @react-three/fiber 9.6.1 (MIT) ===')
        }
    } else { $pass=([bool]$errorText -and -not (Test-Path $output)) }
    $results+=@{case=$case;pass=$pass;refusal=$errorText;outputPresent=(Test-Path $output)}
    Write-Host "$case : $pass $(if ($errorText) {$errorText})"
}
[IO.File]::WriteAllText((Join-Path $Scratch 'results.json'),($results|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
if (@($results|Where-Object {-not $_.pass}).Count) { throw 'Behavioral notice tests failed' }
Write-Host "NOTICE BEHAVIOR PASS: $($results.Count) cases; genuine closure $($seen.Count) packages. No installed/cache mutation."
