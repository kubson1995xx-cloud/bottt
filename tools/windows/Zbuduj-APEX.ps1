#Requires -Version 5.1
<# Frontend-first builder. No test bypass, deployment, provisioning or broker smoke. #>
param([string]$Wyjscie = '', [ValidateSet('win-x64','linux-x64')][string]$Target='win-x64', [switch]$Offline)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'TestEvidence.ps1')
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if ([string]::IsNullOrWhiteSpace($Wyjscie)) { $Wyjscie=Join-Path $repo 'dist' }
$Wyjscie=[IO.Path]::GetFullPath($Wyjscie)
if ((Test-Path -LiteralPath $Wyjscie) -and @(Get-ChildItem -LiteralPath $Wyjscie -Force).Count -gt 0) { throw 'Katalog Wyjscie nie jest pusty; wymagany NOWY katalog.' }
if ((Test-Path -LiteralPath ($Wyjscie+'.zip')) -or (Test-Path -LiteralPath ($Wyjscie+'.zip.sha256'))) { throw 'ZIP juz istnieje; wybierz NOWY katalog.' }
if ($Target -eq 'linux-x64' -and ($PSVersionTable.PSVersion.Major -lt 7 -or -not $IsLinux -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -ne 'X64')) { throw 'linux-x64 requires PowerShell7 on Linux x64' }
if ($Target -eq 'win-x64' -and $env:OS -ne 'Windows_NT') { throw 'Windows target requires native Windows; no cross-publish acceptance' }
function Sprawdz([string]$co) { if ($LASTEXITCODE -ne 0) { throw "$co failed ($LASTEXITCODE); no accepted package" } }
function Hash([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
function Json([string]$p,$o) { [IO.File]::WriteAllText($p,($o|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false)) }
$node=(& node --version);Sprawdz 'Node';if ($node -notmatch '^v26\.') { throw 'Use tested Node26 toolchain' }
$npm=(& npm --version);Sprawdz 'npm';$sdk=(& dotnet --version);Sprawdz 'SDK';if ($sdk -notmatch '^8\.') { throw '.NET8 SDK required' }
$commit=(& git -C $repo rev-parse HEAD);Sprawdz 'git HEAD'
$dirty=[bool](& git -C $repo status --porcelain)
$evidence=Join-Path $Wyjscie 'Weryfikacja';New-Item -ItemType Directory -Force -Path $evidence | Out-Null
# External temporary install/test plumbing is never copied into the package.
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('apex-build-'+[Guid]::NewGuid().ToString('N'));New-ApexPrivateBuildDirectory $scratch
$emptyFeed=Join-Path $scratch 'empty-feed';New-Item -ItemType Directory -Path $emptyFeed|Out-Null
$oldPackage=$env:APEX_PACKAGE_ROOT;$oldEvidence=$env:NAVAL_HOST_EVIDENCE_DIR
$env:APEX_PACKAGE_ROOT=$null;$env:NAVAL_HOST_EVIDENCE_DIR=$null
function Inputs {
    $names=@(& git -C $repo ls-files) + @(& git -C $repo ls-files --others --exclude-standard)
    $items=@($names|Sort-Object -Unique|Where-Object { $_ -match '^(src/|tests/|tools/|frontend/|config/|docs/|reports/|\.github/|package.*\.json$|APEX.*\.sln$|README\.md$|Directory\.|global\.json$|NuGet\.)' }|ForEach-Object {
        $p=Join-Path $repo $_
        if (-not (Test-Path -LiteralPath $p -PathType Leaf) -or ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Source input missing/link' }
        [ordered]@{path=$_;bytes=(Get-Item -LiteralPath $p).Length;sha256=(Hash $p)}
    })
    return [ordered]@{schema='apex-source-inputs-v1';files=$items}
}
function NodeGate([string]$name,[string[]]$arguments) {
    $log=Join-Path $evidence ($name+'.txt')
    # TAP is selected explicitly so counts derive from actual node runner output.
    $oldNodeOptions=$env:NODE_OPTIONS
    try {
        $env:NODE_OPTIONS=($oldNodeOptions+' --test-reporter=tap').Trim()
        & npm --prefix frontend/naval @arguments 2>&1 | Tee-Object -FilePath $log | Write-Host
        Sprawdz $name
    } finally { $env:NODE_OPTIONS=$oldNodeOptions }
    $text=[IO.File]::ReadAllText($log)
    $values=@{}
    foreach ($n in @('tests','pass','fail','skipped','cancelled','todo')) { $m=[regex]::Match($text,('(?m)^# '+$n+' (\d+)\r?$'));if (-not $m.Success) { throw "Missing TAP counter $n" };$values[$n]=[int]$m.Groups[1].Value }
    if ($values.tests -le 0 -or $values.pass -ne $values.tests -or $values.fail+$values.skipped+$values.cancelled+$values.todo -ne 0) { throw 'Incomplete frontend tests' }
    return [ordered]@{total=$values.tests;passed=$values.pass;failed=$values.fail;log=($name+'.txt');logSha256=(Hash $log)}
}
$before=Inputs;Json (Join-Path $evidence 'source-inputs.json') $before
$rootHashes=@{};foreach ($f in @('index.html','app.js','app.css','decision-center.js')) { $rootHashes[$f]=Hash (Join-Path $repo ('src/Apex.Desktop/wwwroot/'+$f)) }
Push-Location $repo
try {
    $install=@('--ignore-scripts','--no-audit','--no-fund');if ($Offline) { $install+=@('--offline') }
    & npm --prefix frontend/naval ci @install;Sprawdz 'Clean Naval npm ci'
    & npm --prefix frontend/naval run typecheck;Sprawdz 'Naval strict typecheck'
    # npm test command is the same node runner, with an explicit TAP reporter for sealed evidence.
    $frontendTests=NodeGate 'frontend-tests' @('test','--','--test-reporter=tap')
    & npm --prefix frontend/naval run build;Sprawdz 'Naval production build and staging'
    $frontendBuild=NodeGate 'frontend-build-tests' @('run','test:build','--','--test-reporter=tap')
    $naval=Test-ApexNavalAssets (Join-Path $repo 'frontend/naval/dist')
    $stage=Test-ApexNavalAssets (Join-Path $repo 'src/Apex.Desktop/wwwroot/naval')
    if ($stage.sha256 -cne $naval.sha256) { throw 'dist/staging parity failed' }
    if ($Offline) { & dotnet restore APEX.Trading.Demo.sln --source $emptyFeed -p:NuGetAudit=false;Sprawdz 'Cache-only solution restore' }
    else { & dotnet restore APEX.Trading.Demo.sln;Sprawdz 'Solution restore' }
    if ($Target -eq 'win-x64') {
        $restore=@('restore','src/Apex.Desktop/Apex.Desktop.csproj','-r','win-x64','-p:SelfContained=true','-p:NuGetAudit=false');if ($Offline) { $restore+=@('--source',$emptyFeed) }; & dotnet @restore;Sprawdz 'Windows runtime restore'
    }
    & dotnet build APEX.Trading.Demo.sln -c Release --no-restore;Sprawdz 'Solution Release build'
    $built=Test-ApexNavalAssets (Join-Path $repo 'src/Apex.Desktop/bin/Release/net8.0/wwwroot/naval');if ($built.sha256 -cne $naval.sha256) { throw 'Build/Naval parity failed' }
    foreach ($suite in @(@('bot','tests/Apex.Tests/Apex.Tests.csproj'),@('platform','src/Platforms/cTrader/Tests/Apex.Platform.Tests.csproj'),@('backtester','tests/Apex.Backtester.Tests/Apex.Backtester.Tests.csproj'))) {
        & dotnet test $suite[1] -c Release --no-build --no-restore --logger ('trx;LogFileName='+$suite[0]+'.trx') --results-directory $evidence;Sprawdz $suite[0]
    }
    $testCounts=[ordered]@{bot=(Read-ApexTestCounts (Join-Path $evidence 'bot.trx'));platform=(Read-ApexTestCounts (Join-Path $evidence 'platform.trx'));backtester=(Read-ApexTestCounts (Join-Path $evidence 'backtester.trx'))}
    & npm ci @install;Sprawdz 'Root npm ci'
    & npm test;Sprawdz 'Root panel and packaging tests'
    & npm run check;Sprawdz 'Original panel JS syntax'
    & (Join-Path $repo 'tests/package/Test-BacktesterGate.ps1') -Source $repo -Scratch (Join-Path $scratch 'fixtures')
    & (Join-Path $repo 'tests/package/Test-NavalNotices.ps1') -Source $repo -Scratch (Join-Path $scratch 'notice-fixtures')
    $desktop=Join-Path $Wyjscie 'APEX-Desktop'
    if ($Target -eq 'linux-x64') {
        & dotnet publish src/Apex.Desktop/Apex.Desktop.csproj -c Release --no-restore --self-contained false -p:UseAppHost=false -p:IsTransformWebConfigDisabled=true -o $desktop
    } else {
        & dotnet publish src/Apex.Desktop/Apex.Desktop.csproj -c Release --no-restore -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IsTransformWebConfigDisabled=true -o $desktop
    }
    Sprawdz 'Publish'
    if ($Target -eq 'win-x64') {
        # Genuine build-time CLI only. Authority/result/logs stay in private external scratch.
        $presentationBuild=Join-Path $scratch 'presentation-build'
        New-ApexPresentation -Source $repo -Scratch $presentationBuild | Out-Null
        $restore=@('restore','tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj','-p:NuGetAudit=false');if ($Offline) { $restore+=@('--source',$emptyFeed) }; & dotnet @restore;Sprawdz 'Presentation Source restore'
        & dotnet build tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj -c Release --no-restore;Sprawdz 'Presentation Source build'
        $oldPresentation=$env:APEX_PRESENTATION_TEST_DIRECTORY
        try {
            $env:APEX_PRESENTATION_TEST_DIRECTORY=Join-Path $presentationBuild 'presentation'
            & dotnet test tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~WindowsLocalSetupTests --logger 'trx;LogFileName=presentation.trx' --results-directory $evidence;Sprawdz 'Typed historical presentation'
            Read-ApexTestCounts (Join-Path $evidence 'presentation.trx') | Out-Null
        } finally { $env:APEX_PRESENTATION_TEST_DIRECTORY=$oldPresentation }
        $presentationPublish=Join-Path $desktop 'presentation'
        New-Item -ItemType Directory -Path $presentationPublish | Out-Null
        foreach ($name in @('naval-publication.json','presentation.json')) { Copy-Item -LiteralPath (Join-Path $presentationBuild ('presentation/'+$name)) -Destination (Join-Path $presentationPublish $name) }
        Test-ApexPresentation $presentationPublish | Out-Null
    }
    $published=Test-ApexNavalAssets (Join-Path $desktop 'wwwroot/naval');if ($published.sha256 -cne $naval.sha256) { throw 'Publish/Naval parity failed' }
    foreach ($f in $rootHashes.Keys) { foreach ($tree in @('src/Apex.Desktop/wwwroot','src/Apex.Desktop/bin/Release/net8.0/wwwroot')) { if ((Hash (Join-Path $repo ($tree+'/'+$f))) -cne $rootHashes[$f]) { throw 'Original panel changed' } };if ((Hash (Join-Path $desktop ('wwwroot/'+$f))) -cne $rootHashes[$f]) { throw 'Published original panel differs' } }
    $ctrader=Join-Path $Wyjscie 'cTrader';New-Item -ItemType Directory -Path $ctrader|Out-Null
    Copy-Item 'src/Platforms/cTrader/ApexBridge/ApexBridge/bin/Release/net6.0/ApexBridge.algo' $ctrader
    Copy-Item 'src/Platforms/cTrader/ApexTradingAI/ApexTradingAI/bin/Release/net6.0/ApexTradingAI.algo' $ctrader
    $mt5=Join-Path $Wyjscie 'MT5-opcjonalny';New-Item -ItemType Directory -Path $mt5|Out-Null;Copy-Item 'src/Platforms/MT5/ApexBridge.mq5' $mt5
    $docs=Join-Path $Wyjscie 'Dokumentacja';New-Item -ItemType Directory -Path $docs|Out-Null
    foreach ($name in @('INSTALACJA_WINDOWS.md','PROCEDURA_DEMO_PEPPERSTONE.md','PODLACZENIE_PLATFORM.md','INSTRUKCJA_PANELU.md','KONFIGURACJA.md','OGRANICZENIA_I_STATUS.md','WYNIKI_TESTOW.md','PRZEGLAD_BEZPIECZENSTWA.md','METODOLOGIA_TESTOW.md','BACKTESTER.md','NAVAL_READ_PUBLICATION.md','NAVAL_DELIVERY_VPS.md','NAVAL_OFFLINE.md','NAVAL_SHARED_OFFLINE.md')) { Copy-Item (Join-Path 'docs' $name) $docs }
    Copy-Item 'README.md' $docs;Copy-Item 'reports/RAPORT_BADAN.md' $docs;Copy-Item 'frontend/naval/README.md' (Join-Path $docs 'NAVAL_FRONTEND.md')
    # Genuine installed runtime closure; include complete upstream license/notice texts.
    $queue=New-Object Collections.Generic.Queue[string];$seen=@{};$notices=New-Object Text.StringBuilder
    $direct=Get-Content 'frontend/naval/package.json' -Raw|ConvertFrom-Json
    foreach ($p in $direct.dependencies.PSObject.Properties) { $queue.Enqueue($p.Name) }
    while ($queue.Count -gt 0) {
        $name=$queue.Dequeue();if ($seen.ContainsKey($name)) { continue };$seen[$name]=$true
        $dir=Join-Path $repo ('frontend/naval/node_modules/'+$name);$manifest=Get-Content (Join-Path $dir 'package.json') -Raw|ConvertFrom-Json
        $license=@(Get-ChildItem -LiteralPath $dir -File|Where-Object {$_.Name -match '^(LICENSE|COPYING|NOTICE)'})
        $upstream=$false
        if ($license.Count -eq 0) {
            if ($name -cne '@react-three/fiber') { throw "Required genuine license notice absent: $name" }
            # Parent-authorized exception ONLY for this exact installed artifact and reviewed provenance.
            if ($manifest.name -cne '@react-three/fiber' -or $manifest.version -cne '9.6.1' -or $manifest.license -cne 'MIT' -or $manifest.repository.type -cne 'git' -or $manifest.repository.url -cne 'git+https://github.com/pmndrs/react-three-fiber.git' -or (Hash (Join-Path $dir 'package.json')) -cne 'a4d04c276030b69da8e45a15459f817fcc467d242a58cb0c40f8ba6aea273bf5') { throw 'Fiber upstream fallback refused: installed manifest identity/hash' }
            $third=Join-Path $repo 'docs/third-party';$provenance=Join-Path $third 'provenance.json';$notice=Join-Path $third 'react-three-fiber-9.6.1-LICENSE.txt'
            foreach ($p in @((Join-Path $repo 'docs'),$third,$provenance,$notice)) {
                if (-not (Test-Path -LiteralPath $p) -or ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Fiber upstream fallback refused: missing/linked source' }
            }
            # Provenance cannot redefine any expected value, URL, hash or version binding.
            if ((Hash $provenance) -cne '001a151cd91d29855b314cfa85878d922f5e82a67a47dbac0693176ece491911') { throw 'Fiber upstream fallback refused: provenance hash' }
            $pr=Get-Content -LiteralPath $provenance -Raw|ConvertFrom-Json
            if ($pr.schema_version -ne 1 -or $pr.package -cne '@react-three/fiber' -or $pr.version -cne '9.6.1' -or $pr.artifact_kind -cne 'upstream_repository_license_not_npm_package_file' -or $pr.npm_git_head -cne '2a528745e9aa7c9e6cca41e404b59d45cf0d0cc7' -or $pr.license_sha256 -cne '9c35b5de7b7493a707fffe4eb23bd2f7f449153c1911f7c6eefb4e591fd5349a' -or $pr.license_byte_length -ne 1072 -or $pr.installed_manifest_sha256 -cne 'a4d04c276030b69da8e45a15459f817fcc467d242a58cb0c40f8ba6aea273bf5') { throw 'Fiber upstream fallback refused: provenance identity' }
            if ((Get-Item -LiteralPath $notice).Length -ne 1072 -or (Hash $notice) -cne '9c35b5de7b7493a707fffe4eb23bd2f7f449153c1911f7c6eefb4e591fd5349a') { throw 'Fiber upstream fallback refused: notice length/hash' }
            $license=@(Get-Item -LiteralPath $notice);$upstream=$true
        }
        [void]$notices.AppendLine(('=== '+$manifest.name+' '+$manifest.version+' ('+$manifest.license+') ==='))
        if ($upstream) { [void]$notices.AppendLine('Source: upstream repository; NOT npm-shipped. npm gitHead 2a528745e9aa7c9e6cca41e404b59d45cf0d0cc7; https://raw.githubusercontent.com/pmndrs/react-three-fiber/2a528745e9aa7c9e6cca41e404b59d45cf0d0cc7/LICENSE') }
        foreach ($f in $license) { [void]$notices.AppendLine($f.Name);[void]$notices.AppendLine([IO.File]::ReadAllText($f.FullName)) }
        if ($null -ne $manifest.dependencies) { foreach ($p in $manifest.dependencies.PSObject.Properties) { $queue.Enqueue($p.Name) } }
    }
    [IO.File]::WriteAllText((Join-Path $docs 'THIRD_PARTY_NAVAL.txt'),$notices.ToString(),[Text.UTF8Encoding]::new($false))
    foreach ($name in @('Sprawdz-Pakiet.ps1','TestEvidence.ps1')) { Copy-Item (Join-Path $PSScriptRoot $name) $Wyjscie }
    Copy-Item (Join-Path $PSScriptRoot 'Smoke-Windows.ps1') $evidence
    if ($Target -eq 'linux-x64') {
        $launcher=@'
#!/bin/sh
set -eu
case "${APEX_DATA_DIR:-}" in /*) ;; *) printf '%s\n' 'Set external absolute private APEX_DATA_DIR first' >&2; exit 2;; esac
here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
data=$(realpath -e -- "$APEX_DATA_DIR")
case "$data/" in "$here/"*) printf '%s\n' 'Data must be outside immutable package' >&2; exit 2;; esac
exec dotnet "$here/APEX-Desktop/ApexDesktop.dll" --data "$data" --no-browser
'@
        [IO.File]::WriteAllText((Join-Path $Wyjscie 'Uruchom APEX.sh'),($launcher.Replace("`r`n","`n")+"`n"),[Text.Encoding]::ASCII)
        $mode='framework-dependent';$runtime='Microsoft.AspNetCore.App 8'
    } else {
        [IO.File]::WriteAllText((Join-Path $Wyjscie 'Uruchom APEX.cmd'),"@echo off`r`nstart `"`" `"%~dp0APEX-Desktop\ApexDesktop.exe`"`r`n",[Text.Encoding]::ASCII)
        $mode='self-contained';$runtime='included'
    }
    Json (Join-Path $evidence 'frontend-results.json') ([ordered]@{schema='apex-frontend-tests-v1';tests=$frontendTests;build=$frontendBuild;typecheckExit=0;buildExit=0})
    $after=Inputs
    if (($before|ConvertTo-Json -Depth 12 -Compress) -cne ($after|ConvertTo-Json -Depth 12 -Compress)) { throw 'Source inputs changed during build; preserve evidence, rebind before promotion' }
    $info=[ordered]@{schema='apex-build-v2';product='APEX Trading AI DEMO';sourceCommit="$commit".Trim();sourceDirty=$dirty;builtUtc=[DateTime]::UtcNow.ToString('o');target=$Target;deploymentMode=$mode;requiredRuntime=$runtime;demoOnly=$true;botTestsRun=$true;platformTestsRun=$true;backtesterTestsRun=$true;testCounts=$testCounts;panelTestsRun=$true;frontendTestsRun=$true;frontendBuildVerified=$true;brokerTested=$false;windowsRuntimeTested=$false;navalReadDefaultEnabled=$false;task7Acceptance='accepted-local-linux';sourceInputsSha256=(Hash (Join-Path $evidence 'source-inputs.json'));rootLockSha256=(Hash 'package-lock.json');navalLockSha256=(Hash 'frontend/naval/package-lock.json');navalManifestSha256=$naval.sha256;frontendResultsSha256=(Hash (Join-Path $evidence 'frontend-results.json'));tools=@{node=$node;npm=$npm;dotnet=$sdk;powershell=$PSVersionTable.PSVersion.ToString()};originalPanelSha256=$rootHashes}
    Json (Join-Path $Wyjscie 'apex-build.json') $info
    $base=$Wyjscie.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    $hashes=@(Get-ChildItem -LiteralPath $Wyjscie -Force -Recurse -File|Sort-Object FullName|ForEach-Object {(Hash $_.FullName)+'  '+$_.FullName.Substring($base.Length).Replace('\','/')})
    [IO.File]::WriteAllLines((Join-Path $Wyjscie 'manifest.sha256'),$hashes,[Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'Sprawdz-Pakiet.ps1') -Pakiet $Wyjscie
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($Wyjscie,($Wyjscie+'.zip'),[IO.Compression.CompressionLevel]::Optimal,$false)
    $zipHash=Hash ($Wyjscie+'.zip');[IO.File]::WriteAllText(($Wyjscie+'.zip.sha256'),($zipHash+'  '+[IO.Path]::GetFileName($Wyjscie)+".zip`n"),[Text.Encoding]::ASCII)
    # Independent safe extraction, CRC and native packaged tests. Evidence remains external/sealed ZIP unchanged.
    & (Join-Path $repo 'tests/package/Test-NavalPackage.ps1') -Source $repo -Pakiet ($Wyjscie+'-extracted') -Evidence ($Wyjscie+'-package-evidence') -Zip ($Wyjscie+'.zip')
    Write-Host "Local package gates PASS: $Wyjscie.zip SHA256 $zipHash. No broker, Windows/Linux deployment or device acceptance."
} finally {
    Pop-Location;$env:APEX_PACKAGE_ROOT=$oldPackage;$env:NAVAL_HOST_EVIDENCE_DIR=$oldEvidence
    # Keep private authority/result and failed evidence outside the immutable payload.
    Write-Host ('Private build scratch retained outside package: '+$scratch)
}
