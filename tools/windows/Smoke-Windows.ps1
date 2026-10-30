#Requires -Version 5.1
<# Legacy-config actual EXE smoke. Not first-run/modal/default-shell/browser acceptance. #>
param([Parameter(Mandatory=$true)][string]$Pakiet,[string]$Evidence='')
$ErrorActionPreference='Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Native Windows required; cross-publish is not acceptance' }
$Pakiet=(Get-Item -LiteralPath (Resolve-Path -LiteralPath $Pakiet).Path).FullName
. (Join-Path $Pakiet 'TestEvidence.ps1')
& (Join-Path $Pakiet 'Sprawdz-Pakiet.ps1') -Pakiet $Pakiet
if ($Evidence) {
    $Evidence=[IO.Path]::GetFullPath($Evidence)
    if (Test-Path -LiteralPath $Evidence) { throw 'Evidence directory must be new' }
    if ($Evidence.Equals($Pakiet,[StringComparison]::OrdinalIgnoreCase) -or $Evidence.StartsWith($Pakiet.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must be outside sealed package' }
    New-Item -ItemType Directory -Path $Evidence | Out-Null
}
function Hash([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
function Inventory { @(Get-ChildItem -LiteralPath $Pakiet -Recurse -Force -File | Sort-Object FullName | ForEach-Object { $_.FullName.Substring($Pakiet.Length+1).Replace('\','/')+' '+(Hash $_.FullName) }) }
function Assert-Private([string]$Path,[bool]$Protected) {
    $acl=Get-Acl -LiteralPath $Path
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $owner=$acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if (@($sid,'S-1-5-18','S-1-5-32-544') -notcontains $owner -or ($Protected -and -not $acl.AreAccessRulesProtected)) { throw 'Extraction owner/protected ACL invalid' }
    $rules=@($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
    if ($rules.Count -eq 0) { throw 'Extraction empty ACL invalid' }
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -ne 'Allow' -or @($sid,'S-1-5-18','S-1-5-32-544') -notcontains $rule.IdentityReference.Value) { throw 'Extraction broad/unsupported ACL invalid' }
    }
}
$before=Inventory
$publish=Test-ApexWindowsPublish (Join-Path $Pakiet 'APEX-Desktop') (Join-Path $Pakiet 'Weryfikacja/windows-publish.json')
$meta=Get-Content (Join-Path $Pakiet 'apex-build.json') -Raw|ConvertFrom-Json
$listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0);$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
# Native private creation, external to package, under the actual user's LocalAppData.
$data=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('ApexSmoke-'+[Guid]::NewGuid().ToString('N'))
New-ApexPrivateBuildDirectory $data
$extract=Join-Path $data 'bundle';New-ApexPrivateBuildDirectory $extract
$empty=Join-Path $data 'empty-runtime';New-ApexPrivateBuildDirectory $empty
$proc=$null;$stdout=$null;$stderr=$null;$passed=$false;$modules=@();$aclCount=0
try {
    $token=[Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N')
    $config=@{HttpPort=$port;BridgeTcpPort=0;ApiToken=$token;PairingKey=[Guid]::NewGuid().ToString('N');OpenBrowserOnStart=$false;News=@{Enabled=$false}}
    [IO.File]::WriteAllText((Join-Path $data 'apex.config.json'),($config|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    $exe=Join-Path $Pakiet 'APEX-Desktop/ApexDesktop.exe'
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$exe;$start.Arguments='--data "'+$data+'" --no-browser';$start.UseShellExecute=$false
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true;$start.CreateNoWindow=$true
    # No parent PATH/runtime environment mutation. Parent SDK remains available.
    $start.EnvironmentVariables['DOTNET_ROOT']=$empty
    $start.EnvironmentVariables['DOTNET_ROOT_X64']=$empty
    $start.EnvironmentVariables['DOTNET_ROOT(x86)']=$empty
    $start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP']='0'
    $start.EnvironmentVariables['DOTNET_SHARED_STORE']=$empty
    $start.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR']=$extract
    $start.EnvironmentVariables['PATH']=(Join-Path $env:WINDIR 'System32')+';'+$env:WINDIR
    foreach ($dir in $start.EnvironmentVariables['PATH'].Split(';')) { if (Test-Path (Join-Path $dir 'dotnet.exe')) { throw 'Child PATH unexpectedly contains dotnet' } }
    Assert-Private $data $true;Assert-Private $extract $true
    $proc=[Diagnostics.Process]::Start($start)
    $stdout=$proc.StandardOutput.ReadToEndAsync();$stderr=$proc.StandardError.ReadToEndAsync()
    $deadline=[DateTime]::UtcNow.AddSeconds(45);$status=$null
    do {
        if ($proc.HasExited) { throw 'EXE exited before HTTP readiness; private output not uploaded' }
        try { $status=Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/status" -Headers @{'X-Apex-Token'=$token} -TimeoutSec 2 }
        catch { Start-Sleep -Milliseconds 100 }
    } while ($null -eq $status -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $status) { throw 'No actual EXE HTTP response' }
    if (@($status.data.accounts).Count -ne 0 -or $status.host.liveFeature) { throw 'Unexpected account/LIVE in legacy smoke' }
    $html=(Invoke-WebRequest -Uri "http://127.0.0.1:$port/" -UseBasicParsing).Content
    if ($html -notmatch 'APEX Trading AI' -or $html -match '__APEX_TOKEN__') { throw 'Actual panel not served' }
    $js=(Invoke-WebRequest -Uri "http://127.0.0.1:$port/app.js" -UseBasicParsing).Content
    if ($js -match '/api/ai/|unlockLive|AI Lab') { throw 'Unwanted module in served panel' }
    # Inspect actual loaded runtime modules. OS ucrtbase is allowed, external VC is not.
    $extract=(Get-Item -LiteralPath $extract).FullName
    $osSystem=(Get-Item -LiteralPath (Join-Path $env:WINDIR 'System32')).FullName
    $proc.Refresh()
    foreach ($module in $proc.Modules) {
        $name=$module.ModuleName
        $record=@($publish.nativeLibraries|Where-Object { $_.name -ieq $name })
        if ($record.Count -eq 0 -and $name -notmatch '^(coreclr|clrjit|hostpolicy|hostfxr|vcruntime.*|msvcp.*|ucrtbase)\.dll$') { continue }
        $path=(Get-Item -LiteralPath $module.FileName).FullName
        if ($name -ieq 'ucrtbase.dll') {
            if (-not $path.Equals((Join-Path $osSystem 'ucrtbase.dll'),[StringComparison]::OrdinalIgnoreCase)) { throw 'ucrtbase did not originate in Windows System32' }
            $origin='Windows/System32/ucrtbase.dll';$kind='OS'
        } else {
            if (-not $path.StartsWith($extract+'\',[StringComparison]::OrdinalIgnoreCase) -or $record.Count -ne 1 -or (Hash $path) -cne $record[0].sha256 -or (Get-Item $path).Length -ne $record[0].bytes) { throw ('Non-OS runtime borrowed or provenance mismatch: '+$name) }
            $origin='bundle/'+$path.Substring($extract.Length+1).Replace('\','/');$kind='authenticated-runtime-pack'
        }
        $modules+=@{name=$name;origin=$origin;kind=$kind;sha256=(Hash $path)}
    }
    if (@($modules|Where-Object { $_.name -ieq 'coreclr.dll' -and $_.kind -eq 'authenticated-runtime-pack' }).Count -ne 1) { throw 'No actual bundled coreclr module' }
    foreach ($item in @((Get-Item -LiteralPath $extract))+@(Get-ChildItem -LiteralPath $extract -Recurse -Force)) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Extraction reparse point forbidden' }
        Assert-Private $item.FullName ($item.FullName -ceq $extract);$aclCount++
    }
    if ($aclCount -le 1) { throw 'No runtime extraction was inspected' }
    $passed=$true
} finally {
    if ($null -ne $proc) { if (-not $proc.HasExited) { $proc.Kill() };$proc.WaitForExit();$proc.Dispose() }
    # Output can contain legacy bootstrap credentials; NEVER publish it.
    if ($null -ne $stdout) { $null=$stdout.GetAwaiter().GetResult();$null=$stderr.GetAwaiter().GetResult() }
    $after=Inventory
    if (($before -join "`n") -cne ($after -join "`n")) { $passed=$false;throw 'Package changed during smoke' }
    Remove-Item -LiteralPath $data -Recurse -Force
    if (Test-Path -LiteralPath $data) { throw 'Owned private smoke directory not removed' }
    if (@(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue).Count -ne 0) { throw 'Owned HTTP listener survived smoke' }
    & (Join-Path $Pakiet 'Sprawdz-Pakiet.ps1') -Pakiet $Pakiet
    if ($Evidence) {
        $summary=[ordered]@{schema='apex-windows-smoke-v1';status=$(if($passed){'PASS'}else{'FAIL'});sourceCommit=$meta.sourceCommit;sourceInputsSha256=$meta.sourceInputsSha256;exeSha256=$publish.exeSha256;osCaption=(Get-CimInstance Win32_OperatingSystem).Caption;osVersion=[Environment]::OSVersion.Version.ToString();childRuntimeRootsEmpty=$true;childPathHasDotnet=$false;machineDotnetUsed=$(if($passed){$false}else{$null});machineVcRedistributableUsed=$(if($passed){$false}else{$null});modules=$modules;hostpolicyLoaded=(@($modules|Where-Object name -ieq 'hostpolicy.dll').Count -gt 0);hostpolicyNote='Single-file apphost can statically link hostpolicy; report actual loaded module presence, never infer a shared DLL';extractionAclItems=$aclCount;extractionPrivate=$passed;zeroAccounts=$passed;servedLegacyPanel=$passed;packageUnchanged=$true;ownedProcessExited=$true;ownedPrivateDataRemoved=$true;firstRunModalTested=$false;defaultBrowserTested=$false;browserRenderingTested=$false;brokerTested=$false}
        [IO.File]::WriteAllText((Join-Path $Evidence 'summary.json'),($summary|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    }
}
if (-not $passed) { throw 'Actual legacy EXE smoke failed' }
Write-Host 'WINDOWS LEGACY EXE SMOKE PASS: isolated child runtime, module provenance/ACL, served panel, zero accounts. Not first-run/modal/browser/broker acceptance.'
