# Two real clients exercise My rooms, password create/join, title host/join, and gameplay over a localhost C-server
# (T-159: rooms play through the C-server; the relay is gone).
# Checks PerformSave upload, guest backup, return-to-title snapshot and lease release. No Steam.
# Uses the caller's ORICOOP_TEST_DLL; builds only RoomServer and CServer (Get-TestServerExe) when needed.
param([switch]$Force)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'test-launch.ps1')
# Like the other suites: without ORICOOP_TEST_DLL it tests the DLL installed in the copy it gets (T-084: the throw
# here made run-affected's rooms suite end in 10 s having tested nothing).
if (-not $env:ORICOOP_TEST_DLL) { 'NOTE: ORICOOP_TEST_DLL not set; testing the DLL installed in the test copy' }
Start-TestRun -Force:$Force
$serverProc = $null
$cserverProc = $null
$ok = $false
try {
    $run = Join-Path $TL_TestDir ('rooms-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $run -Force | Out-Null
    $share = Join-Path $run 'share.dat'
    $roomPort = 47021 + $TL_PortOffset
    $url = "http://127.0.0.1:$roomPort/"
    $cserver = $env:ORICOOP_NET_CSERVER
    if ($cserver -and $cserver -notmatch '^127\.0\.0\.1:([0-9]+)$') {
        throw 'ORICOOP_NET_CSERVER must be 127.0.0.1:port'
    }
    $exe = Get-TestServerExe 'RoomServer'   # built from this checkout, not written into the shared src\RoomServer (T-088)
    $serverProc = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden `
        -ArgumentList @('--prefix', $url, '--data', ('"' + (Join-Path $run 'server') + '"')) `
        -RedirectStandardOutput (Join-Path $run 'server.log') `
        -RedirectStandardError (Join-Path $run 'server-error.log')
    if (-not $cserver) {
        $csPort = 47022 + $TL_PortOffset
        $cserver = "127.0.0.1:$csPort"
        $cserverProc = Start-Process -FilePath (Get-TestServerExe 'CServer') -ArgumentList ('--port ' + $csPort) -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $run 'cserver.log') `
            -RedirectStandardError (Join-Path $run 'cserver-error.log')
    }
    "ROOMS services: $url cserver=$cserver"
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
        if ($cserverProc -and $cserverProc.HasExited) { throw 'C-server exited; inspect cserver-error.log' }
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
                ORICOOP_NET = 'off'; ORICOOP_NET_CSERVER = $cserver; ORICOOP_NET_ROOM = ''
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
    if ($cserverProc -and -not $cserverProc.HasExited) { $cserverProc.Kill(); $cserverProc.WaitForExit() }
    Complete-TestRun
}
# One verdict line for callers (run-affected): the per-step "ROOMS PASS n" lines are no verdict (T-082).
if ($ok) { 'ROOMS RESULT PASS' } else { 'ROOMS RESULT FAIL' }
Exit-TestRun   # LAST: releases the copy lock; exit 7 if this run changed the owner's game settings (T-094, T-117)
if ($ok) { exit 0 }
exit 1
