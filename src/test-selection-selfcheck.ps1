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

'SELECTION SELFCHECK ' + $(if ($fails) { 'FAIL' } else { 'PASS' }) + ' (' + ($n - $fails) + '/' + $n + ')'
if ($fails) { exit 1 }
