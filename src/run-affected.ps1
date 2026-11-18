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
param([string]$Base = 'main', [switch]$WorkingTree, [switch]$DryRun, [ValidateRange(1, 5)][int]$Parallel = 2, [string[]]$Files, [switch]$All, [string]$Summary)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$root = Split-Path -Parent $src

# --- suites (name -> script + arguments). Keep names short: they appear in the plan and the summary.
$Suites = [ordered]@{
    'local'       = @('run-test.ps1')
    'net'         = @('run-nettest.ps1')
    'net-relay'   = @('run-nettest.ps1', '-Relay')
    'net-3p'      = @('run-nettest.ps1', '-Clients', '3')
    'net-4p'      = @('run-nettest.ps1', '-Clients', '4')
    'net-newgame' = @('run-nettest.ps1', '-GuestNewGame')
    'coopsave'    = @('run-coopsave.ps1')
    'titlejoin'   = @('run-titlejoin.ps1')
    'titlejoin-relay' = @('run-titlejoin.ps1', '-Transport', 'relay')
    'rooms'       = @('run-rooms.ps1')
}
$AllSuites = @($Suites.Keys)

# --- module -> suites (see docs\modules.md for what each module contains)
$ModuleSuites = @{
    'M0' = $AllSuites
    'M1' = @('net', 'net-relay', 'net-3p', 'titlejoin', 'titlejoin-relay', 'coopsave')
    'M2' = @('local', 'net', 'net-3p', 'coopsave')
    'M3' = @('net', 'net-relay')
    'M4' = @('net', 'net-3p')
    'M5' = @('net', 'net-4p', 'local')
    'M6' = @('net', 'local')
    'M7' = @('coopsave', 'titlejoin', 'net-newgame', 'rooms')
    'M8' = @('local', 'titlejoin')
    'M9' = @()            # harness files map to their own suite below
}
# Harness / test files -> the suite that runs them (checked before the module table).
$FileSuites = [ordered]@{
    'TestHarness'      = @('local');   'TitleInputTest' = @('local');   'SignpostTest' = @('local')
    'NetIntroTest'     = @('net', 'net-4p');
    'NetTestHarness'   = @('net', 'net-3p', 'net-4p'); 'CoopSaveTest' = @('coopsave'); 'TitleJoinTest' = @('titlejoin', 'titlejoin-relay')
    'LobbyProbe'       = @();          'SkinDump' = @();               'L10nProbe' = @('local')
}

function Get-ModuleMap {
    # Parses the "| M<n> | name | Files | ..." rows of docs\modules.md into file base name -> module.
    $map = @{}
    $doc = Join-Path $root 'docs\modules.md'
    if (-not (Test-Path $doc)) { return $map }
    foreach ($line in Get-Content $doc) {
        if ($line -notmatch '^\|\s*(M\d)\s*\|[^|]*\|([^|]*)\|') { continue }
        $module = $Matches[1]; $files = $Matches[2]
        # Expand "Dir\{A, B}" and "Name(+Hooks)" forms, then split on commas.
        $files = [regex]::Replace($files, '(\w+)\\\{([^}]*)\}', { param($m) (($m.Groups[2].Value -split ',') | ForEach-Object { $m.Groups[1].Value + '\' + $_.Trim() }) -join ', ' })
        $files = [regex]::Replace($files, '(\w+)\(\+(\w+)\)', '$1, $1$2')
        foreach ($f in $files -split ',') {
            $name = ($f.Trim() -replace '`', '' -replace '\.cs$', '')
            if ($name -match '^[A-Za-z][\w\\]*$') { $map[(Split-Path -Leaf $name)] = $module }
        }
    }
    $map
}

function Get-SuitesFor([string]$path, $moduleMap) {
    $p = $path -replace '/', '\'
    $leaf = [IO.Path]::GetFileNameWithoutExtension($p)
    if ($p -like 'docs\*' -or $p -like '*.md') { return @{ Suites = @(); Why = 'docs' } }
    if ($p -like 'src\Tests\*') { return @{ Suites = @(); Why = 'unit tests only' } }
    if ($p -like 'src\RelayServer\*') { return @{ Suites = @('net-relay', 'titlejoin-relay'); Why = 'relay server' } }
    if ($p -like 'src\RoomServer\*') { return @{ Suites = @('rooms'); Why = 'room server' } }
    if ($p -like 'src\CServer\*') { return @{ Suites = @('net'); Why = 'C-server (run-nettest -CServer by hand if it exists)' } }
    if ($p -like 'src\TestQuiet\*' -or $p -eq 'src\test-launch.ps1') { return @{ Suites = @('local', 'net'); Why = 'test launcher' } }
    if ($p -match '^src\\run-(\w+)\.ps1$') {
        $s = @{ 'test' = @('local'); 'nettest' = @('net'); 'coopsave' = @('coopsave'); 'titlejoin' = @('titlejoin'); 'rooms' = @('rooms') }[$Matches[1]]
        return @{ Suites = @($s | Where-Object { $_ }); Why = 'its own script' }
    }
    if ($p -like 'src\assets\*') { return @{ Suites = @('local'); Why = 'assets' } }
    if ($p -like 'src\OriCoop\*') {
        foreach ($k in $FileSuites.Keys) { if ($leaf -eq $k) { return @{ Suites = $FileSuites[$k]; Why = 'harness ' + $k } } }
        if ($leaf -like 'Net*Test' -or $leaf -like '*SyncTest' -or $leaf -like 'NetStory*') { return @{ Suites = @('net'); Why = 'net harness' } }
        if ($moduleMap.ContainsKey($leaf)) { $m = $moduleMap[$leaf]; return @{ Suites = $ModuleSuites[$m]; Why = $m } }
        return @{ Suites = $AllSuites; Why = 'unknown mod file: everything' }
    }
    return @{ Suites = @(); Why = 'not covered by game suites' }
}

# --- changed files
Push-Location $root
try {
    $mergeBase = (git merge-base $Base HEAD 2>$null)
    if (-not $mergeBase) { $mergeBase = $Base }
    $changed = @(git diff --name-only "$mergeBase" HEAD)
    if ($WorkingTree) { $changed += @(git diff --name-only HEAD); $changed += @(git ls-files --others --exclude-standard -- src) }
} finally { Pop-Location }
if ($Files) { $changed = @($Files) }
$changed = @($changed | Where-Object { $_ } | Sort-Object -Unique)

$moduleMap = Get-ModuleMap
$plan = [ordered]@{}
'CHANGED (' + $changed.Count + ' files, base ' + $Base + $(if ($WorkingTree) { ' + working tree' } else { '' }) + ')'
foreach ($f in $changed) {
    $r = Get-SuitesFor $f $moduleMap
    '  ' + $f + '  ->  ' + $(if ($r.Suites.Count) { $r.Suites -join ', ' } else { '-' }) + '  (' + $r.Why + ')'
    foreach ($s in $r.Suites) { $plan[$s] = $true }
}
$toRun = @($Suites.Keys | Where-Object { $plan.Contains($_) })
if ($All) { $toRun = @($Suites.Keys) }
'PLAN: unit tests, then ' + $(if ($toRun.Count) { $toRun -join ', ' } else { 'no game suites' })
if ($DryRun) { return }

# --- layer 1: unit tests
$unitGame = Join-Path $root 'game'
if (-not (Test-Path "$unitGame\oriDE_Data\Managed")) { $unitGame = Join-Path (Split-Path -Parent (git -C $root rev-parse --path-format=absolute --git-common-dir).Trim()) 'game' }
& (Join-Path $src 'run-unittests.ps1') -Game $unitGame
if ($LASTEXITCODE -ne 0) { 'AFFECTED FAIL: unit tests failed; game suites not run'; exit 1 }

# --- layer 2: affected game suites (each in its own PowerShell, so they can share the copy locks)
$outDir = Join-Path $env:TEMP ('oricoop-affected-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $outDir | Out-Null
$running = @(); $results = [ordered]@{}; $queue = New-Object System.Collections.Queue
foreach ($s in $toRun) { $queue.Enqueue($s) }
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($queue.Count -gt 0 -or $running.Count -gt 0) {
    while ($queue.Count -gt 0 -and $running.Count -lt $Parallel) {
        $s = $queue.Dequeue(); $def = $Suites[$s]
        $log = Join-Path $outDir ($s + '.txt')
        $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $src $def[0])) + @($def | Select-Object -Skip 1)
        $p = Start-Process powershell -ArgumentList $args -PassThru -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError ($log + '.err')
        $running += [pscustomobject]@{ Name = $s; Proc = $p; Log = $log; Start = $sw.Elapsed.TotalSeconds }
        'START ' + $s + ' (' + ($def -join ' ') + ')'
        Start-Sleep -Seconds 5   # stagger lock grabs
    }
    Start-Sleep -Seconds 5
    foreach ($r in @($running)) {
        if (-not $r.Proc.HasExited) { continue }
        $text = if (Test-Path $r.Log) { Get-Content $r.Log -Raw } else { '' }
        $ok = $text -match '(?m)(NETTEST PASS|COOPSAVE PASS|TITLEJOIN \w+ PASS|ROOMS PASS|\] RESULT PASS)' -and $text -notmatch '(?m)(NETTEST FAIL|COOPSAVE FAIL|TITLEJOIN \w+ FAIL|\] RESULT FAIL|no result file)'
        if ($text -match 'OWNER IS PLAYING') { $ok = $false; $status = 'STOPPED (owner playing)' } else { $status = $(if ($ok) { 'PASS' } else { 'FAIL' }) }
        $results[$r.Name] = $status
        'DONE ' + $r.Name + ': ' + $status + ' in ' + [int]($sw.Elapsed.TotalSeconds - $r.Start) + ' s (log ' + $r.Log + ')'
        $running = @($running | Where-Object { $_ -ne $r })
    }
}
'--- summary (' + [int]$sw.Elapsed.TotalSeconds + ' s) ---'
$lines = @(foreach ($k in $results.Keys) { $k + ': ' + $results[$k] })
$lines
if ($Summary) { Set-Content -LiteralPath $Summary -Value (@('suites run ' + [DateTime]::Now.ToString('s') + ' (' + [int]$sw.Elapsed.TotalSeconds + ' s), logs ' + $outDir) + $lines) -Encoding UTF8 }
if (@($results.Values | Where-Object { $_ -ne 'PASS' }).Count -eq 0) { 'AFFECTED PASS' } else { 'AFFECTED FAIL'; exit 1 }
