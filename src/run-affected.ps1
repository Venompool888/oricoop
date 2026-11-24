# Runs only the tests that cover what changed (layer 2 of docs\testing-rules.md):
#   1. src\run-unittests.ps1 (always, fast, no game). A failure stops here.
#   2. the game suites that cover the changed files, mapped through the module table in docs\modules.md
#      (file -> module M0..M9 -> suites). Unknown files under src\OriCoop and M0 (composition root) run everything.
# Changed files = git diff <Base>...HEAD, plus uncommitted changes with -WorkingTree.
# Usage: src\run-affected.ps1 [-Base main] [-WorkingTree] [-DryRun] [-Parallel 2]
#   -DryRun     print the plan only.
#   -Parallel N run up to N suites at once (each takes its own test copy through test-launch.ps1's lock).
# Exit 0 = everything that ran passed.
#   -Files a,b  use these paths instead of git (relative to the repo root).   -All  every suite (nightly / release).   -Summary f  also write the summary to file f.
# T-096: net-newgame runs only its own phases before a merge (src\test-selection.ps1 $TS_SuitePhases; -All
# runs everything). A failed suite is rerun ONCE, with only the phases of its failed checks plus every phase that never
# ran (2-player net suites; T-226: fail-fast stops at the first failure, so the phases after it never ran) or whole (the
# other suites, 30-250 s): passing the second time = FLAKY (counts as a pass, logged to logs\flaky-checks.tsv with the
# checks that failed); failing again = FAIL. A rerun that passes without running every phase it was given is a FAIL: a
# phase that never ran never counts as passed. $env:ORICOOP_RERUN=0 turns reruns off.
#   -FakeSuites DIR  (run-affected-selftest.ps1 only) suite scripts come from DIR; no selfcheck, unit tests or build.
param([string]$Base = 'main', [switch]$WorkingTree, [switch]$DryRun, [ValidateRange(1, 5)][int]$Parallel = 2, [string[]]$Files, [switch]$All, [string]$Summary, [string]$FakeSuites = '')
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$root = Split-Path -Parent $src

# --- suites, module map and net phases: src\test-selection.ps1 (+ src\phase-map.psd1), shared with the selfcheck.
$TS_Root = $root
. (Join-Path $src 'test-selection.ps1')
$Suites = $TS_Suites

# --- changed files
Push-Location $root
try {
    $mergeBase = (git merge-base $Base HEAD 2>$null)
    if (-not $mergeBase) { $mergeBase = $Base }
    $changed = @(git diff --name-only "$mergeBase" HEAD)
    if ($WorkingTree) { $changed += @(git diff --name-only HEAD); $changed += @(git ls-files --others --exclude-standard -- src) }
} finally { Pop-Location }
if ($Files) { $changed = @($Files | ForEach-Object { $_ -split "," } | ForEach-Object { $_.Trim() }) }   # -File passes "a,b" as one string
$changed = @($changed | Where-Object { $_ } | Sort-Object -Unique)

$sel = Get-Selection $changed
'CHANGED (' + $changed.Count + ' files, base ' + $Base + $(if ($WorkingTree) { ' + working tree' } else { '' }) + ')'
foreach ($f in $changed) {
    $r = $sel.Why[$f]
    '  ' + $f + '  ->  ' + $(if ($r.Suites.Count) { $r.Suites -join ', ' } else { '-' }) + '  (' + $r.Why + ')' + $(if ($sel.PhaseWhy[$f]) { '  phases ' + $sel.PhaseWhy[$f] } else { '' })
}
$toRun = @($sel.Suites)
$phaseEnv = Get-PhaseEnv $sel
if ($All) { $toRun = @($Suites.Keys); $phaseEnv = '' }
$suitePhases = @{}
foreach ($s in $toRun) { $suitePhases[$s] = Get-SuitePhaseEnv $s $sel -All:$All }
'PLAN: selection selfcheck, unit tests, then ' + $(if ($toRun.Count) { (@($toRun | ForEach-Object { $_ + $(if ($TS_PhasedSuites -contains $_) { ' [' + $(if ($suitePhases[$_]) { $suitePhases[$_] } else { 'ALL' }) + ']' } else { '' }) }) -join ', ') } else { 'no game suites' }) +
    '; a failed suite is rerun once' + $(if ($env:ORICOOP_RERUN -eq '0') { ' (OFF: ORICOOP_RERUN=0)' } else { '' })
if ($DryRun) { return }
$suiteDir = $(if ($FakeSuites) { $FakeSuites } else { $src })

if (-not $FakeSuites) {
# --- layer 0: the selection must not be able to skip a check that catches a known break (T-078)
& (Join-Path $src 'test-selection-selfcheck.ps1') -Root $root | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) { 'AFFECTED FAIL: selection selfcheck failed (src\phase-map.psd1); run src\test-selection-selfcheck.ps1 for the details'; exit 1 }
# The rerun / flaky rules themselves changed: prove them with fake suites (T-096, about a minute, no game).
if (@($changed | Where-Object { ($_ -replace '/', '\') -in @('src\run-affected.ps1', 'src\test-selection.ps1', 'src\run-affected-selftest.ps1') }).Count) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src 'run-affected-selftest.ps1') | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { 'AFFECTED FAIL: run-affected selftest failed; run src\run-affected-selftest.ps1 for the details'; exit 1 }
}

# --- layer 1: unit tests
$unitGame = Join-Path $root 'game'
if (-not (Test-Path "$unitGame\oriDE_Data\Managed")) { $unitGame = Join-Path (Split-Path -Parent (git -C $root rev-parse --path-format=absolute --git-common-dir).Trim()) 'game' }
& (Join-Path $src 'run-unittests.ps1') -Game $unitGame
if ($LASTEXITCODE -ne 0) { 'AFFECTED FAIL: unit tests failed; game suites not run'; exit 1 }

# --- layer 2: affected game suites (each in its own PowerShell, so they can share the copy locks)
# Every suite tests the same build: the caller's ORICOOP_TEST_DLL, else a clean build of HEAD (T-084: without it each
# suite tested whatever DLL its copy last had, and run-rooms refused to start).
if ($toRun.Count -and -not $env:ORICOOP_TEST_DLL) {
    $env:ORICOOP_TEST_DLL = (& (Join-Path $src 'build-commit.ps1') HEAD | Select-Object -Last 1)
    'DLL: ' + $env:ORICOOP_TEST_DLL + ' (clean build of HEAD' + $(if ($WorkingTree) { '; uncommitted changes are NOT in it' } else { '' }) + ')'
}
if ($env:ORICOOP_TEST_DLL -and -not (Test-Path -LiteralPath $env:ORICOOP_TEST_DLL)) { 'AFFECTED FAIL: ORICOOP_TEST_DLL not found: ' + $env:ORICOOP_TEST_DLL; exit 1 }
}
$outDir = Join-Path $(if ($FakeSuites) { $FakeSuites } else { $env:TEMP }) ('oricoop-affected-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $outDir | Out-Null
$running = @(); $results = [ordered]@{}; $times = @{}; $queue = New-Object System.Collections.Queue
foreach ($s in $toRun) { $queue.Enqueue([pscustomobject]@{ Name = $s; Phases = $suitePhases[$s]; Try = 1; First = $null }) }
$flaky = [ordered]@{}
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($queue.Count -gt 0 -or $running.Count -gt 0) {
    while ($queue.Count -gt 0 -and $running.Count -lt $Parallel) {
        $job = $queue.Dequeue(); $s = $job.Name; $def = $Suites[$s]
        $log = Join-Path $outDir ($s + $(if ($job.Try -gt 1) { '.rerun' } else { '' }) + '.txt')
        $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $suiteDir $def[0])) + @($def | Select-Object -Skip 1)
        # 2-player net suites run only their phases (child processes inherit the environment at start).
        $env:ORICOOP_PHASES = $job.Phases
        $p = Start-Process powershell -ArgumentList $args -PassThru -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError ($log + '.err')
        [void]$p.Handle   # keeps the exit code readable after the process ends
        $env:ORICOOP_PHASES = ''
        $running += [pscustomobject]@{ Name = $s; Job = $job; Proc = $p; Log = $log; Start = $sw.Elapsed.TotalSeconds; Got = -1.0 }
        $(if ($job.Try -gt 1) { 'RERUN ' } else { 'START ' }) + $s + ' (' + ($def -join ' ') + $(if ($job.Phases) { '; phases ' + $job.Phases } else { '' }) + ')'
        if (-not $FakeSuites) { Start-Sleep -Seconds 5 }   # stagger lock grabs
    }
    Start-Sleep -Seconds $(if ($FakeSuites) { 1 } else { 5 })
    foreach ($r in @($running)) {
        # Queue time vs own time (T-078): wall time is mostly waiting for a free test copy; the launcher prints
        # "TEST COPY x:" once it holds one. Read with FileShare.ReadWrite (the child is still writing the log).
        if ($r.Got -lt 0) {
            try {
                $fs = [IO.File]::Open($r.Log, 'Open', 'Read', 'ReadWrite'); $rd = New-Object IO.StreamReader($fs)
                if ($rd.ReadToEnd() -match '(?m)^TEST COPY \w+:') { $r.Got = $sw.Elapsed.TotalSeconds }
                $rd.Close()
            } catch { }
        }
        if (-not $r.Proc.HasExited) { continue }
        $text = if (Test-Path $r.Log) { Get-Content $r.Log -Raw } else { '' }
        # PASS needs a verdict line, no failure line and exit code 0: a suite that threw or was refused (exit != 0) is a
        # FAIL even if it printed nothing (T-084), and run-rooms' per-step "ROOMS PASS n" lines are no verdict (T-082).
        $ok = $text -match '(?m)(NETTEST PASS|COOPSAVE PASS|TITLEJOIN \w+ PASS|ROOMS RESULT PASS|\] RESULT PASS)' -and $text -notmatch '(?m)(NETTEST FAIL|COOPSAVE FAIL|TITLEJOIN \w+ FAIL|ROOMS (RESULT FAIL|FAIL|DONE fail)|\] RESULT FAIL|no result file|OWNER SETTINGS CHANGED BY A TEST)' -and $r.Proc.ExitCode -eq 0
        if ($text -cmatch 'OWNER IS PLAYING') { $ok = $false; $status = 'STOPPED (owner playing)' } else { $status = $(if ($ok) { 'PASS' } else { 'FAIL' }) }
        $job = $r.Job
        $now = $sw.Elapsed.TotalSeconds; $got = $(if ($r.Got -ge 0) { $r.Got } else { $r.Start })
        $prev = $times[$r.Name]
        $times[$r.Name] = $(if ($prev) { @(($prev[0] + [int]($got - $r.Start)), ($prev[1] + [int]($now - $got))) } else { @([int]($got - $r.Start), [int]($now - $got)) })
        $failed = @((Get-FailedPhases $text).Checks)
        $rerun = $null; $plan = $null
        # Exit 2 (owner playing / no copy), 4 (gave up waiting for a copy), 5 / 6 / 7 (safety stops) are never rerun.
        if ($status -eq 'FAIL' -and $job.Try -eq 1 -and $env:ORICOOP_RERUN -ne '0' -and @(2, 4, 5, 6, 7) -notcontains $r.Proc.ExitCode) { $plan = Get-RerunPlan $r.Name $text $job.Phases }
        if ($null -ne $plan) { $rerun = $plan.Phases }
        # T-226: a 2-player rerun that passed without every client starting each phase it was given proves nothing about those.
        $unrun = @($(if ($job.Try -gt 1 -and $status -eq 'PASS' -and $TS_PhasedSuites -contains $r.Name) { Get-UnrunPhases $text $job.Phases }))
        if ($rerun -ne $null) {
            $status = 'FAIL, rerun ' + $(if ($rerun -and $rerun -ne $job.Phases) { 'phases ' + $rerun } else { 'whole suite' }) +
                $(if ($plan.NotRun.Count) { ' (failed ' + ($plan.Failed -join ',') + '; never ran ' + ($plan.NotRun -join ',') + ')' } else { '' })
            $queue.Enqueue([pscustomobject]@{ Name = $r.Name; Phases = $rerun; Try = 2; First = $failed })
        } elseif ($unrun.Count) {
            $status = 'FAIL (the rerun passed without running phase ' + ($unrun -join ',') + '; a phase that never ran is no pass)'
        } elseif ($job.Try -gt 1 -and $status -eq 'PASS') {
            $status = 'FLAKY (failed, then passed on the rerun: ' + $(if ($job.First.Count) { ($job.First | Select-Object -First 5) -join ' | ' } else { 'no check named' }) + ')'
            $flaky[$r.Name] = $job.First
        } elseif ($job.Try -gt 1 -and $status -eq 'FAIL') { $status = 'FAIL (twice)' }
        if ($rerun -eq $null) { $results[$r.Name] = $status }
        'DONE ' + $r.Name + $(if ($job.Try -gt 1) { ' (rerun)' } else { '' }) + ': ' + $status + ' in ' + [int]($now - $r.Start) + ' s (queued ' + [int]($got - $r.Start) + ' s, own ' + [int]($now - $got) + ' s) (log ' + $r.Log + ')'
        if ($failed.Count -and $status -notlike 'PASS*') { foreach ($c in ($failed | Select-Object -First 8)) { '    FAIL ' + $c } }
        $running = @($running | Where-Object { $_ -ne $r })
    }
}
'--- summary (' + [int]$sw.Elapsed.TotalSeconds + ' s) ---'
$lines = @(foreach ($k in $results.Keys) { $k + ': ' + $results[$k] + $(if ($times[$k]) { ' (queued ' + $times[$k][0] + ' s, own ' + $times[$k][1] + ' s)' } else { '' }) })
$lines
if ($Summary) { Set-Content -LiteralPath $Summary -Value (@('suites run ' + [DateTime]::Now.ToString('s') + ' (' + [int]$sw.Elapsed.TotalSeconds + ' s), logs ' + $outDir) + $lines) -Encoding UTF8 }
# History for the nightly report ("merges since last night ran only these phases"): one line per run, shared by all
# worktrees (the main checkout's logs folder).
try {
    $common = (git -C $root rev-parse --path-format=absolute --git-common-dir).Trim()
    $hist = Join-Path (Join-Path (Split-Path -Parent $common) 'logs') 'affected-history.tsv'
    New-Item -ItemType Directory -Force (Split-Path -Parent $hist) | Out-Null
    $head = (git -C $root rev-parse --short HEAD).Trim(); $branch = (git -C $root rev-parse --abbrev-ref HEAD).Trim()
    $ok = $(if (@($results.Values | Where-Object { $_ -ne 'PASS' -and $_ -notlike 'FLAKY*' }).Count -eq 0) { $(if ($flaky.Count) { 'FLAKY' } else { 'PASS' }) } else { 'FAIL' })
    if (-not $FakeSuites) { Add-Content -LiteralPath $hist -Value (([DateTime]::Now.ToString('s'), $branch, $head, ($toRun -join ','), $(if ($phaseEnv) { $phaseEnv } else { 'ALL' }), $ok, [int]$sw.Elapsed.TotalSeconds) -join "`t") }
    # Flaky checks (T-096): one line per check that failed and then passed on the rerun; tools\test-stats.py counts them.
    $flakyLog = $(if ($env:ORICOOP_FLAKY_LOG) { $env:ORICOOP_FLAKY_LOG } else { Join-Path (Split-Path -Parent $hist) 'flaky-checks.tsv' })
    foreach ($k in $flaky.Keys) {
        foreach ($c in @($(if ($flaky[$k].Count) { $flaky[$k] } else { @('(no check named)') }))) { Add-Content -LiteralPath $flakyLog -Value (([DateTime]::Now.ToString('s'), $branch, $head, $k, $c) -join "`t") }
    }
} catch { 'NOTE: could not write the affected history: ' + $_.Exception.Message }
if (@($results.Values | Where-Object { $_ -ne 'PASS' -and $_ -notlike 'FLAKY*' }).Count -eq 0) { 'AFFECTED PASS' + $(if ($flaky.Count) { ' (flaky, passed on the rerun: ' + (@($flaky.Keys) -join ', ') + ')' } else { '' }) } else { 'AFFECTED FAIL'; exit 1 }
