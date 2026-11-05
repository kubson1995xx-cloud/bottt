#Requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Source,[Parameter(Mandatory=$true)][string]$Scratch)
$ErrorActionPreference='Stop'
. (Join-Path $Source 'tools/windows/TestEvidence.ps1')
if (Test-Path -LiteralPath $Scratch) { throw 'Static-host controls require new scratch' }
New-Item -ItemType Directory -Path $Scratch|Out-Null
# Explicit parser fixture, NOT product/Windows/native execution evidence.
$exe=Join-Path $Scratch 'controlled.exe';$b=[byte[]]::new(1024)
[BitConverter]::GetBytes([uint16]0x5a4d).CopyTo($b,0)
[BitConverter]::GetBytes([uint32]128).CopyTo($b,60)
[BitConverter]::GetBytes([uint32]0x4550).CopyTo($b,128)
[BitConverter]::GetBytes([uint16]0x8664).CopyTo($b,132)
[BitConverter]::GetBytes([uint16]1).CopyTo($b,134)
[BitConverter]::GetBytes([uint16]240).CopyTo($b,148)
[BitConverter]::GetBytes([uint16]0x20b).CopyTo($b,152)
[Text.Encoding]::ASCII.GetBytes('.text').CopyTo($b,392)
[BitConverter]::GetBytes([uint32]512).CopyTo($b,408)
[BitConverter]::GetBytes([uint32]512).CopyTo($b,412)
[BitConverter]::GetBytes([uint32]0x60000020).CopyTo($b,428)
$b[512]=195;[IO.File]::WriteAllBytes($exe,$b)
$results=New-Object Collections.Generic.List[object]
function Case([string]$Name,[scriptblock]$Body) {try {& $Body;$results.Add(@{name=$Name;pass=$true})}catch{$results.Add(@{name=$Name;pass=$false;reason=$_.Exception.Message});Write-Host ('FAIL '+$Name+': '+$_.Exception.Message)}}
function Reject([scriptblock]$Body){$rejected=$false;try{& $Body|Out-Null}catch{$rejected=$true};if(-not $rejected){throw 'Invalid static-host control accepted'}}
Case 'PE-code-section-real-bytes' {
 $sections=@(Read-ApexPeExecutableSections $exe)
 $sha=[Security.Cryptography.SHA256]::Create();try{$expected=([BitConverter]::ToString($sha.ComputeHash($b,512,512))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
 if($sections.Count -ne 1 -or $sections[0].name -cne '.text' -or $sections[0].bytes -ne 512 -or $sections[0].sha256 -cne $expected){throw 'Code section mismatch'}
}
$proof=[pscustomobject]@{pack='Microsoft.NETCore.App.Host.win-x64';version='8.0.33';templateSha256=('1'*64);runtimeListSha256=('2'*64);droppedNativeComponents=@('coreclr.dll','clrjit.dll','hostfxr.dll','hostpolicy.dll');codeSections=@()}
Case 'Static-host-positive-control' {$proof.codeSections=@(Read-ApexPeExecutableSections $exe);Test-ApexWindowsStaticHost $exe $proof|Out-Null}
Case 'Changed-code-refused' {$changed=[byte[]]$b.Clone();$changed[512]=0;[IO.File]::WriteAllBytes($exe,$changed);try{Reject {Test-ApexWindowsStaticHost $exe $proof}}finally{[IO.File]::WriteAllBytes($exe,$b)}}
Case 'Noncode-apphost-patch-preserves-code' {$changed=[byte[]]$b.Clone();$changed[500]=1;[IO.File]::WriteAllBytes($exe,$changed);try{Test-ApexWindowsStaticHost $exe $proof|Out-Null}finally{[IO.File]::WriteAllBytes($exe,$b)}}
Case 'Missing-proof-refused' {Reject {Test-ApexWindowsStaticHost $exe $null}}
Case 'Missing-coreclr-drop-refused' {$old=$proof.droppedNativeComponents;$proof.droppedNativeComponents=@('clrjit.dll','hostfxr.dll','hostpolicy.dll');try{Reject {Test-ApexWindowsStaticHost $exe $proof}}finally{$proof.droppedNativeComponents=$old}}
Case 'Wrong-pack-refused' {$old=$proof.pack;$proof.pack='Other';try{Reject {Test-ApexWindowsStaticHost $exe $proof}}finally{$proof.pack=$old}}
Case 'Corrupt-PE-refused' {[IO.File]::WriteAllText($exe,'CONTROLLED_NOT_PRODUCT');try{Reject {Read-ApexPeExecutableSections $exe}}finally{[IO.File]::WriteAllBytes($exe,$b)}}
Case 'Out-of-range-PE-section-refused' {$changed=[byte[]]$b.Clone();[BitConverter]::GetBytes([uint32]4096).CopyTo($changed,412);[IO.File]::WriteAllBytes($exe,$changed);try{Reject {Read-ApexPeExecutableSections $exe}}finally{[IO.File]::WriteAllBytes($exe,$b)}}
[IO.File]::WriteAllText((Join-Path $Scratch 'results.json'),($results.ToArray()|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
if(@($results|Where-Object {-not $_.pass}).Count){throw 'Static-host controls failed'}
Write-Host ('STATIC HOST CONTROLS PASS: '+$results.Count+' parser controls, not native Windows proof')
