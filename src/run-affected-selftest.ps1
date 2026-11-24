# Checks run-affected.ps1's rerun / flaky rules and per-suite phases with fake suite scripts (T-096). No game, ~1 min.
# Exit 0 = pass. Runs run-affected.ps1 -FakeSuites <temp dir>: the fake run-*.ps1 scripts print verdict lines like the real
# suites and record the ORICOOP_PHASES they were started with.
$ErrorActionPreference = 'Stop'
$fake = Join-Path $env:TEMP ('oricoop-affected-selftest-' + $PID)
New-Item -ItemType Directory -Force $fake | Out-Null
$fail = 0
function Expect([bool]$ok, [string]$what) { if ($ok) { 'PASS ' + $what } else { $script:fail++; 'FAIL ' + $what } }

# Each fake suite writes "<script> <args> phases=<ORICOOP_PHASES>" to its own file in calls\; its behaviour comes from FAKE_* variables.
# T-160: the name holds EVERY argument (undeclared ones such as -Clients 3 or -LocalPlayers land in $args), so two suites
# never share a call counter; the planted net behaviours apply only to the plain 'net' suite ($plain).
$common = @'
param([switch]$GuestNewGame, [string]$Transport = '')
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$extra = @($args) -join ' '
$name = [IO.Path]::GetFileNameWithoutExtension($MyInvocation.MyCommand.Path) + $(if ($GuestNewGame) { ' -GuestNewGame' } elseif ($Transport) { ' -Transport ' + $Transport } else { '' }) + $(if ($extra) { ' ' + $extra } else { '' })
$plain = -not $GuestNewGame -and -not $Transport -and -not $extra
$calls = Join-Path $dir 'calls'; New-Item -ItemType Directory -Force $calls | Out-Null   # one file per call: suites run in parallel
$n = @(Get-ChildItem $calls -Filter *.txt | ForEach-Object { Get-Content $_.FullName } | Where-Object { $_ -like ($name + ' phases=*') }).Count + 1
Set-Content (Join-Path $calls ([guid]::NewGuid().ToString() + '.txt')) ($name + ' phases=' + $env:ORICOOP_PHASES)
'TEST COPY z: fake'
'@
Set-Content (Join-Path $fake 'run-test.ps1') ($common + [Environment]::NewLine + @'
if ($env:FAKE_LOCAL -eq 'leak' -and $n -eq 1) { '[   2.00] RESULT PASS 2/2'; 'OWNER SETTINGS CHANGED BY A TEST (T-094): HKCU\x: before w=3840; after w=640'; 'FAIL: this run changed the owner''s game settings (exit 7, T-094).'; exit 7 }
if ($env:FAKE_LOCAL -eq 'leak0') { '[   2.00] RESULT PASS 2/2'; 'OWNER SETTINGS CHANGED BY A TEST (T-094): HKCU\x: before w=3840; after w=640'; exit 0 }
if ($env:FAKE_LOCAL -eq 'flaky' -and $n -eq 1) { '[   1.00] FAIL visual self-check: no P2 visual problems'; '[   2.00] RESULT FAIL 1/2'; exit 1 }
'[   2.00] RESULT PASS 2/2'
'@)
Set-Content (Join-Path $fake 'run-titlejoin.ps1') ($common + [Environment]::NewLine + @'
'[   1.00] PASS host: in game'; '[   2.00] RESULT PASS 1/1'; 'TITLEJOIN direct PASS'
'@)
# Like the harness, the fake net suite marks every phase it runs ("=== phase X", ORICOOP_PHASES or all) and then END.
Set-Content (Join-Path $fake 'run-nettest.ps1') ($common + [Environment]::NewLine + @'
$ph = @($(if ($env:ORICOOP_PHASES) { $env:ORICOOP_PHASES -split ',' } else { @('INTRO', 'MOVE', 'W', 'E3', 'E5', 'R', 'S4', 'P', 'WO', 'CAM', 'CUT', 'ESC') }))
$pb = 'PB the guest''s push moved the held stone'
if ($plain -and $env:FAKE_NET -eq 'failfast' -and $n -eq 1) {
    # T-226: fail-fast stopped the games at the first failure (phase P): its summary only, no result files, no markers.
    'FAIL-FAST: stopping after the first failure; set ORICOOP_FAIL_FAST=0 for a full run. Failures so far:'; ('  host-1.log: FAIL ' + $pb)
    '--- host-result.txt ---'; ('no result file: fail-fast stopped the run after: ' + $pb)
    '--- guest-result.txt ---'; ('no result file: fail-fast stopped the run after: ' + $pb); 'NETTEST FAIL'; exit 1
}
'[   0.10] net role=host'
if ($plain -and $env:FAKE_NET -eq 'wo') {
    foreach ($x in $ph) { '[   1.00] === phase ' + $x; $(if ($x -eq 'WO') { '[   3.00] FAIL WO1 lever state matches' } else { '[   1.00] PASS ' + $x + ' ok' }) }
    '[   4.00] === phase END'; '[   5.00] PASS G puppet removed within 7 s'; '[   6.00] RESULT FAIL'; 'NETTEST FAIL'; exit 1
}
if ($plain -and $env:FAKE_NET -eq 'pfail' -and $n -eq 1) {
    foreach ($x in $ph) { '[   1.00] === phase ' + $x; $(if ($x -eq 'P') { '[   2.00] FAIL ' + $pb } else { '[   1.00] PASS ' + $x + ' ok' }) }
    '[   4.00] === phase END'; '[   6.00] RESULT FAIL'; 'NETTEST FAIL'; exit 1
}
if ($plain -and $env:FAKE_NET -eq 'startup' -and $n -eq 1) {
    '[   0.20] FAIL net puppet appears within 40 s'; '[   6.00] RESULT FAIL'; 'NETTEST FAIL'; exit 1
}
if ($plain -and $env:FAKE_RERUN -eq 'first-phase-only' -and $n -eq 2) { $ph = @($ph[0]) }   # a broken rerun: runs only its first phase, passes
foreach ($x in $ph) { '[   1.00] === phase ' + $x; '[   1.00] PASS ' + $x + ' ok' }
'[   2.00] === phase END'; '[   6.00] RESULT PASS 2/2'; 'NETTEST PASS'
'@)

function Invoke-Fake([hashtable]$vars, [string[]]$files) {
    Remove-Item (Join-Path $fake 'calls') -Recurse -Force -ErrorAction SilentlyContinue
    $flaky = Join-Path $fake 'flaky.tsv'; Remove-Item $flaky -ErrorAction SilentlyContinue
    foreach ($k in $vars.Keys) { Set-Item -Path ('env:' + $k) -Value $vars[$k] }
    $env:ORICOOP_FLAKY_LOG = $flaky
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'run-affected.ps1') -FakeSuites $fake -Parallel 3 -Files $files 2>&1 | ForEach-Object { '' + $_ }
    $code = $LASTEXITCODE
    foreach ($k in $vars.Keys) { Remove-Item -Path ('env:' + $k) -ErrorAction SilentlyContinue }
    Remove-Item env:ORICOOP_FLAKY_LOG -ErrorAction SilentlyContinue
    @{ Out = ($out -join "`n"); Code = $code; Calls = @(Get-ChildItem (Join-Path $fake 'calls') -Filter *.txt -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName }); Flaky = @(Get-Content $flaky -ErrorAction SilentlyContinue) }
}
# -File passes a comma list as one string; run-affected splits nothing itself, so give the files as one argument each.
$files = 'src/run-test.ps1,src/run-titlejoin.ps1,src/OriCoop/WorldObjSync.cs'

# 0. T-160: a fake call is named by all its arguments, so net-3p (-Clients 3) is never counted as the plain net suite.
Remove-Item (Join-Path $fake 'calls') -Recurse -Force -ErrorAction SilentlyContinue
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fake 'run-nettest.ps1') -Clients 3 | Out-Null
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fake 'run-nettest.ps1') | Out-Null
$c0 = @(Get-ChildItem (Join-Path $fake 'calls') -Filter *.txt | ForEach-Object { Get-Content $_.FullName })
Expect ($c0 -contains 'run-nettest -Clients 3 phases=' -and $c0 -contains 'run-nettest phases=') ('fake suites are named by all their arguments (got: ' + ($c0 -join ' | ') + ')')

# 1. local flaky, net fails twice in WO, titlejoin passes.
$r = Invoke-Fake @{ FAKE_LOCAL = 'flaky'; FAKE_NET = 'wo' } $files
Expect ($r.Out -match 'PLAN: .*net \[R,WO\]') 'plan: net runs the selected phases R,WO (WorldObjSync, T-144)'
Expect (@($r.Calls | Where-Object { $_ -eq 'run-nettest phases=R,WO' }).Count -eq 1 -and @($r.Calls | Where-Object { $_ -eq 'run-nettest phases=WO' }).Count -eq 1) 'net failed in WO: rerun once with only WO, never a third time'
Expect (@($r.Calls | Where-Object { $_ -like 'run-test phases=*' }).Count -eq 2) 'local failed once: rerun once'
Expect (@($r.Calls | Where-Object { $_ -like 'run-titlejoin phases=*' }).Count -eq 1) 'titlejoin passed: not rerun'
Expect ($r.Out -match '(?m)^local: FLAKY') 'local that passes on the rerun is FLAKY'
Expect ($r.Out -match '(?m)^net: FAIL \(twice\)') 'net that fails the rerun too is FAIL (twice)'
Expect ($r.Out -match 'AFFECTED FAIL' -and $r.Code -eq 1) 'a suite failing twice fails the run (exit 1)'
Expect (@($r.Flaky | Where-Object { $_ -like "*`tlocal`tvisual self-check: no P2 visual problems" }).Count -eq 1) 'the flaky check is logged with its suite'

# 2. only a flaky failure: the run passes, marked flaky.
$r = Invoke-Fake @{ FAKE_LOCAL = 'flaky' } 'src/run-test.ps1'
Expect ($r.Out -match 'AFFECTED PASS \(flaky, passed on the rerun: local\)' -and $r.Code -eq 0) 'a flaky suite alone: AFFECTED PASS, named as flaky'

# 2b. a suite that changed the owner's settings (exit 7): FAIL at once, never rerun, never FLAKY.
$r = Invoke-Fake @{ FAKE_LOCAL = 'leak' } 'src/run-test.ps1'
Expect (@($r.Calls).Count -eq 1 -and $r.Out -match '(?m)^local: FAIL' -and $r.Out -match 'AFFECTED FAIL' -and $r.Code -eq 1) 'owner settings changed (exit 7): no rerun, the run fails'

# 2c. T-117: the owner-settings failure text alone fails the suite, even when its script exits 0 (run-rooms used to).
$r = Invoke-Fake @{ FAKE_LOCAL = 'leak0' } 'src/run-test.ps1'
Expect (@($r.Calls).Count -eq 1 -and $r.Out -match '(?m)^local: FAIL' -and $r.Code -eq 1) 'OWNER SETTINGS CHANGED with exit 0: FAIL, no rerun'

# 3. reruns off.
$r = Invoke-Fake @{ FAKE_LOCAL = 'flaky'; ORICOOP_RERUN = '0' } 'src/run-test.ps1'
Expect (@($r.Calls).Count -eq 1 -and $r.Out -match 'AFFECTED FAIL') 'ORICOOP_RERUN=0: no rerun, the failure stands'

# 4. a failure outside any phase (start-up) reruns the suite with the same phases.
$r = Invoke-Fake @{ FAKE_NET = 'startup' } 'src/OriCoop/WorldObjSync.cs'
Expect (@($r.Calls | Where-Object { $_ -eq 'run-nettest phases=R,WO' }).Count -eq 2 -and $r.Out -match '(?m)^net: FLAKY') 'start-up failure: rerun with the same phases (R,WO); passing then = FLAKY'

# 4b. T-226: fail-fast stopped the run at its first failure (PB, phase P) before the result files existed. S4 ran before
#     P and passed; P failed; WO and CAM never ran. The rerun runs P, WO and CAM (not P alone: a pass of P then counted
#     WO and CAM as passed although they never ran), and passing then = FLAKY.
$tf = 'src/OriCoop/NetStoryTest.cs,src/OriCoop/NetPhysTest.cs,src/OriCoop/NetWorldObjTest.cs,src/OriCoop/NetCameraTest.cs'
$r = Invoke-Fake @{ FAKE_NET = 'failfast' } $tf
Expect ($r.Out -match 'PLAN: .*net \[S4,P,WO,CAM\]') 'plan: net runs S4,P,WO,CAM (four net test files)'
Expect (@($r.Calls | Where-Object { $_ -eq 'run-nettest phases=S4,P,WO,CAM' }).Count -eq 1 -and @($r.Calls | Where-Object { $_ -eq 'run-nettest phases=P,WO,CAM' }).Count -eq 1 -and @($r.Calls).Count -eq 2) ('fail-fast in P: rerun P and the phases that never ran (P,WO,CAM), not S4 (calls: ' + ($r.Calls -join ' | ') + ')')
Expect ($r.Out -match 'net: FAIL, rerun phases P,WO,CAM \(failed P; never ran WO,CAM\)') 'the rerun line names the failed phase and the phases that never ran'
Expect ($r.Out -match '(?m)^net: FLAKY' -and $r.Code -eq 0) 'fail-fast, then the rerun of every phase left passes: FLAKY'

# 4c. T-226: a run that went to its end (every phase marked, END) and failed in P: the later phases ran and passed, so
#     only P is rerun.
$r = Invoke-Fake @{ FAKE_NET = 'pfail' } $tf
Expect (@($r.Calls | Where-Object { $_ -eq 'run-nettest phases=P' }).Count -eq 1 -and @($r.Calls).Count -eq 2 -and $r.Out -match '(?m)^net: FLAKY') ('a full run failing in P: rerun P only (calls: ' + ($r.Calls -join ' | ') + ')')

# 4d. T-226: a rerun that passes without running every phase it was given (here only P of P,WO,CAM) is no pass: the
#     phases it skipped never ran in either run.
$r = Invoke-Fake @{ FAKE_NET = 'failfast'; FAKE_RERUN = 'first-phase-only' } $tf
Expect ($r.Out -match '(?m)^net: FAIL \(the rerun passed without running phase WO,CAM' -and $r.Out -match 'AFFECTED FAIL' -and $r.Code -eq 1 -and @($r.Flaky).Count -eq 0) 'a rerun that skipped phases WO,CAM: FAIL, not FLAKY'

# 5. A file whose module selects every phase (M7): net keeps them all, net-newgame only its own phases (T-096; net-relay,
#    the other capped suite, went with the relay in T-159).
$r = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'run-affected.ps1') -DryRun -Files 'src/OriCoop/Rooms/RoomSession.cs' 2>&1 | ForEach-Object { '' + $_ }
Expect (($r -join "`n") -match 'PLAN: .*net-newgame \[INTRO,MOVE,W\]') 'plan: net-newgame runs only its own phases INTRO,MOVE,W'
Expect (($r -join "`n") -notmatch 'relay') 'plan: no relay suite any more'

# 6. -All (nightly) runs every phase in every 2-player suite.
$r = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'run-affected.ps1') -DryRun -All -Files 'src/run-test.ps1' 2>&1 | ForEach-Object { '' + $_ }
Expect (($r -join "`n") -match 'net \[ALL\]' -and ($r -join "`n") -match 'net-newgame \[ALL\]') '-All: net and net-newgame run every phase'

if (-not $env:KEEP_FAKE) { Remove-Item $fake -Recurse -Force }
if ($fail) { "AFFECTED SELFTEST FAIL ($fail)"; exit 1 } else { 'AFFECTED SELFTEST PASS' }
