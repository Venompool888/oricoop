# Two real clients exercise My rooms, password create/join, title host/join, and gameplay over a localhost relay.
# Checks PerformSave upload, guest backup, return-to-title snapshot and lease release. No Steam.
# Uses the caller's ORICOOP_TEST_DLL; builds only RoomServer and a loopback-only local relay when needed.
param([switch]$Force)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'test-launch.ps1')
# Like the other suites: without ORICOOP_TEST_DLL it tests the DLL installed in the copy it gets (T-084: the throw
# here made run-affected's rooms suite end in 10 s having tested nothing).
if (-not $env:ORICOOP_TEST_DLL) { 'NOTE: ORICOOP_TEST_DLL not set; testing the DLL installed in the test copy' }
Start-TestRun -Force:$Force
$serverProc = $null
$relayProc = $null
$ok = $false
try {
    $run = Join-Path $TL_TestDir ('rooms-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $run -Force | Out-Null
    $share = Join-Path $run 'share.dat'
    $roomPort = 47021 + $TL_PortOffset
    $url = "http://127.0.0.1:$roomPort/"
    $relay = $env:ORICOOP_NET_RELAY
    if ($relay -and $relay -notmatch '^127\.0\.0\.1:([0-9]+)$') {
        throw 'ORICOOP_NET_RELAY must be 127.0.0.1:port'
    }
    $csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $serverDir = Join-Path $PSScriptRoot 'RoomServer'
    $exe = Join-Path $serverDir 'RoomServer.exe'
    & $csc /nologo /target:exe /optimize+ /langversion:5 "/out:$exe" `
        (Join-Path $serverDir 'RoomServer.cs') (Join-Path $PSScriptRoot 'OriCoop\Rooms\RoomWire.cs')
    if ($LASTEXITCODE -ne 0) { throw 'RoomServer compilation failed' }
    $serverProc = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden `
        -ArgumentList @('--prefix', $url, '--data', ('"' + (Join-Path $run 'server') + '"')) `
        -RedirectStandardOutput (Join-Path $run 'server.log') `
        -RedirectStandardError (Join-Path $run 'server-error.log')
    if (-not $relay) {
        $port = 47020 + $TL_PortOffset
        $relay = "127.0.0.1:$port"
        # The stock executable binds Any; adapt only the bind in a temporary source copy.
        $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'RelayServer\RelayServer.cs'))
        $bind = 'socket.Bind(new IPEndPoint(IPAddress.Any, port));'
        if (-not $source.Contains($bind)) { throw 'Relay bind changed; review localhost adaptation' }
        $localSource = Join-Path $run 'RelayServer.cs'
        [IO.File]::WriteAllText($localSource,
            $source.Replace($bind, 'socket.Bind(new IPEndPoint(IPAddress.Loopback, port));'))
        $relayExe = Join-Path $run 'RelayServer.exe'
        & $csc /nologo /target:exe /optimize+ /langversion:5 "/out:$relayExe" $localSource `
            (Join-Path $PSScriptRoot 'RelayServer\RelayPlayerLog.cs')
        if ($LASTEXITCODE -ne 0) { throw 'Local relay compilation failed' }
        $relayProc = Start-Process -FilePath $relayExe -ArgumentList "$port" -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $run 'relay.log') `
            -RedirectStandardError (Join-Path $run 'relay-error.log')
    }
    "ROOMS services: $url relay=$relay"
    $fixture = 'C:\OriCoop\save-backup\20261114-150138'
    if (-not (Test-Path (Join-Path $fixture 'saveFile0.sav'))) { throw 'Missing host slot 0 fixture' }
    foreach ($i in 0, 1) {
        $saves = Join-Path $TL_AppData[$i] 'Ori and the Blind Forest DE'
        New-Item -ItemType Directory -Path $saves -Force | Out-Null
        Get-ChildItem -LiteralPath $saves -File | Where-Object {
            $_.Name -like 'saveFile*' -or $_.Name -like '*.oricoop*' -or $_.Name -eq 'oricoop-player.txt'
        } | Remove-Item -Force
        if ($i -eq 0) { Copy-Item (Join-Path $fixture '*') $saves -Force }
    }
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Invoke-TestWatch
        if ($serverProc.HasExited) { throw 'Room server exited; inspect server-error.log' }
        if ($relayProc -and $relayProc.HasExited) { throw 'Relay exited; inspect relay-error.log' }
        $tcp = New-Object Net.Sockets.TcpClient
        try { $tcp.Connect('127.0.0.1', $roomPort); $ready = $true } catch { } finally { $tcp.Close() }
        if ($ready) { break }
        Wait-TestSeconds 1
    }
    if (-not $ready) { throw 'Room server did not listen on localhost' }
    $specs = @(foreach ($i in 0, 1) {
        $role = if ($i -eq 0) { 'host' } else { 'join' }
        @{
            Tag = "rooms-$role"
            Arguments = '-screen-fullscreen 0 -screen-width 640 -screen-height 360'
            Vars = @{
                LOCALAPPDATA = $TL_AppData[$i]; ORICOOP_TEST = '1'; ORICOOP_NO_STEAM = '1'
                ORICOOP_ROOMS_TEST = $role; ORICOOP_ROOMS_SERVER = $url; ORICOOP_ROOMS_SHARE = $share
                ORICOOP_ROOMS_DIR = (Join-Path $run "store-$role"); ORICOOP_ROOMS_POLL = '1'
                ORICOOP_TEST_STEAMID = $(if ($i -eq 0) { '76561198000000001' } else { '76561198000000002' })
                ORICOOP_TEST_NICK = "Rooms-$role"; ORICOOP_PLAYER_NAME = "Rooms-$role"
                ORICOOP_NET = 'off'; ORICOOP_NET_RELAY = $relay; ORICOOP_NET_ROOM = ''
                ORICOOP_TITLEJOIN = ''; ORICOOP_COOPSAVE_TEST = ''; ORICOOP_NET_SIM = ''
            }
        }
    })
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $clients = @(Start-TestClients $specs)
    $logs = @((Get-ClientLog 'rooms-host'), (Get-ClientLog 'rooms-join'))
    while ($watch.Elapsed.TotalSeconds -lt 360) {
        Invoke-TestWatch
        $finished = @($logs | Where-Object {
            (Test-Path -LiteralPath $_) -and (Select-String -LiteralPath $_ -Pattern 'ROOMS DONE (ok|fail)')
        })
        if ($finished.Count -eq 2) { break }
        if (@($clients | Where-Object { $_.HasExited }).Count -gt 0) { break }
        Wait-TestSeconds 1
    }
    $ok = $true
    foreach ($log in $logs) {
        "ROOMS log: $log"
        $lines = if (Test-Path -LiteralPath $log) { @(Get-Content -LiteralPath $log) } else { @() }
        $lines | Where-Object { $_ -match 'ROOMS (PASS|FAIL|DONE)' }
        if (-not ($lines -match 'ROOMS DONE ok') -or ($lines -match 'ROOMS (FAIL|DONE fail)')) { $ok = $false }
    }
} finally {
    if ($serverProc -and -not $serverProc.HasExited) { $serverProc.Kill(); $serverProc.WaitForExit() }
    if ($relayProc -and -not $relayProc.HasExited) { $relayProc.Kill(); $relayProc.WaitForExit() }
    Complete-TestRun
}
# One verdict line for callers (run-affected): the per-step "ROOMS PASS n" lines are no verdict (T-082).
if ($ok) { 'ROOMS RESULT PASS'; exit 0 }
'ROOMS RESULT FAIL'
exit 1
