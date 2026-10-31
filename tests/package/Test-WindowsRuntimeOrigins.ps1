#Requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Source,[Parameter(Mandatory=$true)][string]$EvidencePath)
$ErrorActionPreference='Stop'
. (Join-Path $Source 'tools/windows/TestEvidence.ps1')
if (Test-Path -LiteralPath $EvidencePath) { throw 'Origin evidence must be new' }
# Pure classifier controls only: no fake DLL is loaded or claimed as Windows proof.
$system=Join-Path ([IO.Path]::GetFullPath($Source)) 'controlled-System32'
$cases=@(
    @{name='OS-ucrtbase-exact';dll='ucrtbase.dll';path=(Join-Path $system 'ucrtbase.dll');expected=$true},
    @{name='OS-msvcp_win-exact';dll='msvcp_win.dll';path=(Join-Path $system 'msvcp_win.dll');expected=$true},
    @{name='OS-case-insensitive';dll='MSVCP_WIN.DLL';path=(Join-Path $system 'MSVCP_WIN.DLL');expected=$true},
    @{name='Redistributable-msvcp140-never-OS';dll='msvcp140.dll';path=(Join-Path $system 'msvcp140.dll');expected=$false},
    @{name='Redistributable-vcruntime-never-OS';dll='vcruntime140.dll';path=(Join-Path $system 'vcruntime140.dll');expected=$false},
    @{name='Coreclr-never-OS';dll='coreclr.dll';path=(Join-Path $system 'coreclr.dll');expected=$false},
    @{name='OS-name-outside-System32';dll='msvcp_win.dll';path=(Join-Path ($system+'-other') 'msvcp_win.dll');expected=$false},
    @{name='OS-name-subdirectory';dll='msvcp_win.dll';path=(Join-Path $system 'sub/msvcp_win.dll');expected=$false},
    @{name='OS-name-other-basename';dll='msvcp_win.dll';path=(Join-Path $system 'msvcp140.dll');expected=$false},
    @{name='OS-ucrtbase-outside';dll='ucrtbase.dll';path=(Join-Path ($system+'-other') 'ucrtbase.dll');expected=$false},
    @{name='Arbitrary-System32-DLL-never-exempt';dll='unexpected.dll';path=(Join-Path $system 'unexpected.dll');expected=$false}
)
$results=@(foreach ($case in $cases) {
    $reason='';$pass=$false
    try { $actual=Test-ApexWindowsOsRuntimeModule $case.dll $case.path $system;$pass=($actual -ceq $case.expected);if(-not $pass){$reason='Unexpected classification'} } catch { $reason=$_.Exception.Message }
    [ordered]@{name=$case.name;pass=$pass;reason=$reason}
})
[IO.File]::WriteAllText($EvidencePath,($results|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
$failed=@($results|Where-Object {-not $_.pass})
if ($failed.Count) { $failed|ForEach-Object {Write-Host ('FAIL '+$_.name+': '+$_.reason)};throw 'Runtime origin controls failed' }
Write-Host ('RUNTIME ORIGIN CONTROLS PASS: '+$results.Count+' pure controls; not native EXE proof')
