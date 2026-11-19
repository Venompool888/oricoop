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

# --- module -> suites (see docs\modules.md for what each module contains)
$TS_ModuleSuites = @{
    'M0' = $TS_AllSuites
    'M1' = @('net', 'net-relay', 'net-3p', 'net-4p', 'net-lp2', 'net-lp3', 'titlejoin', 'titlejoin-relay', 'coopsave')
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
$TS_FileSuites = [ordered]@{
    'TestHarness'      = @('local');   'TitleInputTest' = @('local');   'SignpostTest' = @('local')
    'NetIntroTest'     = @('net', 'net-4p');
    'NetTestHarness'   = @('net', 'net-3p', 'net-4p', 'net-lp2', 'net-lp3'); 'CoopSaveTest' = @('coopsave'); 'TitleJoinTest' = @('titlejoin', 'titlejoin-relay')
    # T-059 several local players per device online: the -LocalPlayers suites (2 devices / 3 players, 3 devices / 4 players).
    'NetLocalPlayersTest' = @('net-lp2', 'net-lp3'); 'OnlineLocalPlayers' = @('local', 'net', 'net-lp2', 'net-lp3'); 'OnlineRoomScreen' = @('net-lp2', 'net-lp3')
    'NetLocalSeats'    = @('net', 'net-lp2', 'net-lp3'); 'LocalEdgeWall' = @('local', 'net-lp2', 'net-lp3')
    'NativeRooms'      = @('rooms', 'local', 'titlejoin'); 'NativeGlowTint' = @('local'); 'ModeSelectLayout' = @('local'); 'RoomsHookTest' = @('rooms');
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
    if ($p -like 'src\CServer\*') { return @{ Suites = @('net'); Why = 'C-server (run-nettest -CServer by hand if it exists)' } }
    if ($p -like 'src\TestQuiet\*' -or $p -eq 'src\test-launch.ps1') { return @{ Suites = @('local', 'net'); Why = 'test launcher' } }
    if ($p -match '^src\\run-(\w+)\.ps1$') {
        $s = @{ 'test' = @('local'); 'nettest' = @('net'); 'coopsave' = @('coopsave'); 'titlejoin' = @('titlejoin'); 'rooms' = @('rooms') }[$Matches[1]]
        return @{ Suites = @($s | Where-Object { $_ }); Why = 'its own script' }
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

# The ORICOOP_PHASES value for a selection ('' = every phase).
function Get-PhaseEnv($selection) {
    if ($selection.Phases -eq 'FULL') { return '' }
    $p = @($selection.Phases)
    return ($p -join ',')
}
