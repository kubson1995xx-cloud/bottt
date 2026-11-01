#Requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Download,[ValidateSet('original','delivery')][string]$Phase='original')
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'TestEvidence.ps1')
$e=Join-Path $env:RUNNER_TEMP 'okx-enrollment-evidence';New-Item -ItemType Directory -Path $e|Out-Null
$prior=Join-Path $env:RUNNER_TEMP 'okx-enrollment-prior'
& python (Join-Path $repo 'tests/package/safe_extract.py') (Join-Path $Download 'APEX-NAVAL-COMMAND-Windows-x64.zip') $prior (Join-Path $e 'prior.json') '8656ceba70026ae55fc1aa6e3d38ccd7f49a7f6a4da8368875011729745fbe0a'
if($LASTEXITCODE -ne 0){throw 'Pinned prior extraction failed'}
$dotnet=(Get-Command dotnet -CommandType Application).Source
$sdk=Join-Path $env:RUNNER_TEMP 'okx-enrollment-sdk';New-Item -ItemType Directory -Path $sdk|Out-Null
[IO.File]::WriteAllText((Join-Path $sdk 'global.json'),'{"sdk":{"version":"8.0.427","rollForward":"disable"}}')
$env:APEX_WINDOWS_EVIDENCE=$e;$env:APEX_PACKAGE_ROOT=$prior;$env:DOTNET_HOST_PATH=$dotnet
@{source=(& git -C $repo rev-parse HEAD);os=(Get-CimInstance Win32_OperatingSystem).Caption;phase=$Phase}|ConvertTo-Json|Set-Content (Join-Path $e 'identity.json') -Encoding UTF8
# Exact old native call in an owned background thread. No credential collection,
# password entry, policy changes, generated Windows accounts or provider replacement.
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;using System.Threading.Tasks;
public static class ApexNativeApprovalProbe {
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct Info{public int Size;public IntPtr Parent;public string Message;public string Caption;public IntPtr Banner;}
 [DllImport("credui.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]static extern uint CredUIPromptForWindowsCredentialsW(ref Info i,uint e,ref uint p,IntPtr b,uint n,out IntPtr o,out uint s,ref bool save,uint f);
 public static Task<uint> Start(){return Task.Run(()=>{var i=new Info{Size=Marshal.SizeOf(typeof(Info)),Caption="APEX — native diagnostic",Message="No credentials required by automated diagnostic; cancel if visible."};uint p=0,s=0;IntPtr o=IntPtr.Zero;bool save=false;try{return CredUIPromptForWindowsCredentialsW(ref i,0,ref p,IntPtr.Zero,0,out o,out s,ref save,0x1200);}finally{if(o!=IntPtr.Zero){for(uint x=0;x<s;x++)Marshal.WriteByte(o,(int)x,0);Marshal.FreeCoTaskMem(o);}}});}
}
'@
Push-Location (Join-Path $repo 'frontend/naval')
try{$old=$ErrorActionPreference;$ErrorActionPreference='Continue';& npm ci --ignore-scripts --no-audit --no-fund *> (Join-Path $e 'npm.log');$install=$LASTEXITCODE;& npm run build *> (Join-Path $e 'frontend.log');$build=$LASTEXITCODE;$ErrorActionPreference=$old;if($install -ne 0 -or $build -ne 0){throw 'Frontend setup failed'}}finally{Pop-Location}
Push-Location $sdk
try{
 if((& $dotnet --version) -cne '8.0.427'){throw 'SDK mismatch'}
 & $dotnet build (Join-Path $repo 'tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj') -c Release -p:NuGetAudit=false *> (Join-Path $e 'build.log');if($LASTEXITCODE -ne 0){throw 'Build setup failure, not RED'}
 $map=@(& git -C $repo ls-files|Where-Object{$_ -match '^(src/|tests/|tools/|frontend/|\.github/)'}|ForEach-Object{$p=Join-Path $repo $_;if(Test-Path $p -PathType Leaf){@{path=$_;sha256=(Get-FileHash $p -Algorithm SHA256).Hash.ToLowerInvariant()}}});$map|ConvertTo-Json -Depth 4|Set-Content (Join-Path $e 'inputs.json') -Encoding UTF8
 Copy-Item (Join-Path $repo 'tests/Apex.Windows.Tests/bin/Release/net8.0') (Join-Path $e 'runtime') -Recurse
 & $dotnet test (Join-Path $repo 'tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj') -c Release --no-build --no-restore --filter 'Gate=EnrollmentOriginal' --logger 'trx;LogFileName=original.trx' --results-directory $e
 if($LASTEXITCODE -ne 0){throw 'Reached original enrollment failure; see TRX'}
}finally{Pop-Location}
