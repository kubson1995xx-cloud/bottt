#Requires -Version 5.1
<# Test uruchomienia PRAWDZIWEGO EXE na Windows. Bez polaczenia z brokerem i bez transakcji. #>
param([Parameter(Mandatory = $true)][string]$Pakiet)
$ErrorActionPreference = "Stop"
if ($env:OS -ne 'Windows_NT') { throw "Ten test wymaga Windows. Kompilacja krzyzowa nie wystarcza." }
$Pakiet = (Resolve-Path -LiteralPath $Pakiet).Path
& (Join-Path $Pakiet 'Sprawdz-Pakiet.ps1') -Pakiet $Pakiet
$listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
# Logs/config stay outside the sealed package. TCP port0 is ephemeral, not disabled.
$data = Join-Path ([IO.Path]::GetTempPath()) ('APEX smoke ' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data | Out-Null
$proc = $null
try {
    $token = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
    $config = @{ HttpPort = $port; BridgeTcpPort = 0; ApiToken = $token; PairingKey = [Guid]::NewGuid().ToString('N'); OpenBrowserOnStart = $false; News = @{ Enabled = $false } }
    [IO.File]::WriteAllText((Join-Path $data 'apex.config.json'), ($config | ConvertTo-Json), [Text.Encoding]::UTF8)
    $arguments = '--data "' + $data + '" --no-browser'
    $proc = Start-Process -FilePath (Join-Path $Pakiet 'APEX-Desktop/ApexDesktop.exe') -ArgumentList $arguments -PassThru -RedirectStandardOutput (Join-Path $data 'stdout.log') -RedirectStandardError (Join-Path $data 'stderr.log')
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $status = $null
    do {
        if ($proc.HasExited) { throw "EXE zakonczyl sie przed odpowiedzia HTTP. Zobacz $data" }
        try { $status = Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/status" -Headers @{ 'X-Apex-Token' = $token } -TimeoutSec 2 }
        catch { Start-Sleep -Milliseconds 300 }
    } while ($null -eq $status -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $status) { throw "Brak odpowiedzi EXE. Zobacz $data" }
    if (@($status.data.accounts).Count -ne 0) { throw "Nieoczekiwane konto w izolowanym tescie." }
    if ($status.host.liveFeature) { throw "LIVE nie moze byc aktywne." }
    $html = (Invoke-WebRequest -Uri "http://127.0.0.1:$port/" -UseBasicParsing).Content
    if ($html -notmatch 'APEX Trading AI' -or $html -match '__APEX_TOKEN__') { throw "Panel nie zostal poprawnie podany przez EXE." }
    $js = (Invoke-WebRequest -Uri "http://127.0.0.1:$port/app.js" -UseBasicParsing).Content
    if ($js -match '/api/ai/|unlockLive|AI Lab') { throw "Pozostalosci niechcianych modulow w panelu." }
    Write-Host 'WINDOWS EXE SMOKE: PASS. Prawdziwy proces, HTTP, panel, zero kont i zero zlecen. Broker NIE byl testowany.'
} finally {
    if ($null -ne $proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; $proc.WaitForExit() }
    # Tylko wlasny losowy katalog testowy, nigdy dane uzytkownika.
    if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
}
