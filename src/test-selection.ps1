# Change -> tests selection, shared by run-affected.ps1, test-selection-selfcheck.ps1 and run-nightly.ps1 (T-078).
# Dot-source it: . (Join-Path $PSScriptRoot 'test-selection.ps1'). Needs $TS_Root (repo root); defaults to the parent of
# this script's folder.
#   Get-Selection <files>  -> @{ Suites = [ordered] suite -> why; Phases = 'FULL' or a sorted list; PhaseWhy = file -> phases }
# Suites come from docs\modules.md (module -> suites, below); net phases from src\phase-map.psd1 (see its header).
if (-not $TS_Root) { $TS_Root = Split-Path -Parent $PSScriptRoot }

# --- suites (name -> script + arguments). Keep names short: they appear in the plan and the summary.
$TS_Suites = [ordered]@{
    'local'       = @('run-test.ps1')
    'net'         = @('run-nettest.ps1')
    'net-relay'   = @('run-nettest.ps1', '-Relay')
    'net-3p'      = @('run-nettest.ps1', '-Clients', '3')
    'net-4p'      = @('run-nettest.ps1', '-Clients', '4')
    'net-3p-cs'   = @('run-nettest.ps1', '-Clients', '3', '-CServer')   # T-111: server mode, P1 leaves, seat 2 hosts
    'net-newgame' = @('run-nettest.ps1', '-GuestNewGame')
    'net-lp2'     = @('run-nettest.ps1', '-LocalPlayers', '-Clients', '2')
    'net-lp3'     = @('run-nettest.ps1', '-LocalPlayers', '-Clients', '3')
    'coopsave'    = @('run-coopsave.ps1')
    'titlejoin'   = @('run-titlejoin.ps1')
    'titlejoin-relay' = @('run-titlejoin.ps1', '-Transport', 'relay')
    'rooms'       = @('run-rooms.ps1')
}
$TS_AllSuites = @($TS_Suites.Keys)
# The 2-player net suites: they run NetTestHarness's phase list and take ORICOOP_PHASES.
$TS_PhasedSuites = @('net', 'net-relay', 'net-newgame')
$TS_AllPhases = @('INTRO', 'MOVE', 'W', 'E3', 'E5', 'R', 'S4', 'P', 'WO', 'CAM', 'CUT')
# Before a merge (not -All / nightly), these 2-player suites run only the phases of their own risk, whatever the change
# selected (T-096): they repeat the direct 'net' suite over another transport (relay) or another join path (guest new
# game). Relay is a plain packet forwarder, so the session start, the movement stream (A..E: loss, blackout, snaps) and
# the bulk world/save transfer (W) cover it; the guest new-game join is decided during the start and W. Everything else in
# those suites tested the same code as 'net' again (measured: 578 / 573 s median, pass rate 31 % / 21 %, the same
# CUT/CAM/R failures as 'net'). The 'net' suite keeps every selected phase; the nightly runs all phases everywhere.
$TS_SuitePhases = [ordered]@{
    'net-relay'   = @('INTRO', 'MOVE', 'W')
    'net-newgame' = @('INTRO', 'MOVE', 'W')
}

# --- module -> suites (see docs\modules.md for what each module contains)
$TS_ModuleSuites = @{
    'M0' = $TS_AllSuites
    'M1' = @('net', 'net-relay', 'net-3p', 'net-3p-cs', 'net-4p', 'net-lp2', 'net-lp3', 'titlejoin', 'titlejoin-relay', 'coopsave')
    'M2' = @('local', 'net', 'net-3p', 'coopsave')
    'M3' = @('net', 'net-relay', 'net-3p')   # T-115: guest-to-guest world messages ride the P2P host's forward table
    'M4' = @('net', 'net-3p')
    'M5' = @('net', 'net-3p-cs', 'net-4p', 'local')
    'M6' = @('net', 'net-3p-cs', 'local')
    'M7' = @('coopsave', 'titlejoin', 'net-newgame', 'rooms')
    'M8' = @('local', 'titlejoin')
    'M9' = @()            # harness files map to their own suite below
}
# Harness / test files -> the suite that runs them (checked before the module table).
$TS_FileSuites = [ordered]@{
    'TestHarness'      = @('local');   'TitleInputTest' = @('local');   'SignpostTest' = @('local')
    'NetIntroTest'     = @('net', 'net-4p');
    'NetTestHarness'   = @('net', 'net-3p', 'net-3p-cs', 'net-4p', 'net-lp2', 'net-lp3'); 'CoopSaveTest' = @('coopsave'); 'TitleJoinTest' = @('titlejoin', 'titlejoin-relay')
    # T-059 several local players per device online: the -LocalPlayers suites (2 devices / 3 players, 3 devices / 4 players).
    'NetLocalPlayersTest' = @('net-lp2', 'net-lp3'); 'OnlineLocalPlayers' = @('local', 'net', 'net-lp2', 'net-lp3'); 'OnlineRoomScreen' = @('net-lp2', 'net-lp3')
    'NetLocalSeats'    = @('net', 'net-lp2', 'net-lp3'); 'LocalEdgeWall' = @('local', 'net-lp2', 'net-lp3')
    'NativeRooms'      = @('rooms', 'local', 'titlejoin'); 'NativeGlowTint' = @('local'); 'ModeSelectLayout' = @('local'); 'OsFonts' = @('local'); 'RoomsHookTest' = @('rooms');
    'LobbyProbe'       = @();          'SkinDump' = @();               'L10nProbe' = @('local')
}

function Get-TSModuleMap {
    # Parses the "| M<n> | name | Files | ..." rows of docs\modules.md into file base name -> module.
    $map = @{}
    $doc = Join-Path $TS_Root 'docs\modules.md'
    if (-not (Test-Path $doc)) { return $map }
    foreach ($line in Get-Content $doc) {
        if ($line -notmatch '^\|\s*(M\d)\s*\|[^|]*\|([^|]*)\|') { continue }
        $module = $Matches[1]; $files = $Matches[2]
        # Expand "Dir\{A, B}" and "Name(+Hooks)" forms, drop "(notes)", then split on commas.
        $files = [regex]::Replace($files, '(\w+)\\\{([^}]*)\}', { param($m) (($m.Groups[2].Value -split ',') | ForEach-Object { $m.Groups[1].Value + '\' + $_.Trim() }) -join ', ' })
        $files = [regex]::Replace($files, '(\w+)\(\+(\w+)\)', '$1, $1$2')
        $files = [regex]::Replace($files, '\s*\([^)]*\)', '')
        foreach ($f in $files -split ',') {
            $name = ($f.Trim() -replace '`', '' -replace '\.cs$', '')
            if ($name -match '^[A-Za-z][\w\\]*$') { $map[(Split-Path -Leaf $name)] = $module }
        }
    }
    $map
}

# Not Import-PowerShellDataFile: Windows PowerShell started from pwsh (Start-Process) inherits pwsh's PSModulePath and
# then does not find that cmdlet. The file is our own hashtable literal, so evaluate it.
function Get-TSPhaseMap { & ([scriptblock]::Create((Get-Content -LiteralPath (Join-Path $TS_Root 'src\phase-map.psd1') -Raw))) }

function Get-SuitesFor([string]$path, $moduleMap) {
    $p = $path -replace '/', '\'
    $leaf = [IO.Path]::GetFileNameWithoutExtension($p)
    if ($p -eq 'src\phase-map.psd1' -or $p -eq 'src\test-selection.ps1') { return @{ Suites = $TS_AllSuites; Why = 'the test selection itself: everything' } }
    if ($p -like 'docs\*' -or $p -like '*.md') { return @{ Suites = @(); Why = 'docs' } }
    if ($p -like 'src\Tests\*') { return @{ Suites = @(); Why = 'unit tests only' } }
    if ($p -like 'src\RelayServer\*') { return @{ Suites = @('net-relay', 'titlejoin-relay'); Why = 'relay server' } }
    if ($p -like 'src\RoomServer\*') { return @{ Suites = @('rooms'); Why = 'room server' } }
    if ($p -like 'src\CServer\*') { return @{ Suites = @('net', 'net-3p-cs'); Why = 'C-server (net-3p-cs runs it)' } }
    # T-118: every suite starts its games through the launcher and TestQuiet (Start-TestClients, copy locks, the run cap,
    # input block), not only local and net.
    if ($p -like 'src\TestQuiet\*' -or $p -eq 'src\test-launch.ps1') { return @{ Suites = $TS_AllSuites; Why = 'test launcher: every suite uses it' } }
    # A suite's own script: every suite that runs it (run-nettest.ps1 -> all net-* suites, run-titlejoin.ps1 -> both
    # titlejoin suites), from the suite table above, so a new suite is covered without another entry here (T-118).
    if ($p -match '^src\\(run-[\w-]+\.ps1)$') {
        $own = $Matches[1]
        $s = @($TS_Suites.Keys | Where-Object { $TS_Suites[$_][0] -eq $own })
        if ($s.Count) { return @{ Suites = $s; Why = 'its own script (every suite that runs it)' } }
    }
    if ($p -like 'src\assets\*') { return @{ Suites = @('local'); Why = 'assets' } }
    if ($p -like 'src\OriCoop\*') {
        foreach ($k in $TS_FileSuites.Keys) { if ($leaf -eq $k) { return @{ Suites = $TS_FileSuites[$k]; Why = 'harness ' + $k } } }
        if ($leaf -like 'Net*Test' -or $leaf -like '*SyncTest' -or $leaf -like 'NetStory*') { return @{ Suites = @('net'); Why = 'net harness' } }
        if ($moduleMap.ContainsKey($leaf)) { $m = $moduleMap[$leaf]; return @{ Suites = $TS_ModuleSuites[$m]; Why = $m } }
        return @{ Suites = $TS_AllSuites; Why = 'unknown mod file: everything' }
    }
    return @{ Suites = @(); Why = 'not covered by game suites' }
}

# Net phases one file needs: 'FULL', or a list (possibly empty: the file does not touch the 2-player net suites).
function Get-PhasesFor([string]$path, $moduleMap, $phaseMap) {
    $p = $path -replace '/', '\'
    $leaf = [IO.Path]::GetFileNameWithoutExtension($p)
    if ($p -eq 'src\phase-map.psd1' -or $p -eq 'src\test-selection.ps1') { return 'FULL' }
    if ($p -eq 'src\run-nettest.ps1' -or $p -eq 'src\test-launch.ps1' -or $p -like 'src\TestQuiet\*' -or $p -like 'src\CServer\*' -or $p -like 'src\RelayServer\*') { return 'FULL' }
    if ($p -notlike 'src\OriCoop\*') { return @() }
    if ($phaseMap.Full -contains $leaf) { return 'FULL' }
    if ($phaseMap.Files.ContainsKey($leaf)) { $v = $phaseMap.Files[$leaf] }
    elseif ($moduleMap.ContainsKey($leaf)) { $v = $phaseMap.Modules[$moduleMap[$leaf]] }
    else { return 'FULL' }   # unknown mod file
    if ($v -eq 'FULL') { return 'FULL' }
    return @(($v -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Get-Selection([string[]]$files) {
    $moduleMap = Get-TSModuleMap; $phaseMap = Get-TSPhaseMap
    $suites = [ordered]@{}; $phases = @{}; $full = $false; $why = [ordered]@{}; $phaseWhy = [ordered]@{}
    foreach ($f in $files) {
        $r = Get-SuitesFor $f $moduleMap
        $why[$f] = $r
        foreach ($s in $r.Suites) { $suites[$s] = $true }
        $ph = Get-PhasesFor $f $moduleMap $phaseMap
        if ($ph -eq 'FULL') { $full = $true; $phaseWhy[$f] = 'FULL' }
        else { foreach ($x in @($ph)) { $phases[$x] = $true }; $phaseWhy[$f] = (@($ph) -join ',') }
    }
    # The phase map decides for the 2-player net suites: no file needs any of their phases -> they are not run.
    if (-not $full -and $phases.Count -eq 0) { foreach ($s in $TS_PhasedSuites) { if ($suites.Contains($s)) { $suites.Remove($s) } } }
    # ... and a file that needs net phases runs at least the 'net' suite, even if its module does not list it (NetHud -> R,S4).
    elseif (-not ($TS_PhasedSuites | Where-Object { $suites.Contains($_) })) { $suites['net'] = $true }
    $ordered = @($TS_Suites.Keys | Where-Object { $suites.Contains($_) })
    $list = $(if ($full) { 'FULL' } else { @($TS_AllPhases | Where-Object { $phases.ContainsKey($_) }) })
    @{ Suites = $ordered; Phases = $list; Why = $why; PhaseWhy = $phaseWhy }
}

# The ORICOOP_PHASES value for one suite of a merge-time run: a capped suite runs its own phases (T-096); -All = every phase.
function Get-SuitePhaseEnv([string]$suite, $selection, [switch]$All) {
    if ($TS_PhasedSuites -notcontains $suite) { return '' }
    if ($All) { return '' }
    if ($TS_SuitePhases.Contains($suite)) { return (@($TS_SuitePhases[$suite]) -join ',') }
    return (Get-PhaseEnv $selection)
}

# Rerun planning (T-096). The 2-player net harness writes "=== phase X" when phase X starts and "=== phase END" before
# its closing checks; "net role=" starts each client's section. A failing check belongs to the phase above it. Fail-fast
# stops the games before the result files exist, so its summary ("  <tag>.log: FAIL <check>") has no markers: those
# checks are placed by their name's first word (Get-CheckPhase).
# Returns @{ Checks = failing check lines; Phases = phases of those checks; Unplaced = $true if any failing check is
# outside a phase (start-up, closing, an unknown name): then the whole suite is rerun }.
function Get-CheckPhase([string]$check) {
    $w = ($check.Trim() -split '\s+')[0]
    if ($w -match '^(INTRO|CAM|CUT|WO|E3|E5|S4)') { return $Matches[1] }
    if ($w -eq 'W' -or $w -match '^W\d') { return 'W' }
    if ($w -match '^R\d*$') { return 'R' }
    if ($w -match '^(Kuro|KURO)$') { return 'CUT' }
    if ($w -eq 'P3' -or $w -eq 'P4') { return $null }      # used by both P (push rounds) and CUT / W: ambiguous
    if ($w -match '^P([BCD]|\d+L\w*|[12])$') { return 'P' }   # push rounds P1, P2L, P3L200oneway, PB/PC/PD
    if ($w -match '^[A-E]$') { return 'MOVE' }
    return $null
}
function Get-FailedPhases([string]$text) {
    $phase = $null; $checks = New-Object 'System.Collections.Generic.List[string]'; $phases = [ordered]@{}; $unplaced = $false
    foreach ($line in ($text -split "`r?`n")) {
        if ($line -match '^\[[\s\d.]+\] net role=') { $phase = $null; continue }
        if ($line -match '^\[[\s\d.]+\] === phase (\w+)') { $phase = $(if ($Matches[1] -eq 'END') { $null } else { $Matches[1] }); continue }
        $c = $null; $p = $null
        if ($line -match '^\[[\s\d.]+\] (TEST )?FAIL (.*)$') { $c = $Matches[2].Trim(); $p = $phase }
        elseif ($line -match '^\s+\S+\.log: (TEST )?FAIL (.*)$') { $c = $Matches[2].Trim() }
        if (-not $c) { continue }
        if ($checks -notcontains $c) { $checks.Add($c) }
        if (-not $p) { $p = Get-CheckPhase $c }
        if ($p -and $TS_AllPhases -contains $p) { $phases[$p] = $true } else { $unplaced = $true }
    }
    @{ Checks = @($checks); Phases = @($TS_AllPhases | Where-Object { $phases.Contains($_) }); Unplaced = $unplaced }
}

# What to rerun after a failed suite: $null = nothing (owner stopped it, a safety stop, or no rerun left); '' = the whole
# suite with the same phases; else the ORICOOP_PHASES list of only the failed phases.
# Safety stops are never flaky and never rerun (exit codes 5/6/7 too, see run-affected.ps1): a test that changed the
# owner's settings (T-094) could pass the second time only because the first run had already changed them; a test game
# that could read or rumble the owner's controller (T-085) or a copy on the owner's settings key must stop the run.
$TS_SafetyStop = 'OWNER SETTINGS CHANGED BY A TEST|changed the owner''s game settings|INPUT NOT BLOCKED|REFUSED copy|NO TEST COPY with its own Unity registry key'
function Get-RerunPhases([string]$suite, [string]$text, [string]$firstPhases) {
    if ($text -cmatch 'OWNER IS PLAYING') { return $null }
    if ($text -cmatch $TS_SafetyStop) { return $null }
    if ($TS_PhasedSuites -notcontains $suite) { return '' }
    $f = Get-FailedPhases $text
    if ($f.Unplaced -or $f.Phases.Count -eq 0) { return $firstPhases }
    return ($f.Phases -join ',')
}

# The ORICOOP_PHASES value for a selection ('' = every phase).
function Get-PhaseEnv($selection) {
    if ($selection.Phases -eq 'FULL') { return '' }
    $p = @($selection.Phases)
    return ($p -join ',')
}
