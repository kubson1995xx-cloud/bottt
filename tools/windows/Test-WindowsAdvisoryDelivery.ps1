param([string]$Prior)
$ErrorActionPreference='Stop'
$repo=$env:GITHUB_WORKSPACE;$e=Join-Path $env:RUNNER_TEMP 'windows-local-package';New-Item -ItemType Directory -Path $e|Out-Null
$inputs=Join-Path $e 'source';New-Item -ItemType Directory -Path $inputs|Out-Null
$files=@(Get-ChildItem (Join-Path $repo 'src'),(Join-Path $repo 'tests'),(Join-Path $repo 'tools/advisory'),(Join-Path $repo 'tools/windows') -Recurse -File|Where-Object{$_.FullName -notmatch '[\\/](bin|obj|node_modules|__pycache__)[\\/]'})
$rows=@($files|ForEach-Object{$rel=[IO.Path]::GetRelativePath($repo,$_.FullName);$out=Join-Path $inputs $rel;New-Item -ItemType Directory -Path (Split-Path $out) -Force|Out-Null;Copy-Item $_.FullName $out;[ordered]@{path=$rel;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}});$rows|ConvertTo-Json|Set-Content (Join-Path $e 'input-map.json')
$env:APEX_ADVISORY_PRIOR=$Prior
& (Join-Path $repo 'tools/windows/Test-WindowsLocalPackage.ps1')
