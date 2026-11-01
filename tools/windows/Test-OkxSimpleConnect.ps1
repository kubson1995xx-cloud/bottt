#requires -Version 5.1
param([ValidateSet('red','green','delivery')][string]$Phase='green')
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$e=Join-Path $env:RUNNER_TEMP 'okx-simple-connect';New-Item -ItemType Directory -Path $e|Out-Null
$sdk=Join-Path $e 'sdk';New-Item -ItemType Directory -Path $sdk|Out-Null
[IO.File]::WriteAllText((Join-Path $sdk 'global.json'),'{"sdk":{"version":"8.0.427","rollForward":"disable"}}')
$os=Get-CimInstance Win32_OperatingSystem
@{source=(& git -C $repo rev-parse HEAD);phase=$Phase;os=$os.Caption;version=$os.Version}|ConvertTo-Json|Set-Content (Join-Path $e 'identity.json') -Encoding UTF8
$inputs=@()
foreach($base in @('src','tests','tools','frontend/naval')) {
 foreach($f in Get-ChildItem (Join-Path $repo $base) -Recurse -File) {
  if($f.FullName -notmatch '[\\/](?:bin|obj|node_modules|dist)[\\/]') {
   $inputs+=@{path=$f.FullName.Substring($repo.Length+1);sha256=(Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
  }
 }
}
$inputs|ConvertTo-Json -Depth 4|Set-Content (Join-Path $e 'inputs.json') -Encoding UTF8
$dotnet=(Get-Command dotnet).Source
Push-Location $sdk
try {
 if ((& $dotnet --version) -cne '8.0.427'){throw 'SDK pin failed'}
 & $dotnet build (Join-Path $repo 'tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj') -c Release *> (Join-Path $e 'build.log');if($LASTEXITCODE -ne 0){throw 'Native build failed'}
 Copy-Item (Join-Path $repo 'tests/Apex.Windows.Tests/bin/Release/net8.0') (Join-Path $e 'native-runtime') -Recurse
 & $dotnet test (Join-Path $e 'native-runtime/Apex.Windows.Tests.dll') --filter 'Gate=SimpleConnectSource' --logger 'trx;LogFileName=native.trx' --results-directory $e
 if($LASTEXITCODE -ne 0){throw 'Native behavioral gate failed; exact inputs and runtime retained'}
}finally{Pop-Location}
