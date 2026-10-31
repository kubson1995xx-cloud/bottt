#Requires -Version 5.1
# Shared by builder and package verifier; counts are read from actual executable test results.
function Read-ApexTestCounts([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing test evidence: $Path" }
    [xml]$trx = [IO.File]::ReadAllText($Path)
    $c = $trx.SelectSingleNode("//*[local-name()='Counters']")
    $results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    if ($null -eq $c) { throw "Missing TRX counters: $Path" }
    foreach ($name in @('total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted', 'inconclusive', 'notExecuted')) {
        if ($c.GetAttribute($name) -notmatch '^\d+$') { throw "Invalid TRX counter $name in $Path" }
    }
    if ([int]$c.total -le 0 -or [int]$c.executed -ne [int]$c.total -or [int]$c.passed -ne [int]$c.total -or
        [int]$c.failed -ne 0 -or [int]$c.error -ne 0 -or [int]$c.timeout -ne 0 -or [int]$c.aborted -ne 0 -or
        [int]$c.inconclusive -ne 0 -or [int]$c.notExecuted -ne 0 -or $results.Count -ne [int]$c.total -or
        @($results | Where-Object { $_.outcome -ne 'Passed' }).Count -gt 0) {
        throw "Empty, failed or inconsistent executable test evidence: $Path"
    }
    return [ordered]@{ total = [int]$c.total; passed = [int]$c.passed; failed = [int]$c.failed }
}

# The shipped frontend uses Node's flat TAP test reporter (no test suites).
# Require one complete summary, a matching plan and actual passing test records.
function Read-ApexFrontendCounts([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Missing frontend TAP log' }
    $text = [IO.File]::ReadAllText($Path)
    $values = @{}
    foreach ($name in @('tests','suites','pass','fail','skipped','cancelled','todo')) {
        $matches = [regex]::Matches($text, ('(?m)^# '+$name+' ([0-9]+)\r?$'))
        if ($matches.Count -ne 1) { throw 'Missing/duplicate frontend TAP counter' }
        $values[$name] = [int]$matches[0].Groups[1].Value
    }
    $plan = [regex]::Matches($text, '(?m)^1\.\.([0-9]+)\r?$')
    # Detect the whole result-shaped line family first, including unsupported
    # spacing/case/IDs. Do not silently filter extra records by valid syntax.
    $records = [regex]::Matches($text, '(?im)^[^\S\r\n]*(?:not[^\S\r\n]*)?ok[^\r\n]*\r?$')
    if ([regex]::Matches($text,'(?m)^TAP version 13\r?$').Count -ne 1 -or
        $values.tests -le 0 -or $values.pass -ne $values.tests -or
        $values.suites -ne 0 -or $values.fail -ne 0 -or $values.skipped -ne 0 -or $values.cancelled -ne 0 -or $values.todo -ne 0 -or
        $plan.Count -ne 1 -or [int]$plan[0].Groups[1].Value -ne $values.tests -or $records.Count -ne $values.tests -or
        $text -match '(?im)^\s*(not ok\b|Bail out!)|^\s*ok\b[^\r\n]*#\s*(SKIP|TODO)\b') { throw 'Incomplete/contradictory frontend TAP evidence' }
    for ($i=0; $i -lt $records.Count; $i++) {
        # Supported flat records: canonical positive ID, bare or Node description.
        # Other TAP shapes are refused rather than inferred from summary totals.
        $record = [regex]::Match($records[$i].Value, '\Aok ([1-9][0-9]*)(?: - [^\r\n]+)?\r?\z')
        if (-not $record.Success) { throw 'Unsupported/malformed frontend TAP result' }
        if ([int]$record.Groups[1].Value -ne $i+1) { throw 'Invalid frontend TAP test sequence' }
    }
    return [ordered]@{total=$values.tests;passed=$values.pass;failed=$values.fail;skipped=$values.skipped;cancelled=$values.cancelled;todo=$values.todo}
}

# Public presentation validation is deliberately not financial authority. The native Source
# test and (in Task3) the host must ALSO use NavalPublicationReader.Parse on these bytes.
function Read-ApexPresentationJson([string]$Path, [int]$MaximumBytes) {
    # PS5.1 does not preload the framework assembly containing the JSON XML reader.
    Add-Type -AssemblyName System.Runtime.Serialization
    if ((Get-Item -LiteralPath $Path).Length -gt $MaximumBytes) { throw 'Presentation JSON too large' }
    return (Read-ApexJsonBytes ([IO.File]::ReadAllBytes($Path)) $MaximumBytes)
}
function Read-ApexJsonBytes([byte[]]$bytes,[int]$MaximumBytes) {
    Add-Type -AssemblyName System.Runtime.Serialization
    if ($bytes.Length -gt $MaximumBytes) { throw 'JSON too large' }
    # The framework JSON XML reader retains duplicate keys on PS5.1 AND PS7.
    # ConvertFrom-Json alone silently replaces them on Windows PowerShell.
    $quotas=New-Object System.Xml.XmlDictionaryReaderQuotas
    $quotas.MaxDepth=64; $quotas.MaxStringContentLength=$MaximumBytes; $quotas.MaxArrayLength=$MaximumBytes
    $reader=[System.Runtime.Serialization.Json.JsonReaderWriterFactory]::CreateJsonReader($bytes,$quotas)
    try { $doc=New-Object System.Xml.XmlDocument; $doc.Load($reader) } finally { $reader.Dispose() }
    # Check before ConvertFrom-Json/return can pipeline-unwrap a singleton array (PS5.1 too).
    if ($doc.DocumentElement.GetAttribute('type') -cne 'object') { throw 'Presentation object root required' }
    foreach ($node in $doc.SelectNodes('//*[@type="object"]')) {
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($child in $node.ChildNodes) { if (-not $seen.Add($child.LocalName)) { throw 'Presentation duplicate JSON property' } }
    }
    return ([Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json)
}
function Assert-ApexPresentationFields($Value,[string[]]$Names) {
    if ($null -eq $Value -or $Value -isnot [pscustomobject]) { throw 'Presentation object required' }
    $actual=@($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $Names.Count) { throw 'Presentation unexpected/missing field' }
    foreach ($n in $Names) { if ($actual -cnotcontains $n) { throw 'Presentation field spelling invalid' } }
}
function Test-ApexPresentation([string]$Directory) {
    try {
        $root=Get-Item -LiteralPath $Directory -Force
        if (-not $root.PSIsContainer -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Presentation directory invalid' }
        $files=@(Get-ChildItem -LiteralPath $Directory -Force)
        if ($files.Count -ne 2) { throw 'Presentation exact two-file inventory required' }
        foreach ($f in $files) {
            if ($f.PSIsContainer -or ($f.Attributes -band [IO.FileAttributes]::ReparsePoint) -or @('presentation.json','naval-publication.json') -cnotcontains $f.Name) { throw 'Presentation unlisted/linked file' }
        }
        $m=Read-ApexPresentationJson (Join-Path $Directory 'presentation.json') 65536
        Assert-ApexPresentationFields $m @('schema','publicationSha256','inputSha256','coreSha256','backtesterSha256','scope')
        if ($m.schema -isnot [string] -or $m.schema -cne 'apex-presentation-v1') { throw 'Presentation schema invalid' }
        foreach ($n in @('publicationSha256','inputSha256','coreSha256','backtesterSha256')) { if ($m.$n -isnot [string] -or $m.$n -cnotmatch '\A[a-f0-9]{64}\z') { throw 'Presentation hash invalid' } }
        $path=Join-Path $Directory 'naval-publication.json'
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $m.publicationSha256) { throw 'Presentation publication hash mismatch' }
        $e=Read-ApexPresentationJson $path 33554432
        Assert-ApexPresentationFields $e @('schema','temporalClassification','sourceClockUtc','sourceFreshness','provenance','publication','contentSha256')
        if ($e.schema -cne 'naval-shared-publication-export-v1' -or $e.temporalClassification -cne 'HISTORICAL_OFFLINE_CHECKPOINT' -or $e.sourceFreshness -cne 'UNKNOWN' -or $e.contentSha256 -isnot [string] -or $e.contentSha256 -cnotmatch '\A[a-f0-9]{64}\z') { throw 'Presentation historical export required' }
        Assert-ApexPresentationFields $e.provenance @('inputSha256','coreSha256','backtesterSha256')
        foreach ($n in @('inputSha256','coreSha256','backtesterSha256')) { if ($e.provenance.$n -isnot [string] -or $e.provenance.$n -cne $m.$n) { throw 'Presentation provenance mismatch' } }
        Assert-ApexPresentationFields $m.scope @('Id','PublicationScopeId','PortfolioId','ConfigDigest','Accounts')
        Assert-ApexPresentationFields $m.scope.PortfolioId @('PortfolioId','ReportingCurrency')
        foreach ($n in @('Id','PublicationScopeId','ConfigDigest')) { if ($m.scope.$n -isnot [string] -or $m.scope.$n.Length -eq 0) { throw 'Presentation scope string required' } }
        $j=$e.publication.journal; $c=$j.checkpoint
        if ($m.scope.Id -cne 'presentation' -or $m.scope.PublicationScopeId -isnot [string] -or $m.scope.PublicationScopeId -cne $j.scopeId -or $m.scope.ConfigDigest -isnot [string] -or $m.scope.ConfigDigest -cnotmatch '\A[a-fA-F0-9]{64}\z' -or $m.scope.ConfigDigest -cne $c.configDigest -or $m.scope.PortfolioId.PortfolioId -isnot [string] -or $m.scope.PortfolioId.PortfolioId -cne $c.portfolioId.portfolioId -or $m.scope.PortfolioId.ReportingCurrency -isnot [string] -or $m.scope.PortfolioId.ReportingCurrency -cne $c.portfolioId.reportingCurrency) { throw 'Presentation foreign scope' }
        if ($m.scope.Accounts -isnot [array] -or $m.scope.Accounts.Count -eq 0 -or $m.scope.Accounts.Count -ne @($c.accounts).Count) { throw 'Presentation account inventory invalid' }
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        for ($i=0;$i -lt $m.scope.Accounts.Count;$i++) {
            $a=$m.scope.Accounts[$i]; $b=$c.accounts[$i].account
            Assert-ApexPresentationFields $a @('Venue','Environment','AccountId','QuoteCurrency')
            foreach ($n in @('Venue','Environment','AccountId','QuoteCurrency')) { if ($a.$n -isnot [string] -or $a.$n.Length -eq 0) { throw 'Presentation account string required' } }
            if ($a.Venue -cne 'SYNTHETIC' -or $a.Environment -cne 'Offline' -or $a.AccountId -isnot [string] -or $a.QuoteCurrency -isnot [string] -or $a.Venue -cne $b.venue -or $a.Environment -cne $b.environment -or $a.AccountId -cne $b.accountId -or $a.QuoteCurrency -cne $b.quoteCurrency -or -not $seen.Add(($a|ConvertTo-Json -Compress))) { throw 'Presentation foreign/non-synthetic account' }
        }
        if ($e.publication.classification -cne 'OFFLINE_SHARED_CAPITAL / SYNTHETIC_NOT_RESEARCH / NO_BROKER_AUTHORITY' -or $c.currentQuality -cne 'UNKNOWN' -or $c.runtimeReadiness -cne 'UNKNOWN' -or $c.coverage -cne 'UNKNOWN' -or $c.brokerState -cne 'NOT_CONFIGURED' -or $null -ne $c.brokerAccountId -or $null -ne $c.brokerEquity -or $null -ne $c.realizedPnl -or $null -ne $c.unrealizedPnl) { throw 'Presentation synthetic unknown semantics required' }
        # Inspect ALL nested labels, not just the checkpoint's first account.
        $pending=New-Object Collections.Generic.Queue[object];$pending.Enqueue($e)
        while ($pending.Count -gt 0) {
            $v=$pending.Dequeue()
            if ($v -is [pscustomobject]) {
                foreach ($p in $v.PSObject.Properties) {
                    if ($p.Name -match '(?i)credential|password|secret|token|receipt|apiKey|privateKey|executionAuthority') { throw 'Presentation private authority forbidden' }
                    if ($p.Name -ceq 'environment' -and $p.Value -cne 'Offline' -or $p.Name -ceq 'venue' -and $p.Value -cne 'SYNTHETIC') { throw 'Presentation non-synthetic label' }
                    if ($null -ne $p.Value) { $pending.Enqueue($p.Value) }
                }
            } elseif ($v -is [array]) { foreach ($item in $v) { if ($null -ne $item) { $pending.Enqueue($item) } } }
        }
        return $m
    } catch { if ($_.Exception.Message -like 'Presentation *') { throw }; throw 'Presentation invalid/unavailable' }
}
# Build-only helper: no exporter, authority, result, tests or logs are copied into publish.
function New-ApexPrivateBuildDirectory([string]$Path) {
    if (Test-Path -LiteralPath $Path) { throw 'Build scratch must be new' }
    if ($env:OS -eq 'Windows_NT') {
        $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $acl=New-Object Security.AccessControl.DirectorySecurity
        $acl.SetSecurityDescriptorSddlForm(('O:'+ $sid +'G:'+ $sid +'D:P(A;OICI;FA;;;'+$sid+')(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)'))
        $dir=New-Object IO.DirectoryInfo($Path)
        if ($PSVersionTable.PSVersion.Major -le 5) { $dir.Create($acl) } else { [IO.FileSystemAclExtensions]::Create($dir,$acl) }
    } else {
        [void][IO.Directory]::CreateDirectory($Path,[IO.UnixFileMode]448)
    }
}
function New-ApexPresentation([string]$Source,[string]$Scratch) {
    $Source=[IO.Path]::GetFullPath($Source); $Scratch=[IO.Path]::GetFullPath($Scratch)
    New-ApexPrivateBuildDirectory $Scratch
    $public=Join-Path $Scratch 'presentation';New-ApexPrivateBuildDirectory $public
    New-ApexPrivateBuildDirectory (Join-Path $Scratch 'presentation-authority')
    $input=Join-Path $Source 'tests/Apex.Backtester.Tests/fixtures/synthetic/naval-shared-replay-v1.json'
    $cli=Join-Path $Source 'tools/Apex.Backtester/bin/Release/net8.0/Apex.Backtester.dll'
    $core=Join-Path (Split-Path $cli) 'Apex.Core.dll'
    # PS5 native stderr is an ErrorRecord; gate on actual exit, preserving private logs.
    $old=$ErrorActionPreference
    try {
        $ErrorActionPreference='Continue'
        $producerArgs=@($cli,'naval-shared-replay','--input',$input,'--state-dir',(Join-Path $Scratch 'presentation-authority'),'--output',(Join-Path $Scratch 'presentation-result.json'),'--publication',(Join-Path $public 'naval-publication.json'))
        if (Get-Command Invoke-ApexBuildDotnet -ErrorAction SilentlyContinue) { Invoke-ApexBuildDotnet @producerArgs 1> (Join-Path $Scratch 'presentation-cli.stdout.log') 2> (Join-Path $Scratch 'presentation-cli.stderr.log') }
        else { & dotnet @producerArgs 1> (Join-Path $Scratch 'presentation-cli.stdout.log') 2> (Join-Path $Scratch 'presentation-cli.stderr.log') }
        $exit=$LASTEXITCODE
    } finally { $ErrorActionPreference=$old }
    if ($exit -ne 0) { throw 'Presentation producer failed; private scratch retained' }
    $p=Read-ApexPresentationJson (Join-Path $public 'naval-publication.json') 33554432
    foreach ($pair in @(@('inputSha256',$input),@('coreSha256',$core),@('backtesterSha256',$cli))) {
        if ((Get-FileHash -LiteralPath $pair[1] -Algorithm SHA256).Hash.ToLowerInvariant() -cne $p.provenance.($pair[0])) { throw 'Presentation producer binary/input binding mismatch' }
    }
    $c=$p.publication.journal.checkpoint
    $m=[ordered]@{schema='apex-presentation-v1';publicationSha256=(Get-FileHash -LiteralPath (Join-Path $public 'naval-publication.json') -Algorithm SHA256).Hash.ToLowerInvariant();inputSha256=$p.provenance.inputSha256;coreSha256=$p.provenance.coreSha256;backtesterSha256=$p.provenance.backtesterSha256;scope=[ordered]@{Id='presentation';PublicationScopeId=$p.publication.journal.scopeId;PortfolioId=[ordered]@{PortfolioId=$c.portfolioId.portfolioId;ReportingCurrency=$c.portfolioId.reportingCurrency};ConfigDigest=$c.configDigest;Accounts=@($c.accounts|ForEach-Object {[ordered]@{Venue=$_.account.venue;Environment=$_.account.environment;AccountId=$_.account.accountId;QuoteCurrency=$_.account.quoteCurrency}})}}
    [IO.File]::WriteAllText((Join-Path $public 'presentation.json'),($m|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    return (Test-ApexPresentation $public)
}

# Runtime evidence is hash-bound build provenance, not a publisher signature.
function Get-ApexTextSha256([string]$Text) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
}
# Only exact canonical System32 OS C/C++ components are exempt from pack binding.
# Callers supply Get-Item canonical paths; this is NOT a loose runtime allowlist.
function Test-ApexWindowsOsRuntimeModule([string]$Name,[string]$Path,[string]$SystemDirectory) {
    if (@('ucrtbase.dll','msvcp_win.dll') -inotcontains $Name) { return $false }
    return $Path.Equals((Join-Path $SystemDirectory $Name),[StringComparison]::OrdinalIgnoreCase)
}

# Hash executable PE sections, not mutable apphost metadata/resources/bundle data.
function Read-ApexPeExecutableSections([string]$Path) {
    $b=[IO.File]::ReadAllBytes($Path)
    if ($b.Length -lt 256 -or $b.Length -gt 268435456 -or [BitConverter]::ToUInt16($b,0) -ne 0x5a4d) { throw 'Invalid bounded PE image' }
    $pe=[long][BitConverter]::ToUInt32($b,60)
    if ($pe -lt 64 -or $pe+24 -gt $b.Length -or [BitConverter]::ToUInt32($b,[int]$pe) -ne 0x4550 -or [BitConverter]::ToUInt16($b,[int]$pe+4) -ne 0x8664) { throw 'Invalid x64 PE header' }
    $count=[BitConverter]::ToUInt16($b,[int]$pe+6);$optional=[BitConverter]::ToUInt16($b,[int]$pe+20)
    $table=$pe+24+$optional
    if ($count -lt 1 -or $count -gt 96 -or $optional -lt 112 -or $table+40*$count -gt $b.Length -or [BitConverter]::ToUInt16($b,[int]$pe+24) -ne 0x20b) { throw 'Invalid PE section table' }
    $sections=@();$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $sha=[Security.Cryptography.SHA256]::Create()
    try {
        for ($i=0;$i -lt $count;$i++) {
            $offset=[int]($table+40*$i)
            if (([BitConverter]::ToUInt32($b,$offset+36) -band 0x20000000) -eq 0) { continue }
            $name=[Text.Encoding]::ASCII.GetString($b,$offset,8).TrimEnd([char]0)
            $size=[long][BitConverter]::ToUInt32($b,$offset+16);$start=[long][BitConverter]::ToUInt32($b,$offset+20)
            if ($name -cnotmatch '\A[.A-Za-z0-9_-]{1,8}\z' -or -not $seen.Add($name) -or $size -le 0 -or $start -lt $table+40*$count -or $start+$size -gt $b.Length) { throw 'Invalid PE executable section' }
            $sections+=[pscustomobject]@{name=$name;bytes=$size;sha256=([BitConverter]::ToString($sha.ComputeHash($b,[int]$start,[int]$size))).Replace('-','').ToLowerInvariant()}
        }
    } finally { $sha.Dispose() }
    if ($sections.Count -eq 0) { throw 'No executable PE sections' }
    return $sections
}
function Test-ApexWindowsStaticHost([string]$Exe,$Proof) {
    Assert-ApexPresentationFields $Proof @('pack','version','templateSha256','runtimeListSha256','codeSections','droppedNativeComponents')
    if ($Proof.pack -isnot [string] -or $Proof.version -isnot [string] -or $Proof.pack -cne 'Microsoft.NETCore.App.Host.win-x64' -or $Proof.version -cnotmatch '\A8\.0\.[0-9]+\z') { throw 'Static host pack identity invalid' }
    foreach ($field in @('templateSha256','runtimeListSha256')) { if ($Proof.$field -isnot [string] -or $Proof.$field -cnotmatch '\A[a-f0-9]{64}\z') { throw 'Static host provenance hash invalid' } }
    $required=@('coreclr.dll','clrjit.dll','hostfxr.dll','hostpolicy.dll')
    if ($Proof.droppedNativeComponents -isnot [array] -or $Proof.droppedNativeComponents.Count -ne $required.Count) { throw 'Static host runtime components unproven' }
    for ($i=0;$i -lt $required.Count;$i++) { if ($Proof.droppedNativeComponents[$i] -isnot [string] -or $Proof.droppedNativeComponents[$i] -cne $required[$i]) { throw 'Static host runtime component identity invalid' } }
    $actual=@(Read-ApexPeExecutableSections $Exe)
    if ($Proof.codeSections -isnot [array] -or $Proof.codeSections.Count -ne $actual.Count) { throw 'Static host code section inventory invalid' }
    for ($i=0;$i -lt $actual.Count;$i++) {
        $expected=$Proof.codeSections[$i];Assert-ApexPresentationFields $expected @('name','bytes','sha256')
        if ($expected.name -isnot [string] -or ($expected.bytes -isnot [int] -and $expected.bytes -isnot [long]) -or $expected.sha256 -isnot [string] -or $expected.name -cne $actual[$i].name -or $expected.bytes -ne $actual[$i].bytes -or $expected.sha256 -cne $actual[$i].sha256) { throw 'EXE runtime code differs from SDK-resolved singlefilehost' }
    }
    return $true
}

function Test-ApexWindowsPublish([string]$Directory,[string]$EvidencePath) {
    $m=Read-ApexPresentationJson $EvidencePath 1048576
    Assert-ApexPresentationFields $m @('schema','rid','sdkVersion','exeSha256','runtimeConfigUtf8','runtimeConfigSha256','assetsSha256','publishArgv','runtimePacks','nativeLibraries','staticHost')
    if ($m.schema -cne 'apex-windows-publish-v1' -or $m.rid -cne 'win-x64' -or $m.sdkVersion -isnot [string] -or $m.sdkVersion -cnotmatch '\A8\.0\.[0-9]+\z') { throw 'Windows publish identity invalid' }
    foreach ($n in @('exeSha256','runtimeConfigSha256','assetsSha256')) { if ($m.$n -isnot [string] -or $m.$n -cnotmatch '\A[a-f0-9]{64}\z') { throw 'Windows publish hash invalid' } }
    if ($m.exeSha256 -cne (Get-FileHash -LiteralPath (Join-Path $Directory 'ApexDesktop.exe') -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Windows publish EXE binding invalid' }
    foreach ($f in Get-ChildItem -LiteralPath $Directory -Force) {
        if (($f.Attributes -band [IO.FileAttributes]::ReparsePoint) -or ($f.PSIsContainer -and @('wwwroot','presentation') -cnotcontains $f.Name) -or (-not $f.PSIsContainer -and @('ApexDesktop.exe','ApexDesktop.pdb','Apex.Core.pdb') -cnotcontains $f.Name)) { throw 'Windows publish exact layout invalid; no arbitrary loose runtime files' }
    }
    $expected=@('publish','src/Apex.Desktop/Apex.Desktop.csproj','-c','Release','--no-restore','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:PublishTrimmed=false','-p:PublishReadyToRun=false','-p:IsTransformWebConfigDisabled=true','-o')
    if ($m.publishArgv -isnot [array] -or $m.publishArgv.Count -ne $expected.Count+1) { throw 'Windows publish argv invalid' }
    for ($i=0;$i -lt $expected.Count;$i++) { if ($m.publishArgv[$i] -isnot [string] -or ($i -eq 1 -and $m.publishArgv[$i].Replace('\','/') -cnotmatch '(^|/)src/Apex.Desktop/Apex.Desktop.csproj\z') -or ($i -ne 1 -and $m.publishArgv[$i] -cne $expected[$i])) { throw 'Windows publish argv invalid' } }
    if ($m.publishArgv[-1] -isnot [string] -or $m.publishArgv[-1] -cnotmatch '(^|[/\\])APEX-Desktop\z') { throw 'Windows publish output invalid' }
    if ($m.runtimeConfigUtf8 -isnot [string] -or (Get-ApexTextSha256 $m.runtimeConfigUtf8) -cne $m.runtimeConfigSha256) { throw 'Windows runtimeconfig hash invalid' }
    $r=Read-ApexJsonBytes ([Text.Encoding]::UTF8.GetBytes($m.runtimeConfigUtf8)) 65536
    if ($r -isnot [pscustomobject] -or $r.runtimeOptions.tfm -cne 'net8.0' -or $null -ne $r.runtimeOptions.framework -or $null -ne $r.runtimeOptions.frameworks -or $r.runtimeOptions.includedFrameworks -isnot [array] -or $r.runtimeOptions.includedFrameworks.Count -ne 2) { throw 'Windows self-contained included frameworks required' }
    if ($m.runtimePacks -isnot [array] -or $m.runtimePacks.Count -ne 2) { throw 'Windows runtime packs required' }
    $names=@('Microsoft.NETCore.App','Microsoft.AspNetCore.App')
    for ($i=0;$i -lt 2;$i++) {
        $framework=@($r.runtimeOptions.includedFrameworks|Where-Object { $_.name -ceq $names[$i] })
        $pack=@($m.runtimePacks|Where-Object { $_.name -ceq ($names[$i]+'.Runtime.win-x64') })
        if ($framework.Count -ne 1 -or $pack.Count -ne 1) { throw 'Windows runtime pack identity invalid' }
        Assert-ApexPresentationFields $framework[0] @('name','version');Assert-ApexPresentationFields $pack[0] @('name','version','sha512')
        if ($framework[0].name -isnot [string] -or $framework[0].version -isnot [string] -or $pack[0].name -isnot [string] -or $pack[0].version -isnot [string] -or $pack[0].version -cnotmatch '\A8\.0\.[0-9]+\z' -or $framework[0].version -cne $pack[0].version -or $pack[0].sha512 -isnot [string] -or $pack[0].sha512 -cnotmatch '\A[A-Za-z0-9+/]{86}==\z') { throw 'Windows runtime pack version/hash invalid' }
    }
    if ($m.nativeLibraries -isnot [array] -or $m.nativeLibraries.Count -eq 0) { throw 'Windows native provenance required' }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($n in $m.nativeLibraries) {
        Assert-ApexPresentationFields $n @('name','pack','bytes','sha256')
        if ($n.name -isnot [string] -or $n.name -cnotmatch '\A[A-Za-z0-9_.-]+\.dll\z' -or -not $seen.Add($n.name) -or $n.pack -cnotin @('Microsoft.NETCore.App.Runtime.win-x64','Microsoft.AspNetCore.App.Runtime.win-x64') -or $n.sha256 -isnot [string] -or $n.sha256 -cnotmatch '\A[a-f0-9]{64}\z' -or ($n.bytes -isnot [int] -and $n.bytes -isnot [long]) -or $n.bytes -le 0) { throw 'Windows native provenance invalid' }
    }
    if (-not $seen.Contains('coreclr.dll')) { throw 'Windows coreclr provenance required' }
    Test-ApexWindowsStaticHost (Join-Path $Directory 'ApexDesktop.exe') $m.staticHost | Out-Null
    if ($m.staticHost.version -cne @($m.runtimePacks|Where-Object name -ceq 'Microsoft.NETCore.App.Runtime.win-x64')[0].version) { throw 'Static host/runtime pack version mismatch' }
    return $m
}

# Exact bounded Vite inventory, also used on dist/staging/build/publish/extraction.
function Test-ApexNavalAssets([string]$NavalRoot) {
    $root = [IO.Path]::GetFullPath($NavalRoot)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'Missing required Naval build' }
    $nodes = @((Get-Item -LiteralPath $root -Force)) + @(Get-ChildItem -LiteralPath $root -Force -Recurse)
    foreach ($n in $nodes) { if (($n.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Naval link/reparse forbidden' } }
    $manifestPath = Join-Path $root 'build-manifest.json'
    $m = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    if ($m.schema -cne 'naval-build-v1' -or @($m.artifacts).Count -lt 3) { throw 'Invalid Naval inventory schema' }
    $listed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $folded = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($a in $m.artifacts) {
        $p = [string]$a.path
        if ($p -cnotmatch '^(index\.html|assets/[A-Za-z0-9_-]+\.(js|css))$' -or -not $folded.Add($p) -or $a.sha256 -cnotmatch '^[a-f0-9]{64}$' -or "$($a.bytes)" -notmatch '^\d+$') { throw 'Invalid/duplicate Naval inventory path/hash' }
        $file = Join-Path $root $p
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -ne $a.bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $a.sha256) { throw 'Missing or stale Naval asset' }
        [void]$listed.Add($p)
    }
    if (-not $listed.Contains('index.html') -or @($listed | Where-Object { $_ -cmatch '^assets/.+\.js$' }).Count -eq 0 -or @($listed | Where-Object { $_ -cmatch '^assets/.+\.css$' }).Count -eq 0) { throw 'Incomplete Naval inventory' }
    $prefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $actualFolded = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($n in $nodes | Where-Object { $_.FullName -cne $root }) {
        $p = $n.FullName.Substring($prefix.Length).Replace('\','/')
        if (-not $actualFolded.Add($p)) { throw 'Case-colliding Naval path' }
        if ($n.PSIsContainer) { if ($p -cne 'assets') { throw 'Unexpected Naval directory' }; continue }
        [void]$actual.Add($p)
        if ($p -cne 'build-manifest.json' -and -not $listed.Contains($p)) { throw 'Unlisted Naval asset' }
        if ($p -cmatch '\.js$') {
            # Vite local lazy targets, including template-literal imports/preload maps.
            foreach ($match in [regex]::Matches([IO.File]::ReadAllText($n.FullName), '(?:\./)?(chunk-[A-Za-z0-9_-]+\.js)')) {
                if (-not $listed.Contains('assets/' + $match.Groups[1].Value)) { throw 'Missing dynamic-import target' }
            }
        }
    }
    foreach ($p in $listed) { if (-not $actual.Contains($p)) { throw 'Naval declared spelling differs from actual path' } }
    $html = [IO.File]::ReadAllText((Join-Path $root 'index.html'))
    foreach ($match in [regex]::Matches($html, '(?:src|href)="/naval/([^"]+)"')) { if (-not $listed.Contains($match.Groups[1].Value)) { throw 'Missing Naval entry target' } }
    return [ordered]@{sha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant(); artifacts=$listed.Count}
}
