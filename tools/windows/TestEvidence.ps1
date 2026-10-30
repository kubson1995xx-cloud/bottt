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
