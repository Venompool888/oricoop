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

# Every mutation switch in the mod source has a row.
$listed = @($map.Mutations | ForEach-Object { $_.Switch })
$inSource = @(Get-ChildItem (Join-Path $TS_Root 'src\OriCoop') -Recurse -Filter *.cs | Select-String -Pattern '"(ORICOOP_MUTATE_\w+)"' -AllMatches |
    ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
foreach ($s in $inSource) { Ok ($listed -contains $s) ($s + ' (in the source) has a row in phase-map.psd1') }

# Every nightly mutation switch has a row.
$nightly = Join-Path $TS_Root 'src\run-nightly.ps1'
if (Test-Path $nightly) {
    $helpers = @('ORICOOP_NET_ONLY', 'ORICOOP_FORCE_ZH', 'ORICOOP_TITLEJOIN_LEAVE', 'ORICOOP_S4_SKEW', 'ORICOOP_PHASES')
    $nightlySwitches = @(Select-String -LiteralPath $nightly -Pattern "Env = @\{([^}]*)\}" | ForEach-Object { [regex]::Matches($_.Matches[0].Groups[1].Value, '(ORICOOP_\w+)\s*=') } |
        ForEach-Object { $_.Groups[1].Value } | Where-Object { $helpers -notcontains $_ } | Sort-Object -Unique)
    foreach ($s in $nightlySwitches) { Ok ($listed -contains $s) ($s + ' (nightly mutation) has a row in phase-map.psd1') }
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
$t = "[ 0.1] net role=host`n[ 0.2] === phase W`n[ 1.0] FAIL W digests`n[ 2.0] === phase CAM`n[ 3.0] PASS CAM x`n[ 4.0] === phase END`n[ 5.0] PASS G ok`n[ 0.1] net role=guest`n[ 0.2] === phase CUT`n[ 1.0] FAIL CUT step 3"
$f = Get-FailedPhases $t
Ok ((($f.Phases) -join ',') -eq 'W,CUT' -and -not $f.Unplaced) ('failed checks map to their phases (W,CUT; got ' + ($f.Phases -join ',') + ')')
Ok ((Get-RerunPhases 'net' $t 'W,CAM,CUT') -eq 'W,CUT') 'rerun only the failed phases'
Ok ((Get-RerunPhases 'net' ($t + "`n[ 6.0] === phase END`n[ 7.0] FAIL net no Unity errors") 'W,CAM,CUT') -eq 'W,CAM,CUT') 'a failure outside a phase reruns the same phases'
Ok ((Get-RerunPhases 'local' '[ 1.0] FAIL x' '') -eq '') 'other suites rerun whole'
Ok ($null -eq (Get-RerunPhases 'net' ('OWNER IS PLAYING' + "`n" + $t) '')) 'a run stopped for the owner is not rerun'
Ok ($null -eq (Get-RerunPhases 'rooms' ("ROOMS RESULT PASS`nOWNER SETTINGS CHANGED BY A TEST (T-094): HKCU\x: before a; after b") '')) 'a run that changed the owner''s settings is never rerun (a rerun could pass because the key is already changed)'
Ok ($null -eq (Get-RerunPhases 'local' "INPUT NOT BLOCKED in test game host: no line" '')) 'a run whose test game could read the owner''s controller is never rerun'
Ok ($null -eq (Get-RerunPhases 'net' "REFUSED copy e: it still uses the owner's Unity registry key" '')) 'a refused copy is never rerun'
$ff = "FAIL-FAST: stopping after the first failure`n  b-guest-1.log: FAIL E5Lc kill 1 puppet death effect exactly once`n  host.log: FAIL WO9L guest water height`nno result file: fail-fast stopped the run"
Ok ((Get-RerunPhases 'net' $ff '') -eq 'E5,WO') ('fail-fast summary checks are placed by name (E5,WO; got ' + (Get-RerunPhases 'net' $ff '') + ')')
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
