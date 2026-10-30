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

# Exact bounded Vite inventory, also used on dist/staging/build/publish/extraction.
function Test-ApexNavalAssets([string]$NavalRoot) {
    $root = [IO.Path]::GetFullPath($NavalRoot)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'Missing required Naval build' }
    $nodes = @((Get-Item -LiteralPath $root -Force)) + @(Get-ChildItem -LiteralPath $root -Force -Recurse)
    foreach ($n in $nodes) { if (($n.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Naval link/reparse forbidden' } }
    $manifestPath = Join-Path $root 'build-manifest.json'
    $m = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    if ($m.schema -cne 'naval-build-v1' -or @($m.artifacts).Count -lt 3) { throw 'Invalid Naval inventory schema' }
    $listed = @{}
    foreach ($a in $m.artifacts) {
        $p = [string]$a.path
        if ($p -cnotmatch '^(index\.html|assets/[A-Za-z0-9_-]+\.(js|css))$' -or $listed.ContainsKey($p) -or $a.sha256 -cnotmatch '^[a-f0-9]{64}$' -or "$($a.bytes)" -notmatch '^\d+$') { throw 'Invalid/duplicate Naval inventory path/hash' }
        $file = Join-Path $root $p
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -ne $a.bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $a.sha256) { throw 'Missing or stale Naval asset' }
        $listed[$p] = $true
    }
    if (-not $listed.ContainsKey('index.html') -or @($listed.Keys | Where-Object { $_ -cmatch '^assets/.+\.js$' }).Count -eq 0 -or @($listed.Keys | Where-Object { $_ -cmatch '^assets/.+\.css$' }).Count -eq 0) { throw 'Incomplete Naval inventory' }
    $prefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($n in $nodes | Where-Object { $_.FullName -ne $root }) {
        $p = $n.FullName.Substring($prefix.Length).Replace('\','/')
        if ($n.PSIsContainer) { if ($p -cne 'assets') { throw 'Unexpected Naval directory' }; continue }
        if ($p -cne 'build-manifest.json' -and -not $listed.ContainsKey($p)) { throw 'Unlisted Naval asset' }
        if ($p -cmatch '\.js$') {
            # Vite local lazy targets, including template-literal imports/preload maps.
            foreach ($match in [regex]::Matches([IO.File]::ReadAllText($n.FullName), '(?:\./)?(chunk-[A-Za-z0-9_-]+\.js)')) {
                if (-not $listed.ContainsKey('assets/' + $match.Groups[1].Value)) { throw 'Missing dynamic-import target' }
            }
        }
    }
    $html = [IO.File]::ReadAllText((Join-Path $root 'index.html'))
    foreach ($match in [regex]::Matches($html, '(?:src|href)="/naval/([^"]+)"')) { if (-not $listed.ContainsKey($match.Groups[1].Value)) { throw 'Missing Naval entry target' } }
    return [ordered]@{sha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant(); artifacts=$listed.Count}
}
