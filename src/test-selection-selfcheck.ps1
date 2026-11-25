# Proves the change -> tests selection cannot skip a check that would catch a known break (T-078). No game, ~1 s.
# For every mutation switch in src\phase-map.psd1: the switch is really in that file, and the tests selected for a change
# to that file include the mutation's suite and (2-player net suites) its catching phase. Also: every ORICOOP_MUTATE_*
# switch in the source and every nightly mutation has a row; the phase list matches NetTestHarness.AllPhases; every
# net test file has a phase. Exit 0 = all pass. Run by run-affected.ps1 before anything else, and by the nightly.
param([string]$Root = '')
$ErrorActionPreference = 'Stop'
$TS_Root = $(if ($Root) { $Root } else { Split-Path -Parent $PSScriptRoot })
. (Join-Path $PSScriptRoot 'test-selection.ps1')
$map = Get-TSPhaseMap
$fails = 0; $n = 0
function Ok([bool]$cond, [string]$what) { $script:n++; if ($cond) { 'PASS ' + $what } else { $script:fails++; 'FAIL ' + $what } }

foreach ($m in $map.Mutations) {
    $path = Join-Path $TS_Root $m.File
    $needle = $(if ($m.Mention) { $m.Mention } else { $m.Switch })
    $present = (Test-Path $path) -and (Select-String -LiteralPath $path -SimpleMatch $needle -Quiet)
    Ok $present ($m.Switch + ': "' + $needle + '" is in ' + $m.File)
    $sel = Get-Selection @($m.File)
    $suite = $(if ($m.Suite -eq 'net') { 'net' } else { $m.Suite })
    Ok ($sel.Suites -contains $suite) ($m.Switch + ': a change to ' + $m.File + ' runs suite ' + $suite + ' (selected: ' + ($sel.Suites -join ',') + ')')
    if ($m.Phase) {
        $phOk = ($sel.Phases -eq 'FULL') -or (@($sel.Phases) -contains $m.Phase)
        Ok $phOk ($m.Switch + ': ... and phase ' + $m.Phase + ' (selected: ' + $(if ($sel.Phases -eq 'FULL') { 'FULL' } else { @($sel.Phases) -join ',' }) + ')')
    }
}

# T-165: a change to a file whose code a 3-4 player suite checks (and no 2-player suite does) selects that suite. The
# checks: L lineage / late-guest commit / promoted host / 4P wipe reload (saves), 4P enemy owner + duplicate hit (enemies),
# 4P revive / spectate / menu / wipe + 3P soul link + cross-device revive (rules), 3P pickup dedupe (progress), digests on
# three (world), seats and handover (seats), extra local players' bars (BottomHud), multi-seat INTRO.
$multiRows = @(
    @('src\OriCoop\SaveSync.cs', @('net-3p', 'net-3p-cs', 'net-4p', 'net-lp2', 'net-lp3', 'net')),
    @('src\OriCoop\CoopSaves.cs', @('net-3p', 'net-3p-cs', 'net-4p', 'net-lp2', 'net-lp3', 'net')),
    @('src\OriCoop\CoopSaveTest.cs', @('net-3p', 'net-3p-cs', 'net-4p', 'net-lp2', 'net-lp3', 'net', 'coopsave')),
    @('src\OriCoop\GuestJoin.cs', @('net-3p', 'net-3p-cs', 'net-4p')),
    @('src\OriCoop\Saves\LoadRestart.cs', @('net-4p')),
    @('src\OriCoop\EnemySync.cs', @('net-4p')),
    @('src\OriCoop\Revive.cs', @('net-3p', 'net-4p', 'net-lp2', 'net-lp3')),
    @('src\OriCoop\SessionRules.cs', @('net-4p')),
    @('src\OriCoop\PartnerFx.cs', @('net-3p', 'net-4p', 'net-lp2', 'net-lp3')),
    @('src\OriCoop\ProgressSync.cs', @('net-3p', 'net-3p-cs')),
    @('src\OriCoop\WorldSync.cs', @('net-3p', 'net-3p-cs')),
    @('src\OriCoop\Players.cs', @('net-3p', 'net-3p-cs', 'net-4p', 'net-lp2', 'net-lp3')),
    @('src\OriCoop\Seats\GameSeatView.cs', @('net-lp2', 'net-lp3')),
    @('src\OriCoop\Authority\HostArbiter.cs', @('net-3p-cs')),
    @('src\OriCoop\BottomHud.cs', @('net-lp2', 'net-lp3', 'local')),
    @('src\OriCoop\NetIntroTest.cs', @('net-3p', 'net-3p-cs', 'net-4p'))
)
foreach ($row in $multiRows) {
    $got = @((Get-Selection @($row[0])).Suites)
    $missing = @($row[1] | Where-Object { $got -notcontains $_ })
    Ok ($missing.Count -eq 0) ('a change to ' + $row[0] + ' runs ' + ($row[1] -join ',') + $(if ($missing.Count) { ' (missing: ' + ($missing -join ',') + ')' } else { '' }))
}
foreach ($file in @('SteamTransport.cs', 'Net\SteamApi.cs', 'Net\RealSteamApi.cs', 'Net\FakeSteamApi.cs', 'SteamInviteHook.cs', 'NetPlay.cs')) {   # T-216: NetPlay drives the Steam session (PumpSteam)
    $got = @((Get-Selection @('src\OriCoop\' + $file)).Suites)
    Ok ($got -contains 'titlejoin-steam') ('a change to ' + $file + ' runs titlejoin-steam')
    Ok ($got -contains 'titlejoin-steam-retry') ('a change to ' + $file + ' runs titlejoin-steam-retry')
}
foreach ($file in @('Net\NetPeer.cs', 'InviteRules.cs')) {
    $got = @((Get-Selection @('src\OriCoop\' + $file)).Suites)
    Ok ($got -notcontains 'titlejoin-steam') ('a change to ' + $file + ' does not run titlejoin-steam')
}
# A shared-core file (every phase, phase-map.psd1 Full) runs the direct 'net' suite, the one 2-player suite that is never
# limited to a few phases (T-096): selecting only net-relay / net-newgame would run just INTRO,MOVE,W (T-165 found
# SaveSync and GuestJoin like that).
foreach ($leaf in $map.Full) {
    $got = @((Get-Selection @('src\OriCoop\' + $leaf + '.cs')).Suites)
    Ok ($got -contains 'net') ("shared-core file $leaf runs the direct 'net' suite (every phase)" + $(if ($got -notcontains 'net') { ' (selected: ' + ($got -join ',') + ')' } else { '' }))
}
# Rooms files (M7) do not drag in the multi-seat suites: their code never runs there.
$rooms = @((Get-Selection @('src\OriCoop\Rooms\RoomsPanel.cs')).Suites)
Ok (@($rooms | Where-Object { @('net-3p', 'net-3p-cs', 'net-4p', 'net-lp2', 'net-lp3') -contains $_ }).Count -eq 0) ('a rooms UI change does not run the multi-seat suites (selected: ' + ($rooms -join ',') + ')')

# Every mutation switch in the mod source has a row.
$listed = @($map.Mutations | ForEach-Object { $_.Switch })
$inSource = @(Get-ChildItem (Join-Path $TS_Root 'src\OriCoop') -Recurse -Filter *.cs | Select-String -Pattern '"(ORICOOP_MUTATE_\w+)"' -AllMatches |
    ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
foreach ($s in $inSource) { Ok ($listed -contains $s) ($s + ' (in the source) has a row in phase-map.psd1') }

# Every nightly mutation switch has a row.
$nightly = Join-Path $TS_Root 'src\run-nightly.ps1'
if (Test-Path $nightly) {
    $helpers = @('ORICOOP_NET_ONLY', 'ORICOOP_FORCE_ZH', 'ORICOOP_TITLEJOIN_LEAVE', 'ORICOOP_S4_SKEW', 'ORICOOP_PHASES', 'ORICOOP_P3_KURO', 'ORICOOP_P3_SEIN', 'ORICOOP_P3_EXTERNAL')   # P3 helpers select native CUT scenarios
    $nightlySwitches = @(Select-String -LiteralPath $nightly -Pattern "Env = @\{([^}]*)\}" | ForEach-Object { [regex]::Matches($_.Matches[0].Groups[1].Value, '(ORICOOP_\w+)\s*=') } |
        ForEach-Object { $_.Groups[1].Value } | Where-Object { $helpers -notcontains $_ } | Sort-Object -Unique)
    foreach ($s in $nightlySwitches) { Ok ($listed -contains $s) ($s + ' (nightly mutation) has a row in phase-map.psd1') }
}

# T-118: a change to a suite's own script runs every suite that uses that script; a change to the launcher or TestQuiet
# runs every suite (all of them start their games through it).
foreach ($s in $TS_Suites.Keys) {
    $file = 'src\' + $TS_Suites[$s][0]
    $got = @((Get-Selection @($file)).Suites)
    Ok ($got -contains $s) ('a change to ' + $file + ' runs suite ' + $s + ' (selected: ' + ($got -join ',') + ')')
}
foreach ($file in @('src\test-launch.ps1', 'src\TestQuiet\InputBlock.cs')) {
    $got = @((Get-Selection @($file)).Suites)
    Ok (($got -join ',') -eq ($TS_AllSuites -join ',')) ('a change to ' + $file + ' runs every suite (selected: ' + ($got -join ',') + ')')
}

# The phase list matches the harness.
$harness = Get-Content (Join-Path $TS_Root 'src\OriCoop\NetTestHarness.cs') -Raw
$m2 = [regex]::Match($harness, 'AllPhases\s*=\s*\{([^}]*)\}')
$csPhases = @(([regex]::Matches($m2.Groups[1].Value, '"(\w+)"')) | ForEach-Object { $_.Groups[1].Value })
Ok (($csPhases -join ',') -eq ($TS_AllPhases -join ',')) ('phase list matches NetTestHarness.AllPhases (' + ($csPhases -join ',') + ')')
foreach ($k in @($map.Modules.Values) + @($map.Files.Values)) {
    foreach ($x in ($k -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -ne 'FULL' })) { if ($TS_AllPhases -notcontains $x) { Ok $false ('unknown phase ' + $x + ' in phase-map.psd1') } }
}

# Every net test file has a phase (a new phase file must not fall through silently).
foreach ($f in Get-ChildItem (Join-Path $TS_Root 'src\OriCoop') -Filter 'Net*Test.cs') {
    $leaf = [IO.Path]::GetFileNameWithoutExtension($f.Name)
    Ok ($map.Files.ContainsKey($leaf) -or $map.Full -contains $leaf) ($leaf + ' has a phase in phase-map.psd1')
}

# T-096: per-suite phase caps never take a phase away from the direct 'net' suite (every mutation row with Suite 'net'
# is caught there), and the caps name real phases.
Ok (-not $TS_SuitePhases.Contains('net')) "the direct 'net' suite is never capped"
foreach ($m in @($map.Mutations | Where-Object { $_.Suite -eq 'net' -and $_.Phase })) {
    $pe = Get-SuitePhaseEnv 'net' (Get-Selection @($m.File))
    Ok ((-not $pe) -or (@($pe -split ',') -contains $m.Phase)) ($m.Switch + ": suite 'net' still runs phase " + $m.Phase + ' (' + $(if ($pe) { $pe } else { 'ALL' }) + ')')
}
foreach ($k in $TS_SuitePhases.Keys) { foreach ($x in $TS_SuitePhases[$k]) { Ok ($TS_AllPhases -contains $x) ("capped suite $k phase $x exists") } }
# Rerun planning reads the harness's phase markers.
$t = "[ 0.1] net role=host`n[ 0.2] === phase W`n[ 1.0] FAIL W digests`n[ 2.0] === phase CAM`n[ 3.0] PASS CAM x`n[ 3.5] === phase CUT`n[ 4.0] === phase END`n[ 5.0] PASS G ok`n" +
    "[ 0.1] net role=guest`n[ 0.2] === phase W`n[ 0.5] === phase CAM`n[ 0.7] === phase CUT`n[ 1.0] FAIL CUT step 3`n[ 2.0] === phase END"
$f = Get-FailedPhases $t
Ok ((($f.Phases) -join ',') -eq 'W,CUT' -and -not $f.Unplaced) ('failed checks map to their phases (W,CUT; got ' + ($f.Phases -join ',') + ')')
Ok ((Get-RerunPhases 'net' $t 'W,CAM,CUT') -eq 'W,CUT') 'a run that went to its end: rerun only the failed phases'
# T-226: a phase never counts as passed unless it ran. A client section without END was cut short: everything from the
# first failed phase on is rerun (here CAM too); a phase that a finished run never marked is rerun as well.
$cut = "[ 0.1] net role=host`n[ 0.2] === phase W`n[ 1.0] FAIL W digests`n[ 2.0] === phase CAM`n[ 3.0] PASS CAM x`n[ 4.0] === phase END`n[ 0.1] net role=guest`n[ 0.2] === phase W`n[ 0.5] === phase CAM`n[ 0.7] === phase CUT`n[ 1.0] FAIL CUT step 3"
Ok ((Get-RerunPhases 'net' $cut 'W,CAM,CUT') -eq 'W,CAM,CUT') ('a client cut short (no END): rerun from the first failed phase on (W,CAM,CUT; got ' + (Get-RerunPhases 'net' $cut 'W,CAM,CUT') + ')')
$skip = "[ 0.1] net role=host`n[ 0.2] === phase E3`n[ 0.3] === phase E5`n[ 1.0] FAIL E5 kill`n[ 4.0] === phase END`n[ 0.1] net role=guest`n[ 0.2] === phase E3`n[ 0.3] === phase E5`n[ 4.0] === phase END"
Ok ((Get-RerunPhases 'net' $skip 'E3,E5,R,S4') -eq 'E5,R,S4') ('phases no client marked are rerun with the failed one (E5,R,S4; got ' + (Get-RerunPhases 'net' $skip 'E3,E5,R,S4') + ')')
$plan = Get-RerunPlan 'net' $skip 'E3,E5,R,S4'
Ok ((@($plan.Failed) -join ',') -eq 'E5' -and (@($plan.NotRun) -join ',') -eq 'R,S4') 'the rerun plan names the failed and the never-run phases'
Ok ((Get-UnrunPhases $t 'W,CAM,CUT').Count -eq 0 -and (@(Get-UnrunPhases $t 'W,CAM,CUT,ESC') -join ',') -eq 'ESC' -and (@(Get-UnrunPhases 'NETTEST PASS' 'P,WO') -join ',') -eq 'P,WO') 'a passed run without the markers of a phase did not run it'
Ok ((Get-RerunPhases 'net' ($t + "`n[ 6.0] === phase END`n[ 7.0] FAIL net no Unity errors") 'W,CAM,CUT') -eq 'W,CAM,CUT') 'a failure outside a phase reruns the same phases'
Ok ((Get-RerunPhases 'local' '[ 1.0] FAIL x' '') -eq '') 'other suites rerun whole'
Ok ($null -eq (Get-RerunPhases 'net' ('OWNER IS PLAYING' + "`n" + $t) '')) 'a run stopped for the owner is not rerun'
Ok ($null -eq (Get-RerunPhases 'rooms' ("ROOMS RESULT PASS`nOWNER SETTINGS CHANGED BY A TEST (T-094): HKCU\x: before a; after b") '')) 'a run that changed the owner''s settings is never rerun (a rerun could pass because the key is already changed)'
Ok ($null -eq (Get-RerunPhases 'local' "INPUT NOT BLOCKED in test game host: no line" '')) 'a run whose test game could read the owner''s controller is never rerun'
Ok ($null -eq (Get-RerunPhases 'net' "REFUSED copy e: it still uses the owner's Unity registry key" '')) 'a refused copy is never rerun'
$ff = "FAIL-FAST: stopping after the first failure`n  b-guest-1.log: FAIL E5Lc kill 1 puppet death effect exactly once`n  host.log: FAIL WO9L guest water height`nno result file: fail-fast stopped the run"
Ok (((Get-FailedPhases $ff).Phases -join ',') -eq 'E5,WO') ('fail-fast summary checks are placed by name (E5,WO; got ' + ((Get-FailedPhases $ff).Phases -join ',') + ')')
# T-226: fail-fast stopped the run in E5, so E5 and every later phase never ran to its end: all of them are rerun, the
# phases before E5 (they ran and passed) are not.
Ok ((Get-RerunPhases 'net' $ff '') -eq 'E5,R,S4,P,WO,CAM,CUT,ESC') ('fail-fast: rerun the failed phase and every later one (E5,R,S4,P,WO,CAM,CUT,ESC; got ' + (Get-RerunPhases 'net' $ff '') + ')')
Ok ((Get-RerunPhases 'net' $ff 'INTRO,E5,WO,CAM') -eq 'E5,WO,CAM') ('fail-fast with selected phases: from the first failed one on (E5,WO,CAM; got ' + (Get-RerunPhases 'net' $ff 'INTRO,E5,WO,CAM') + ')')
$ffIntro = "FAIL-FAST: stopping after the first failure`n  host-1.log: FAIL INTRO barrier 0 all seats observed`nno result file: fail-fast stopped the run"
Ok ((Get-RerunPhases 'net-newgame' $ffIntro 'INTRO,MOVE,W') -eq 'INTRO,MOVE,W') 'fail-fast in the first phase: the whole suite again'
foreach ($pair in @(@('INTRO P2 alone retains input', 'INTRO'), @('C RTT 30..260', 'MOVE'), @('W both world digests match', 'W'), @('E3 guest sees enemy', 'E3'),
                    @('E5Lc kill', 'E5'), @('R2 host: revive glow played once', 'R'), @('S4 partner step 3 completed', 'S4'), @('PB two pushers', 'P'), @('P3L200oneway stone', 'P'), @('P1L push', 'P'),
                    @('WO9L guest water', 'WO'), @('CAM guest: pull', 'CAM'), @('CUT reset: WallJump', 'CUT'), @('Kuro gate', 'CUT'))) {
    Ok ((Get-CheckPhase $pair[0]) -eq $pair[1]) ('check "' + $pair[0] + '" is in phase ' + $pair[1])
}
foreach ($c in @('P3 lever found', 'P4 native story and End', 'net no Unity errors/exceptions', 'G puppet removed within 7 s', 'L guest slot list shows the kept copy', 'digest-mismatch full resyncs')) { Ok ($null -eq (Get-CheckPhase $c)) ('check "' + $c + '" is in no phase (whole rerun)') }
$harnessText = Get-Content (Join-Path $TS_Root 'src\OriCoop\NetTestHarness.cs') -Raw
foreach ($ph in $TS_AllPhases) { Ok ($harnessText -match ('Mark\(h, "' + $ph + '"\)')) ("the harness marks phase $ph for reruns") }

'SELECTION SELFCHECK ' + $(if ($fails) { 'FAIL' } else { 'PASS' }) + ' (' + ($n - $fails) + '/' + $n + ')'
if ($fails) { exit 1 }
