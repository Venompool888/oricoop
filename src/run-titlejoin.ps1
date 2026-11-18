# Title online flow with real menus (TitleJoinTest.cs): the host picks 多人游玩 -> transport -> 开房 -> a save slot;
# the guest starts with NO saves at all (appdata2 emptied) and picks 多人游玩 -> transport -> 加入, enters the address
# or room code and connects. Checks: the guest never sees a save slot, loads the host's save, ends beside the host,
# and keeps a co-op copy in a slot.  -Transport direct (default) | relay | cserver (starts a local server).
# Steam cannot run here (the test copy has no Steam client session); its pages are covered by run-test.
# -FullSlots: the guest has 10 unrelated saves (every slot full): it is asked which slot keeps the co-op copy.
param([ValidateSet('direct', 'relay', 'cserver')][string]$Transport = 'direct', [int]$TimeoutSec = 300, [switch]$Force, [switch]$FullSlots, [int]$Width = 800, [int]$Height = 450)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'test-launch.ps1')   # quiet launch + owner watchdog (docs\testing-rules.md)
Start-TestRun -Force:$Force
$testDir = $TL_TestDir
New-Item -ItemType Directory -Force $testDir | Out-Null
Get-ChildItem $testDir -Filter 'titlejoin*' -ErrorAction SilentlyContinue | Remove-Item -Force
$hostOut = Join-Path $testDir 'titlejoin-host.txt'
$guestOut = Join-Path $testDir 'titlejoin-guest.txt'
# Host: the pristine saves. Guest: no saves at all (no slot files, links or backups).
$pristine = 'C:\OriCoop\save-backup\20261114-150138'
foreach ($ad in $TL_AppData[0], $TL_AppData[1]) {
    $saves = Join-Path $ad 'Ori and the Blind Forest DE'
    New-Item -ItemType Directory -Force $saves | Out-Null
    Get-ChildItem $saves -File | Where-Object { $_.Name -like 'saveFile*' -or $_.Name -like '*.oricoop*' -or $_.Name -eq 'oricoop-player.txt' } | Remove-Item -Force
    if (Test-Path "$saves\OriCoop-backups") { Remove-Item "$saves\OriCoop-backups" -Recurse -Force }
    if ($ad -eq $TL_AppData[0] -and (Test-Path $pristine)) { Copy-Item "$pristine\*" $saves -Force }
    if ($ad -eq $TL_AppData[1] -and (Test-Path $pristine)) { Copy-Item "$pristine\*" $saves -Force; Get-ChildItem $saves -File | Where-Object { $_.Name -like 'saveFile*' } | Remove-Item -Force }
}
if ($FullSlots) { $g = Join-Path $TL_AppData[1] 'Ori and the Blind Forest DE'; for ($i = 0; $i -lt 10; $i++) { Copy-Item "$pristine\saveFile$($i % 2).sav" "$g\saveFile$i.sav" -Force } }
'GUEST appdata2 save files: ' + @(Get-ChildItem (Join-Path $TL_AppData[1] 'Ori and the Blind Forest DE') -Filter 'saveFile*' -ErrorAction SilentlyContinue).Count

function Client-Spec([string]$role, [string]$appData, [string]$resultPath) {
    $vars = @{ LOCALAPPDATA = $appData; ORICOOP_TEST = ''; ORICOOP_TITLEJOIN = $role; ORICOOP_TITLEJOIN_TRANSPORT = $Transport
        ORICOOP_SLOT = '1'; ORICOOP_TEST_OUT = $resultPath }
    if ($Transport -eq 'relay') { $vars['ORICOOP_NET_RELAY'] = '127.0.0.1:' + (47020 + $TL_PortOffset) }
    if ($Transport -eq 'cserver') { $vars['ORICOOP_NET_CSERVER'] = [System.Net.IPAddress]::Loopback.ToString() + ':' + (47022 + $TL_PortOffset) }
    if ($FullSlots) { $vars['ORICOOP_TITLEJOIN_FULL'] = '1' }
    @{ Tag = ('titlejoin-' + $role); Arguments = "-screen-fullscreen 0 -screen-width $Width -screen-height $Height"; Vars = $vars }
}

# The harness and the menus save settings (relay server, last address) at once; put them back afterwards.
$cfgFiles = @((Join-Path $TL_GameDir 'BepInEx\config\local.oricoop.cfg'), (Join-Path $TL_GameDir 'BepInEx\config\oricoop-relays.txt'))
$before = @{}
foreach ($c in $cfgFiles) { if (Test-Path $c) { $before[$c] = [IO.File]::ReadAllBytes($c) } }
$clients = @(); $relayProc = $null; $cserverProc = $null
$watch = [System.Diagnostics.Stopwatch]::StartNew()
try {
    if ($Transport -eq 'relay') {
        $relayProc = Start-Process -FilePath (Get-TestServerExe 'RelayServer') -ArgumentList ([string](47020 + $TL_PortOffset)) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $testDir 'titlejoin-relay.log')
    }
    if ($Transport -eq 'cserver') {
        $cserverExe = Get-TestServerExe 'CServer'   # ORICOOP_CSERVER_EXE: another build, like ORICOOP_RELAY_EXE
        $cserverProc = Start-Process -FilePath $cserverExe -ArgumentList @('--port', [string](47022 + $TL_PortOffset)) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $testDir 'titlejoin-cserver.log')
    }
    # Both games start back to back (Unity refuses a copy started after another finished starting).
    $clients = @(Start-TestClients @((Client-Spec 'host' $TL_AppData[0] $hostOut), (Client-Spec 'join' $TL_AppData[1] $guestOut)))
    [void](Wait-TestGames $clients $TimeoutSec $watch @($hostOut, $guestOut))
    foreach ($client in $clients) { if (-not $client.HasExited) { $client.Kill(); $client.WaitForExit(); 'TIMEOUT: game {0} killed after {1} s' -f $client.Id, $TimeoutSec } }
} finally {
    foreach ($client in $clients) { if (-not $client.HasExited) { $client.Kill(); $client.WaitForExit() }; $client.Dispose() }
    if ($relayProc -and -not $relayProc.HasExited) { $relayProc.Kill() }
    if ($cserverProc) { if (-not $cserverProc.HasExited) { $cserverProc.Kill(); $cserverProc.WaitForExit() }; $cserverProc.Dispose() }
    foreach ($c in $before.Keys) { [IO.File]::WriteAllBytes($c, $before[$c]) }
    Complete-TestRun
}
$passed = $true
foreach ($r in @($hostOut, $guestOut)) {
    '--- {0} ---' -f $r
    if (Test-Path $r) { $lines = @(Get-Content $r); $lines; if (-not ($lines -match '\] RESULT PASS [1-9]\d*/\d+$')) { $passed = $false } }
    else { 'no result file: ' + (Get-NoResultReason); $passed = $false }
}
if ($passed) { "TITLEJOIN $Transport PASS" } else { "TITLEJOIN $Transport FAIL" }
Exit-TestRun   # release the test copy only now, after the results were read
