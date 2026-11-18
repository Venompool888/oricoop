# One game copy walks the mode menu, including My rooms as native rows (no IMGUI popup).
param([string]$Repo = (Split-Path -Parent $PSScriptRoot), [int]$TimeoutSec = 300)
$ErrorActionPreference = 'Stop'
. (Join-Path $Repo 'src\test-launch.ps1')
Start-TestRun -Copy a
$testSaves = Join-Path $TL_AppData[0] 'Ori and the Blind Forest DE'
$pristine = 'C:\OriCoop\save-backup\20261114-150138'
if (Test-Path $pristine) { Copy-Item "$pristine\*" $testSaves -Force }
try {
    $vars = @{ LOCALAPPDATA = $TL_AppData[0]; ORICOOP_TEST = '1'; ORICOOP_TEST_OUT = (Join-Path $TL_TestDir 'modeprobe-result.txt'); ORICOOP_MODEMENU_PROBE = '1'; ORICOOP_SLOT = '1' }
    $p = Start-TestGame -Arguments '-screen-fullscreen 0 -screen-width 1280 -screen-height 720' -Vars $vars -Tag 'modeprobe'
    # The probe walks the menu once and logs "back on page"; after that the test harness carries on into gameplay and
    # re-opens the menu, which would run a second, out-of-step pass. Stop the game at the end of the first pass.
    $logPath = Join-Path $TL_GameDir 'BepInEx\LogOutput.log'; $t0 = Get-Date; $t0u = $t0.AddSeconds(-5); $finished = $false
    function Fresh { (Test-Path $logPath) -and ((Get-Item $logPath).LastWriteTime -gt $t0u) }   # the log is shared and may be stale
    while (((Get-Date) - $t0).TotalSeconds -lt $TimeoutSec -and -not $p.HasExited) {
        Invoke-TestWatch
        if ((Fresh) -and (Select-String -Path $logPath -Pattern 'mode menu probe: back on page' -Quiet)) { $finished = $true; break }
        Wait-TestSeconds 2
    }
    if (-not $finished -and -not $p.HasExited) { "TIMEOUT: probe did not finish in $TimeoutSec s"; '--- BepInEx log tail ---'; Get-Content $logPath -Tail 25 | ForEach-Object { $_.Substring(0, [Math]::Min(200, $_.Length)) } }
    if ($p.HasExited -and -not $finished) { "game exited early, code $($p.ExitCode)"; Get-ChildItem $TL_TestDir -Filter 'modeprobe*' | ForEach-Object { '--- ' + $_.Name; Get-Content $_.FullName -Tail 12 } }
    if (-not $p.HasExited) { $p.Kill() }
    if (Fresh) { Copy-Item $logPath (Join-Path $TL_TestDir 'modeprobe-LogOutput.log') -Force }   # full log for digging
    $log = $(if (Fresh) { Get-Content $logPath } else { 'no fresh BepInEx log (game exited or never started)' })   # read while this run still holds the copy (the log is shared)
}
finally { Complete-TestRun }
'--- mode menu probe lines ---'
$log | Select-String 'mode menu probe|mode menu: page|host mode' | Select-Object -Last 30 | ForEach-Object Line
'--- probe errors ---'
$err = @($log | Select-String 'mode menu probe:.*(expected|still listed)')
$err | ForEach-Object Line
$rooms = $log | Select-String 'mode menu probe: native rooms rows verified \(no popup\)'
if ($err.Count -eq 0 -and $rooms -and ($log | Select-String 'mode menu: page rooms')) { 'MODEMENU PROBE PASS' } else { 'MODEMENU PROBE FAIL (errors=' + $err.Count + ')' }
Exit-TestRun
