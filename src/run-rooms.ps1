# Two real clients exercise 我的房间 (T-173) using the remote room service and C-server, all through the real title
# menu and pages (src\OriCoop\Rooms\RoomsHookTest.cs):
# fresh 我的房间 menu (新建房间 / 加入房间, no 房间列表); host: 新建房间 -> save slots -> co-op slot 1 -> pending open room
# (invite code, English name, 4 seats, ROOM >= 1001) on the room page; guest: 加入房间 page -> server + invite code ->
# the same room's page; ready + the creator's 开始 confirms the room and closes the code; the guest reopens the code
# (5 minutes). Then gameplay: PerformSave upload, guest backup, host adopt, return-to-title snapshot and lease release;
# last the guest's 房间列表 shows the room (name, ROOM <no>, 2/4). No Steam, no passwords.
# Uses the caller's ORICOOP_TEST_DLL; fresh test SteamIDs isolate parallel runs on the shared room server.
param([switch]$Force)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'test-launch.ps1')
# Like the other suites: without ORICOOP_TEST_DLL it tests the DLL installed in the copy it gets (T-084: the throw
# here made run-affected's rooms suite end in 10 s having tested nothing).
if (-not $env:ORICOOP_TEST_DLL) { 'NOTE: ORICOOP_TEST_DLL not set; testing the DLL installed in the test copy' }
$rooms = [uri]('http://' + (Get-RemoteTestServer 'rooms') + '/')
$cserver = Get-RemoteTestServer 'cserver'
Assert-TestServerCurrent 'rooms', 'cserver'
$steamBase = [long]76561198000000000 + 2 * (Get-Random -Minimum 1000 -Maximum 400000000)   # 17 digits, like a real SteamID64
$steamIds = @([string]$steamBase, [string]($steamBase + 1))
Start-TestRun -Force:$Force
$ok = $false
try {
    $run = Join-Path $TL_TestDir ('rooms-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $run -Force | Out-Null
    $share = Join-Path $run 'share.dat'
    'ROOMS services: remote room server and C-server'
    $fixture = 'C:\OriCoop\save-backup\20261114-150138'
    if (-not (Test-Path (Join-Path $fixture 'saveFile0.sav'))) { throw 'Missing host slot 0 fixture' }
    foreach ($i in 0, 1) {
        $saves = Join-Path $TL_AppData[$i] 'Ori and the Blind Forest DE'
        New-Item -ItemType Directory -Path $saves -Force | Out-Null
        $coop = Join-Path $saves 'OriCoop-coop'
        if (Test-Path -LiteralPath $coop) { Remove-Item -LiteralPath $coop -Recurse -Force }
        Get-ChildItem -LiteralPath $saves -File | Where-Object {
            $_.Name -like 'saveFile*' -or $_.Name -like '*.oricoop*' -or $_.Name -eq 'oricoop-player.txt'
        } | Remove-Item -Force
        if ($i -eq 0) {
            Copy-Item (Join-Path $fixture '*') $saves -Force
            # 新建房间 picks a co-op slot: the host's co-op slot 2 (saveFile1) holds the fixture save, a non-first slot so the
            # test proves the room is hosted from the PICKED slot (review F19), not from slot 1 by default.
            New-Item -ItemType Directory -Path $coop -Force | Out-Null
            Copy-Item (Join-Path $fixture 'saveFile0.sav') (Join-Path $coop 'saveFile1.sav') -Force
        }
    }
    $ready = $false
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        Invoke-TestWatch
        $tcp = New-Object Net.Sockets.TcpClient
        try { $tcp.Connect($rooms.DnsSafeHost, $rooms.Port); $ready = $true } catch { } finally { $tcp.Close() }
        if ($ready) { break }
        Wait-TestSeconds 1
    }
    if (-not $ready) { throw 'Remote test room server is not reachable (see src\test-servers\README.md).' }
    $specs = @(foreach ($i in 0, 1) {
        $role = if ($i -eq 0) { 'host' } else { 'join' }
        @{
            Tag = "rooms-$role"
            Arguments = '-screen-fullscreen 0 -screen-width 640 -screen-height 360'
            Vars = @{
                LOCALAPPDATA = $TL_AppData[$i]; ORICOOP_TEST = '1'; ORICOOP_NO_STEAM = '1'
                ORICOOP_ROOMS_TEST = $role; ORICOOP_ROOMS_SERVER = $rooms.AbsoluteUri; ORICOOP_ROOMS_SHARE = $share
                ORICOOP_ROOMS_DIR = (Join-Path $run "store-$role"); ORICOOP_ROOMS_POLL = '1'
                ORICOOP_TEST_STEAMID = $steamIds[$i]
                ORICOOP_TEST_NICK = "Rooms-$role"; ORICOOP_PLAYER_NAME = "Rooms-$role"
                ORICOOP_NET = 'off'; ORICOOP_NET_CSERVER = $cserver; ORICOOP_NET_ROOM = ''
                ORICOOP_TITLEJOIN = ''; ORICOOP_COOPSAVE_TEST = ''; ORICOOP_NET_SIM = ''
            }
        }
    })
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $clients = @(Start-TestClients $specs)
    $logs = @((Get-ClientLog 'rooms-host'), (Get-ClientLog 'rooms-join'))
    while ($watch.Elapsed.TotalSeconds -lt 500) {   # RoomsHookTest gives up after 480 s
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
    Complete-TestRun
}
# One verdict line for callers (run-affected): the per-step "ROOMS PASS n" lines are no verdict (T-082).
if ($ok) { 'ROOMS RESULT PASS' } else { 'ROOMS RESULT FAIL' }
Exit-TestRun   # LAST: releases the copy lock; exit 7 if this run changed the owner's game settings (T-094, T-117)
if ($ok) { exit 0 }
exit 1
