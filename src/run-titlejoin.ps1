# Title online flow with real menus (TitleJoinTest.cs): the host picks 多人游玩 -> transport -> 开房 -> a save slot;
# the guest starts with NO saves at all (appdata2 emptied) and picks 多人游玩 -> transport -> 加入, enters the address
# or room code and connects. Checks: the guest never sees a save slot, loads the host's save, ends beside the host,
# and keeps a co-op copy in a slot.  -Transport direct (default, LAN host mode) | cserver (remote test C-server).
# Steam cannot run here (the test copy has no Steam client session); its pages are covered by run-test.
# -FullSlots: the guest has 10 unrelated saves (every slot full): it is asked which slot keeps the co-op copy.
# -Prologue skip|leave (T-203, TitlePrologueTest.cs): the host starts a NEW game on an empty slot while the guest
#   waits; the prologue plays on both. skip (LAN or -Transport cserver): in step, only the host's input counts, the
#   host's skip ends it for both, both load into the first scene with the host's save. leave (LAN only): the guest
#   leaves mid-prologue, joins again late (waiting screen), the host skips, the late guest loads the host's save.
#   hostquit (LAN only): the host quits to the title mid-prologue; the guest's copy ends after the 10 s link-drop
#   grace, its join is given up with "the host left", both titles are usable.
param([ValidateSet('direct', 'cserver')][string]$Transport = 'direct', [int]$TimeoutSec = 300, [switch]$Force, [switch]$FullSlots, [int]$Width = 800, [int]$Height = 450,
    [ValidateSet('', 'skip', 'leave', 'hostquit')][string]$Prologue = '')
$ErrorActionPreference = 'Stop'
if ($Prologue) {
    if ($Prologue -ne 'skip' -and $Transport -ne 'direct') { throw "-Prologue $Prologue runs on the LAN join page only: use -Transport direct" }
    if (-not $PSBoundParameters.ContainsKey('TimeoutSec')) { $TimeoutSec = 540 }   # the prologue runs about a minute before Naru is playable
}
. (Join-Path $PSScriptRoot 'test-launch.ps1')   # quiet launch + owner watchdog (docs\testing-rules.md)
if ($Transport -eq 'cserver') {
    $remote = Get-RemoteTestServer 'cserver'
    Assert-TestServerCurrent 'cserver'
}
Start-TestRun -Force:$Force
$testDir = $TL_TestDir
New-Item -ItemType Directory -Force $testDir | Out-Null
# Files only: a folder another test keeps there under a titlejoin* name (seen: titlejoin-fake-steam) is not this run's
# to remove, and Remove-Item stops the whole run on a folder that has children.
Get-ChildItem $testDir -Filter 'titlejoin*' -File -ErrorAction SilentlyContinue | Remove-Item -Force
$hostOut = Join-Path $testDir 'titlejoin-host.txt'
$guestOut = Join-Path $testDir 'titlejoin-guest.txt'
# Host: the pristine saves. Guest: no saves at all (no slot files, links or backups).
$pristine = 'C:\OriCoop\save-backup\20261114-150138'
foreach ($ad in $TL_AppData[0], $TL_AppData[1]) {
    $saves = Join-Path $ad 'Ori and the Blind Forest DE'
    $coop = Join-Path $saves 'OriCoop-coop'
    if (Test-Path $coop) { Remove-Item $coop -Recurse -Force }
    New-Item -ItemType Directory -Force $coop | Out-Null
    Get-ChildItem $saves -File | Where-Object { $_.Name -like 'saveFile*' -or $_.Name -like '*.oricoop*' -or $_.Name -eq 'oricoop-player.txt' } | Remove-Item -Force
    if (Test-Path "$saves\OriCoop-backups") { Remove-Item "$saves\OriCoop-backups" -Recurse -Force }
    if ($ad -eq $TL_AppData[0] -and (Test-Path $pristine)) { Copy-Item "$pristine\*" $saves -Force; Copy-Item "$pristine\saveFile*.sav" $coop -Force }
    if ($ad -eq $TL_AppData[1] -and (Test-Path $pristine)) { Copy-Item "$pristine\*" $saves -Force; Get-ChildItem $saves -File | Where-Object { $_.Name -like 'saveFile*' } | Remove-Item -Force }
}
$g = Join-Path $TL_AppData[1] 'Ori and the Blind Forest DE\OriCoop-coop'
if ($FullSlots) { for ($i = 0; $i -lt 10; $i++) { Copy-Item "$pristine\saveFile$($i % 2).sav" "$g\saveFile$i.sav" -Force } }
'GUEST appdata2 save files: ' + @(Get-ChildItem $g -Filter 'saveFile*' -ErrorAction SilentlyContinue).Count

function Client-Spec([string]$role, [string]$appData, [string]$resultPath) {
    $vars = @{ LOCALAPPDATA = $appData; ORICOOP_TEST = ''; ORICOOP_TITLEJOIN = $role; ORICOOP_TITLEJOIN_TRANSPORT = $Transport
        ORICOOP_SLOT = '1'; ORICOOP_TEST_OUT = $resultPath }
    if ($Transport -eq 'cserver') { $vars['ORICOOP_NET_CSERVER'] = $remote }
    if ($FullSlots) { $vars['ORICOOP_TITLEJOIN_FULL'] = '1' }
    if ($Prologue) { $vars['ORICOOP_TITLEJOIN_PROLOGUE'] = $Prologue }
    @{ Tag = ('titlejoin-' + $role); Arguments = "-screen-fullscreen 0 -screen-width $Width -screen-height $Height"; Vars = $vars }
}

# The harness and the menus save settings (last address, C-server list) at once; put them back afterwards.
$cfgFiles = @((Join-Path $TL_GameDir 'BepInEx\config\local.oricoop.cfg'))
$before = @{}
foreach ($c in $cfgFiles) { if (Test-Path $c) { $before[$c] = [IO.File]::ReadAllBytes($c) } }
$clients = @()
$watch = [System.Diagnostics.Stopwatch]::StartNew()
try {
    # Both games start back to back (Unity refuses a copy started after another finished starting).
    $clients = @(Start-TestClients @((Client-Spec 'host' $TL_AppData[0] $hostOut), (Client-Spec 'join' $TL_AppData[1] $guestOut)))
    [void](Wait-TestGames $clients $TimeoutSec $watch @($hostOut, $guestOut))
    foreach ($client in $clients) { if (-not $client.HasExited) { $client.Kill(); $client.WaitForExit(); 'TIMEOUT: game {0} killed after {1} s' -f $client.Id, $TimeoutSec } }
} finally {
    foreach ($client in $clients) { if (-not $client.HasExited) { $client.Kill(); $client.WaitForExit() }; $client.Dispose() }
    foreach ($c in $before.Keys) { [IO.File]::WriteAllBytes($c, $before[$c]) }
    Complete-TestRun
}
$passed = $true
foreach ($r in @($hostOut, $guestOut)) {
    '--- {0} ---' -f $r
    if (Test-Path $r) { $lines = @(Get-Content $r); $lines; if (-not ($lines -match '\] RESULT PASS [1-9]\d*/\d+$')) { $passed = $false } }
    else { 'no result file: ' + (Get-NoResultReason); $passed = $false }
}
$what = $(if ($Prologue) { "${Transport}_prologue_$Prologue" } else { $Transport })   # one word: run-affected reads "TITLEJOIN <word> PASS"
if ($passed) { "TITLEJOIN $what PASS" } else { "TITLEJOIN $what FAIL" }
Exit-TestRun   # release the test copy only now, after the results were read
