# Two to four isolated Ori clients; none uses the owner's real LOCALAPPDATA save location.
# -Clients 3|4: processes start together; only the LAST guest connects after LateJoinSec (default 20) and after the host settled the save lineage (T-129 marker file).
# -CServer: all games share one room on the remote test C-server instead of direct UDP (LAN host mode).
#   Works with -Clients 2..4. See src\test-servers\README.md for configuration.
# -TimeoutSec: whole run (default 1020). -HarnessSec: the in-game suite limit (ORICOOP_HARNESS_SEC, default 900; the
#   harness needed ~360 s at HEAD 2026-11-16 and phase WO adds ~120 s, more under load; main reached ~655 s on 2026-11-18 and T-059's local-player checks need more; TestHarness defaults to 420).
# -Width/-Height: window size of both games (default 640x360; larger for readable screenshots).
# -GuestNewGame: the guest has NO saves (appdata2 emptied) and picks an empty slot -> Normal -> new game; it must skip
#   the prologue, load the host's save from the title and join the host (GuestJoin.cs; "W empty slot" checks).
# -GuestNoSlot: same empty guest, but it never picks a slot (title 加入 path): GuestJoin.BeginNoSlot once connected.
param([int]$TimeoutSec = 1020, [int]$HarnessSec = 900, [string]$Sim = '', [switch]$Force, [switch]$CServer, [int]$Width = 640, [int]$Height = 360, [switch]$GuestNewGame, [switch]$GuestNoSlot,
    [switch]$LocalPlayers, [ValidateSet(2, 3, 4)][int]$Clients = 2, [ValidateRange(0, 3600)][int]$LateJoinSec = 0)
$script:ExtraEnv = @{}
if ($LocalPlayers) {
    if ($Clients -eq 4) { throw 'LocalPlayers requires 2 or 3 devices (four seats maximum).' }
    $script:ExtraEnv['ORICOOP_NET_LOCAL_PLAYERS'] = '1'
}
$ErrorActionPreference = 'Stop'
if (-not $PSBoundParameters.ContainsKey('LateJoinSec') -and $Clients -ge 3) { $LateJoinSec = 20 }
. (Join-Path $PSScriptRoot 'test-launch.ps1')   # copy lock, quiet launch, owner watchdog (docs\testing-rules.md)
if ($CServer) {
    $script:ExtraEnv['ORICOOP_NET_CSERVER'] = Get-RemoteTestServer 'cserver'
    Assert-TestServerCurrent 'cserver'
    $script:ExtraEnv['ORICOOP_NET_TRANSPORT'] = 'cserver'
}
Start-TestRun -Force:$Force
$testDir = $TL_TestDir
New-Item -ItemType Directory -Force $testDir | Out-Null
$launchLog = Join-Path $testDir 'net-launch.log'
Set-Content -LiteralPath $launchLog -Value ('RUN ' + [DateTime]::UtcNow.ToString('o') + ' clients=' + $Clients + ' lateJoinSec=' + $LateJoinSec)
$hostOut = Join-Path $testDir 'net-host.txt'
$guestOut = Join-Path $testDir 'net-guest.txt'
$guest2Out = Join-Path $testDir 'net-guest2.txt'
$guest3Out = Join-Path $testDir 'net-guest3.txt'
$results = @($hostOut, $guestOut)
if ($Clients -ge 3) { $results += $guest2Out }
if ($Clients -ge 4) { $results += $guest3Out }
foreach ($resultPath in $results) {
    foreach ($artifact in @($resultPath, ($resultPath + '.track.csv'))) {
        if (Test-Path -LiteralPath $artifact) { Remove-Item -LiteralPath $artifact -Force }
    }
}
# The games write progress back into the test saves during runs; start both from the untouched backup every time
# (same as run-test.ps1), so positions and world state do not drift between runs.
$pristine = 'C:\OriCoop\save-backup\20261114-150138'
$appDataDirs = @($TL_AppData[0], $TL_AppData[1])
if ($Clients -ge 3 -and -not $CServer) { $script:ExtraEnv['ORICOOP_NET_TRANSPORT'] = 'direct' }
for ($i = 2; $i -lt $Clients; $i++) {
    # Players 3 and 4 get their settings from player 2's folder the first time (saves come from the backup below).
    $saves = Join-Path $TL_AppData[$i] 'Ori and the Blind Forest DE'
    $new = -not (Test-Path -LiteralPath $saves)
    New-Item -ItemType Directory -Force -Path $saves | Out-Null
    if ($new) {
        Get-ChildItem -LiteralPath (Join-Path $TL_AppData[1] 'Ori and the Blind Forest DE') |
            Where-Object { $_.Name -notlike 'saveFile*' -and $_.Name -notlike '*.oricoop*' -and $_.Name -ne 'oricoop-player.txt' -and $_.Name -ne 'OriCoop-backups' -and $_.Name -ne 'OriCoop-coop' } |
            Copy-Item -Destination $saves -Recurse -Force
    }
    $appDataDirs += $TL_AppData[$i]
    if (-not (Test-Path -LiteralPath $pristine)) { throw ('Pristine saves missing for appdata' + ($i + 1) + ' reset.') }
}
if (Test-Path $pristine) {
    foreach ($ad in $appDataDirs) {
        $saves = Join-Path $ad 'Ori and the Blind Forest DE'
        # Co-op save lineage (CoopSaves.cs) adds slots, *.oricoop links and OriCoop-backups: drop them too, so
        # every run starts as a fresh pair.
        Get-ChildItem $saves -File | Where-Object { $_.Name -like 'saveFile*' -or $_.Name -like '*.oricoop*' -or $_.Name -eq 'oricoop-player.txt' } | Remove-Item -Force
        $coopPath = [IO.Path]::GetFullPath((Join-Path $saves 'OriCoop-coop'))
        if (-not $coopPath.StartsWith(([IO.Path]::GetFullPath($saves) + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Co-op path outside test save directory.' }
        if (Test-Path -LiteralPath $coopPath) { Remove-Item -LiteralPath $coopPath -Recurse -Force }
        Copy-Item "$pristine\*" $saves -Force
        New-Item -ItemType Directory -Force $coopPath | Out-Null
        Copy-Item "$pristine\saveFile*.sav" $coopPath -Force
    }
}
if ($GuestNewGame -or $GuestNoSlot) {
    Get-ChildItem (Join-Path $TL_AppData[1] 'Ori and the Blind Forest DE\OriCoop-coop') -File | Where-Object { $_.Name -like 'saveFile*' } | Remove-Item -Force
    'GUEST NEW GAME: appdata2 has no saves; the guest starts from an empty slot'
}

# One client of the batch (test-launch.ps1 Start-TestClients prepares a private BepInEx root per tag in
# $TL_RuntimeDir, launches all of them back to back and waits for "Chainloader startup complete").
function Net-Spec([string]$role, [string]$appData, [string]$resultPath, [string]$tag, [long]$connectAt = 0) {
    $v = @{ LOCALAPPDATA = $appData; ORICOOP_TEST = '1'; ORICOOP_SLOT = '1'; ORICOOP_TEST_OUT = $resultPath; ORICOOP_NET = $role
        ORICOOP_NET_ADDR = '127.0.0.1'; ORICOOP_NET_PORT = [string](47011 + $TL_PortOffset); ORICOOP_NET_SIM = $Sim
        ORICOOP_NET_CLIENTS = [string]$Clients; ORICOOP_NET_CONNECT_AT = [string]$connectAt; ORICOOP_HARNESS_SEC = [string]$HarnessSec }
    foreach ($k in $script:ExtraEnv.Keys) { $v[$k] = $script:ExtraEnv[$k] }
    if ($GuestNewGame -and $role -eq 'join') { $v['ORICOOP_NEWGAME'] = '1' }
    if ($GuestNoSlot -and $role -eq 'join') { $v['ORICOOP_NEWGAME'] = 'noslot' }
    Add-Content -LiteralPath $launchLog -Value ('SPEC ' + [DateTime]::UtcNow.ToString('o') + ' tag=' + $tag + ' role=' + $role + ' appdata=' + $appData + ' result=' + $resultPath + ' connectAtTicks=' + $connectAt + ' bepinex=' + (Join-Path $TL_RuntimeDir ($tag + '\BepInEx')))
    @{ Tag = $tag; Arguments = "-screen-fullscreen 0 -screen-width $Width -screen-height $Height"; Vars = $v }
}

function Read-Tracks([string]$resultPath) {
    $path = $resultPath + '.track.csv'
    if (Test-Path -LiteralPath $path) {
        Import-Csv -LiteralPath $path -Header kind,tick,x,y
    }
}

function Compare-Tracks([string]$label, [object[]]$observer, [object[]]$owner) {
    $byTick = @{}
    foreach ($row in $owner) { if ($row.kind -eq 'own') { $byTick[$row.tick] = $row } }
    $sum = 0.0; $maximum = 0.0; $count = 0
    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    foreach ($row in $observer) {
        if ($row.kind -ne 'puppet' -or -not $byTick.ContainsKey($row.tick)) { continue }
        $real = $byTick[$row.tick]
        $dx = [double]::Parse($row.x, $culture) - [double]::Parse($real.x, $culture)
        $dy = [double]::Parse($row.y, $culture) - [double]::Parse($real.y, $culture)
        $distance = [Math]::Sqrt($dx * $dx + $dy * $dy)
        $sum += $distance; $maximum = [Math]::Max($maximum, $distance); $count++
    }
    if ($count -gt 0) {
        'TRACK {0} mean {1} max {2} n {3}' -f $label, ($sum / $count).ToString('F4', $culture), $maximum.ToString('F4', $culture), $count
    } else { 'TRACK {0} mean NaN max NaN n 0' -f $label }
}

$clientProcesses = @()
$watch = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $roomFile = Join-Path $testDir 'room-code.txt'
    Remove-Item $roomFile -Force -ErrorAction SilentlyContinue
    if ($CServer) { $script:ExtraEnv['ORICOOP_NET_ROOM_FILE'] = $roomFile }
    # T-129: the last guest connects only after the host settled the save lineage with the others (the host writes this
    # marker at its first lineage commit; NetTestHarness.LateJoinGate). The games take ~30 s to start, so LateJoinSec
    # alone has already run out by then and the 'late' guest used to see the offer like everyone else.
    $lineageFile = Join-Path $testDir 'lineage-settled.txt'
    Remove-Item $lineageFile -Force -ErrorAction SilentlyContinue
    if ($Clients -ge 3) { $script:ExtraEnv['ORICOOP_NET_LINEAGE_FILE'] = $lineageFile }
    # All clients are prepared first, then launched back to back (Unity refuses a copy that starts after another
    # has finished starting), then each one's start-up is awaited.
    # Only the last guest joins late (LateJoinSec); the others connect at once.
    $lateConnectAt = [DateTime]::UtcNow.AddSeconds($LateJoinSec).Ticks
    $guestOuts = @($guestOut, $guest2Out, $guest3Out)
    $specs = @(Net-Spec 'host' $TL_AppData[0] $hostOut 'host-1')
    for ($g = 1; $g -lt $Clients; $g++) {
        $at = $(if ($g -eq $Clients - 1 -and $Clients -ge 3) { $lateConnectAt } else { 0 })
        $specs += Net-Spec 'join' $TL_AppData[$g] $guestOuts[$g - 1] ('guest-' + $g) $at
    }
    $clientProcesses = @(Start-TestClients $specs 60)
    for ($i = 0; $i -lt $clientProcesses.Count; $i++) { Add-Content -LiteralPath $launchLog -Value ('LAUNCH ' + [DateTime]::UtcNow.ToString('o') + ' tag=' + $specs[$i].Tag + ' pid=' + $clientProcesses[$i].Id) }
    if ($CServer) {
        # The host's game creates the room; use its private log.
        $code = $null; $log = Get-ClientLog 'host-1'
        for ($i = 0; $i -lt 180 -and -not $code; $i++) {
            Wait-TestSeconds 0.5
            if (Test-Path $log) { $m = Select-String -Path $log -Pattern 'cserver: seat P1, room code ([A-Z0-9]{6})' | Select-Object -Last 1; if ($m) { $code = $m.Matches[0].Groups[1].Value } }
        }
        if (-not $code) { 'CSERVER: host never reported a room code'; throw 'no room code' }
        'CSERVER room code ' + $code
        Set-Content -Path $roomFile -Value $code -Encoding ASCII
    }
    [void](Wait-TestGames $clientProcesses $TimeoutSec $watch $results)
    foreach ($client in $clientProcesses) {
        if (-not $client.HasExited) {
            Add-Content -LiteralPath $launchLog -Value ('TIMEOUT ' + [DateTime]::UtcNow.ToString('o') + ' pid=' + $client.Id)
            $client.Kill()
            $client.WaitForExit()
            'TIMEOUT: game {0} killed after {1} s' -f $client.Id, $TimeoutSec
        }
    }
} finally {
    foreach ($client in $clientProcesses) {
        if (-not $client.HasExited) { $client.Kill(); $client.WaitForExit() }
        Add-Content -LiteralPath $launchLog -Value ('EXIT ' + [DateTime]::UtcNow.ToString('o') + ' pid=' + $client.Id + ' exitCode=' + $client.ExitCode)
    }
    Complete-TestRun   # kills only this run's PIDs and unlinks the client junctions
}
$passed = $true
foreach ($resultPath in $results) {
    '--- {0} ---' -f $resultPath
    if (Test-Path -LiteralPath $resultPath) {
        $lines = @(Get-Content -LiteralPath $resultPath)
        $lines
        # A PASS needs at least one check (T-086: "RESULT PASS 0/0" is no pass).
        if (-not ($lines -match '\] RESULT PASS [1-9]\d*/\d+$') -or ($lines -match '\] RESULT FAIL')) { $passed = $false }
    } else { 'no result file: ' + (Get-NoResultReason); $passed = $false }
}
if ($Clients -eq 2) {
    $hostRows = @(Read-Tracks $hostOut)
    $guestRows = @(Read-Tracks $guestOut)
    Compare-Tracks 'host-sees-guest' $hostRows $guestRows
    Compare-Tracks 'guest-sees-host' $guestRows $hostRows
    'TRACK reference: mean < 0.3 for phases A/B; reported numbers include A..E and are informational only.'
}
if ($passed) { 'NETTEST PASS' } else { 'NETTEST FAIL' }
Exit-TestRun   # release the test copy only now, after the results were read
