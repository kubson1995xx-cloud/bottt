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
