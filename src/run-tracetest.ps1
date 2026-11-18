# Live trace test (playtest P6): while the game runs, its trace file (CoopTrace.cs) and BepInEx\LogOutput.log must
# already hold the expected lines, each line stamped within a couple of seconds of real time. Reads the files while
# the game is alive (FileShare.ReadWrite), never after exit, so it fails if logs are only written on quit.
#   single (default): one client running the in-game harness scenarios $Scenarios.
#   -Net: runs src\run-nettest.ps1 (host + guest) with the trace dir redirected, then checks both traces share one
#         session id and both roles appear. (run-nettest only checks liveness after the fact, so -Net checks sid only.)
# Uses test-launch.ps1 (quiet, locked test copy). Set $env:ORICOOP_TEST_DLL to test a build that is not installed.
param([string]$Scenarios = 'move,camera,enemy', [int]$TimeoutSec = 300, [switch]$Net, [double]$MaxLagSec = 2.0)
$ErrorActionPreference = 'Stop'
$traceDir = 'C:\OriCoop\test\trace'
New-Item -ItemType Directory -Force $traceDir | Out-Null
Get-ChildItem $traceDir -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

function Read-Shared([string]$path) {
    try {
        $fs = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        try { $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8); $sr.ReadToEnd() } finally { $fs.Close() }
    } catch { '' }
}
function Last-Utc([string[]]$lines) {
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        $p = $lines[$i].Split(' ')
        if ($p.Count -gt 2 -and $p[1].EndsWith('Z')) { return [DateTime]::Parse($p[1]).ToUniversalTime() }
    }
    return $null
}

if ($Net) {
    $env:ORICOOP_TRACE_DIR = $traceDir; $env:ORICOOP_TRACE_LEVEL = '2'
    $runStart = Get-Date
    try { & (Join-Path $PSScriptRoot 'run-nettest.ps1') -TimeoutSec $TimeoutSec | Tee-Object -FilePath (Join-Path $traceDir 'nettest-output.txt') | Select-Object -Last 8 }
    finally { Remove-Item Env:ORICOOP_TRACE_DIR, Env:ORICOOP_TRACE_LEVEL -ErrorAction SilentlyContinue }
    $files = @(Get-ChildItem $traceDir -Filter 'OriCoop-trace-*.log')
    $sids = @{}; $roles = @{}
    foreach ($f in $files) {
        foreach ($m in (Select-String -Path $f.FullName -Pattern ' sid=([0-9a-f]{8}) role=(P\d) ' -AllMatches)) {
            $sids[$m.Matches[0].Groups[1].Value] = 1; $roles[$m.Matches[0].Groups[2].Value + '@' + $f.Name] = 1
        }
    }
    $ok = $files.Count -ge 2 -and $sids.Count -ge 1 -and @($roles.Keys | Where-Object { $_ -like 'P1@*' }).Count -ge 1 -and @($roles.Keys | Where-Object { $_ -like 'P2@*' }).Count -ge 1
    $shared = $false
    foreach ($sid in $sids.Keys) { if (@($files | Where-Object { Select-String -Path $_.FullName -Pattern (' sid=' + $sid + ' ') -Quiet }).Count -ge 2) { $shared = $true } }
    'trace files: ' + $files.Count + '; session ids: ' + (($sids.Keys) -join ',') + '; roles: ' + (($roles.Keys | ForEach-Object { $_.Split('@')[0] } | Sort-Object -Unique) -join ',')
    # T-067: each end's own BepInEx\LogOutput.log (paths from the net launch log) must stamp every OriCoop line with
    # UTC, carry the "clock: utc_offset=" header and, once connected, the measured "clock: offset vs Pn" line.
    $clockOk = $true; $logs = @()
    $launch = Get-ChildItem 'C:\OriCoop\test*\net-launch.log' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt $runStart } | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($launch) { foreach ($m in (Select-String -Path $launch.FullName -Pattern ' tag=(\S+) .* bepinex=(\S+)')) { $logs += (Join-Path $m.Matches[0].Groups[2].Value 'LogOutput.log') } }
    if ($logs.Count -lt 2) { 'clock check: could not find both ends'' LogOutput.log (launch log: ' + $(if ($launch) { $launch.FullName } else { 'none' }) + ')'; $clockOk = $false }
    foreach ($log in $logs) {
        $ours = @(Get-Content -LiteralPath $log -ErrorAction SilentlyContinue | Where-Object { $_ -match '^\[\w+\s*:Ori Local Co-op\] ' })
        $unstamped = @($ours | Where-Object { $_ -notmatch '^\[\w+\s*:Ori Local Co-op\] \d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z ' })
        $header = @($ours | Where-Object { $_ -match ' clock: utc_offset=[+-]\d\d:\d\d local=\S+ utc=\S+Z$' }).Count
        $offset = @($ours | Where-Object { $_ -match ' clock: offset vs P\d = [+-]\d+\.\d ms \(P\d clock minus ours; rtt \d+\.\d ms, \d+ samples\)$' })
        'clock check ' + (Split-Path (Split-Path (Split-Path $log -Parent) -Parent) -Leaf) + ': ' + $ours.Count + ' OriCoop lines, ' + $unstamped.Count + ' without UTC, header ' + $header + ', offset lines ' + $offset.Count + $(if ($offset.Count) { ' (' + ($offset[-1] -replace '^.* clock: ', '') + ')' } else { '' })
        if ($ours.Count -eq 0 -or $unstamped.Count -gt 0 -or $header -ne 1 -or $offset.Count -lt 1) { $clockOk = $false; if ($unstamped.Count) { '  first unstamped: ' + $unstamped[0] } }
    }
    if ($ok -and $shared -and $clockOk) { 'TRACE NET PASS: both ends traced the same session id; both LogOutput.log files are UTC stamped with the clock offset' } else { 'TRACE NET FAIL'; exit 1 }
    return
}

. (Join-Path $PSScriptRoot 'test-launch.ps1')
Start-TestRun
$pristine = 'C:\OriCoop\save-backup\20261114-150138'
$testSaves = Join-Path $TL_AppData[0] 'Ori and the Blind Forest DE'
if (Test-Path $pristine) {
    Get-ChildItem $testSaves -File | Where-Object { $_.Name -like 'saveFile*' -or $_.Name -like '*.oricoop*' -or $_.Name -eq 'oricoop-player.txt' } | Remove-Item -Force
    Copy-Item "$pristine\*" $testSaves -Force
}
$out = Join-Path $TL_TestDir 'trace-result.txt'
Remove-Item $out -ErrorAction SilentlyContinue
$bepLog = Join-Path $TL_GameDir 'BepInEx\LogOutput.log'
$cfg = Join-Path $TL_GameDir 'BepInEx\config\local.oricoop.cfg'
$cfgBefore = if (Test-Path $cfg) { [IO.File]::ReadAllBytes($cfg) } else { $null }
$vars = @{ LOCALAPPDATA = $TL_AppData[0]; ORICOOP_TEST = '1'; ORICOOP_SLOT = '1'; ORICOOP_TEST_OUT = $out; ORICOOP_SCENARIOS = $Scenarios
           ORICOOP_TRACE_DIR = $traceDir; ORICOOP_TRACE_LEVEL = '2' }
$seen = [ordered]@{ header = $null; mirror = $null; scene = $null; player = $null; live = $null; beplive = $null }
$bestLag = 999.0; $ipLeak = $null
try {
    $p = Start-TestGame -Arguments '-screen-fullscreen 0 -screen-width 640 -screen-height 360' -Vars $vars -Tag 'trace'
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        Invoke-TestWatch; Wait-TestSeconds 1
        if ($p.HasExited) { break }
        $f = Get-ChildItem $traceDir -Filter 'OriCoop-trace-*.log' | Sort-Object LastWriteTime | Select-Object -Last 1
        if (-not $f) { continue }
        $text = Read-Shared $f.FullName
        $t = [int]$sw.Elapsed.TotalSeconds
        if ($p.HasExited) { break }                       # only count what was readable while the game was alive
        $lines = $text -split "`n" | ForEach-Object { $_.TrimEnd("`r") } | Where-Object { $_ }
        if (-not $seen.header -and $text.StartsWith('# OriCoop trace')) { $seen.header = $t }
        if (-not $seen.mirror -and $text -match ' log info src="?Ori Local Co-op"? msg="?Ori Local Co-op \S+ loaded') { $seen.mirror = $t }
        if (-not $seen.scene -and $text -match ' scene current from=\S* to=\w') { $seen.scene = $t }
        if (-not $seen.player -and $text -match ' player \w+ .*who=P1 .*pos=') { $seen.player = $t }
        $last = Last-Utc $lines
        if ($last) {
            $lag = ([DateTime]::UtcNow - $last).TotalSeconds
            if ($seen.player -and $lag -lt $bestLag) { $bestLag = $lag }
            if ($seen.player -and $lag -le $MaxLagSec -and -not $seen.live) { $seen.live = $t }
        }
        if (-not $seen.beplive -and (Read-Shared $bepLog) -match 'loaded; P2 pad') { $seen.beplive = $t }
        if (-not $ipLeak) { $m = [regex]::Match($text, '\b(?!127\.)(?:\d{1,3}\.){3}\d{1,3}\b'); if ($m.Success) { $ipLeak = $m.Value } }
        if (@($seen.Values | Where-Object { $_ -eq $null }).Count -eq 0 -and $sw.Elapsed.TotalSeconds -gt 40) { break }
    }
    if (-not $p.HasExited) { $p.Kill(); $p.WaitForExit(10000) | Out-Null }
}
finally { if ($cfgBefore -ne $null) { [IO.File]::WriteAllBytes($cfg, $cfgBefore) }; Complete-TestRun }

$fail = @()
foreach ($k in $seen.Keys) { 'live check ' + $k + ': ' + $(if ($seen[$k] -ne $null) { 'seen at ' + $seen[$k] + ' s' } else { 'MISSING' }); if ($seen[$k] -eq $null) { $fail += $k } }
'smallest lag of the newest trace line while running: ' + [Math]::Round($bestLag, 2) + ' s (limit ' + $MaxLagSec + ')'
if ($ipLeak) { 'IP address leaked into the trace: ' + $ipLeak.Substring(0, [Math]::Min(3, $ipLeak.Length)) + '...'; $fail += 'ip' }
if ($fail.Count -eq 0) { 'TRACE LIVE PASS' } else { 'TRACE LIVE FAIL: ' + ($fail -join ','); exit 1 }
