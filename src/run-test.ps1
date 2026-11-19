# Runs the automated in-game co-op test against the isolated test copy.
# Saves/settings are redirected to C:\OriCoop\appdata so the real %LOCALAPPDATA% saves are never touched.
param([int]$Slot = 1, [int]$TimeoutSec = 300, [string]$Scenarios = '', [switch]$Force, [int]$Width = 960, [int]$Height = 540)
$ErrorActionPreference = 'Stop'
# Never close a running game on our own: it is usually someone playing on this PC (a test run once closed a
# live co-op session). test-launch.ps1 refuses while any oriDE.exe runs, launches quietly and stops the
# run (exit 3) if the owner starts playing. See docs\testing-rules.md.
. (Join-Path $PSScriptRoot 'test-launch.ps1')
Start-TestRun -Force:$Force
# Every run starts from the same save: the game writes progress back into the test copy during a run (area changes,
# checkpoints, deaths), so without this each run started somewhere else and terrain-dependent checks drifted.
# Only the isolated test copy is restored, from the untouched backup taken before any testing.
$pristine = 'C:\OriCoop\save-backup\20261114-150138'
$testSaves = Join-Path $TL_AppData[0] 'Ori and the Blind Forest DE'
if (Test-Path $pristine) {
    # Leftovers of the net tests' co-op save lineage (CoopSaves.cs): extra slots, *.oricoop links, backups.
    Get-ChildItem $testSaves -File | Where-Object { $_.Name -like 'saveFile*' -or $_.Name -like '*.oricoop*' -or $_.Name -eq 'oricoop-player.txt' } | Remove-Item -Force
    if (Test-Path "$testSaves\OriCoop-backups") { Remove-Item "$testSaves\OriCoop-backups" -Recurse -Force }
    Copy-Item "$pristine\*" $testSaves -Force
}
$testDir = $TL_TestDir
New-Item -ItemType Directory -Force $testDir | Out-Null
# Only this script's own result: other runs (other scripts, other copies' tagged logs) share this folder.
$out = "$testDir\result.txt"
Remove-Item -LiteralPath $out -Force -ErrorAction SilentlyContinue
# Keep the owner's own last play-session log: the test run overwrites BepInEx\LogOutput.log.
$log = Join-Path $TL_GameDir 'BepInEx\LogOutput.log'
if ((Test-Path $log) -and -not (Select-String -Path $log -Pattern 'TEST test harness up' -Quiet)) {
    New-Item -ItemType Directory -Force $TL_LogsDir | Out-Null
    Copy-Item $log ("$TL_LogsDir\play-" + (Get-Item $log).LastWriteTime.ToString('yyyyMMdd-HHmmss') + '.log') -Force
}

$vars = @{ LOCALAPPDATA = $TL_AppData[0]; ORICOOP_TEST = '1'; ORICOOP_SLOT = "$Slot"; ORICOOP_TEST_OUT = $out }
# Commas, spaces or semicolons separate names ("visual bottomhud" used to arrive as one unknown name, T-086).
if ($Scenarios) { $vars['ORICOOP_SCENARIOS'] = (($Scenarios -split '[\s,;]+') | Where-Object { $_ }) -join ',' }
# The harness flips settings during a run (P2CanTriggerCutscenes, pad assignments...) and BepInEx saves them at
# once; a run that crashes or is killed before the harness puts them back left them set for every later run
# (2026-11-15: a stuck P2CanTriggerCutscenes = true broke the interact scenario in all runs). Put the file back.
$cfg = Join-Path $TL_GameDir 'BepInEx\config\local.oricoop.cfg'
$cfgBefore = if (Test-Path $cfg) { [IO.File]::ReadAllBytes($cfg) } else { $null }
try {
    $p = Start-TestGame -Arguments "-screen-fullscreen 0 -screen-width $Width -screen-height $Height" -Vars $vars -Tag 'run-test'
    if (-not (Wait-TestGames @($p) $TimeoutSec $null @($out))) { $p.Kill(); $p.WaitForExit(10000) | Out-Null; "TIMEOUT: game killed after $TimeoutSec s" }
}
finally { if ($cfgBefore -ne $null) { [IO.File]::WriteAllBytes($cfg, $cfgBefore) }; Complete-TestRun }
if (Test-Path $out) { Get-Content $out } else { 'no result file: ' + (Get-NoResultReason) }
"--- BepInEx errors/warnings ---"
Get-Content $log | Select-String 'Error|Exception|Warning|P2 ' | Select-Object -First 30
Exit-TestRun   # release the test copy only now, after the results were read
