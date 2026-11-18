# Offline integration test: exercises the runner's journal recovery + report path without launching a game.
$ErrorActionPreference = 'Stop'
$src = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fixture = Join-Path $src ('bin\sweep-runner-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $fixture | Out-Null
try {
    $mock = Join-Path $fixture 'mock'
    New-Item -ItemType Directory -Force (Join-Path $mock 'OriCoop\Sweep') | Out-Null
    $runner = Join-Path $mock 'run-scenesweep.ps1'
    # Keep even generated reports/cache inside this worktree. The replacement is an exact literal.
    [IO.File]::WriteAllText($runner, [IO.File]::ReadAllText((Join-Path $src 'run-scenesweep.ps1')).Replace("'C:\OriCoop\test-reports'", ("'" + $fixture + "'")))
    Copy-Item (Join-Path $src 'OriCoop\Sweep\*.cs') (Join-Path $mock 'OriCoop\Sweep')
    @'
index,kind,scene,pedestal
0,scene,a,-1
1,save,a,0
2,scene,b,-1
'@ | Set-Content -LiteralPath (Join-Path $fixture 'host.plan.csv') -Encoding ASCII
    @'
index,side,kind,scene,verdict,fails,t0,t1,note
0,host,scene,a,PASS,,2026-11-18T01:00:00Z,2026-11-18T01:01:00Z,ok
1,host,save,a,RUNNING,,2026-11-18T01:01:00Z,,
'@ | Set-Content -LiteralPath (Join-Path $fixture 'host.csv') -Encoding ASCII
    @'
index,side,kind,scene,verdict,fails,t0,t1,note
0,guest,scene,a,PASS,,2026-11-18T01:00:00Z,2026-11-18T01:01:00Z,ok
'@ | Set-Content -LiteralPath (Join-Path $fixture 'guest.csv') -Encoding ASCII
    $output = & (Join-Path $PSHOME 'powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $runner -ReportOnly -Resume $fixture 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($output -join "`n") }
    if (($output -join "`n") -notmatch 'SWEEP DONE pass=1 fail=1 inconclusive=1') { throw ('Unexpected summary: ' + ($output -join "`n")) }
    $h = @(Import-Csv (Join-Path $fixture 'host.csv'))
    $g = @(Import-Csv (Join-Path $fixture 'guest.csv'))
    if ($h[-1].verdict -ne 'FAIL' -or $g[-1].fails -ne 'crash' -or -not $g[-1].t1) { throw 'Interrupted entry was not finalized on both sides' }
    $count = $h.Count + $g.Count
    $again = & (Join-Path $PSHOME 'powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $runner -ReportOnly -Resume $fixture 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($again -join "`n") }
    if (@(Import-Csv (Join-Path $fixture 'host.csv')).Count + @(Import-Csv (Join-Path $fixture 'guest.csv')).Count -ne $count) { throw 'Recovery was not idempotent' }
    Write-Host 'PASS runner crash recovery, missing tail, report output, and idempotent resume (no game)'
    # Isolated fake launchers exercise the actual restart loop; only PowerShell processes are started.
    $legacy = Join-Path $fixture 'legacy'
    New-Item -ItemType Directory -Force $legacy | Out-Null
    foreach ($side in @('host','guest')) {
        "index,side,kind,scene,verdict,fails,t0,t1,note`n0,$side,scene,a,PASS,0,start,end," | Set-Content (Join-Path $legacy ($side + '.catalog.csv'))
    }
    @'
function Start-TestRun {
    $script:TL_Copy = 'mock'; $script:TL_TestDir = Join-Path $PSScriptRoot 'test'
    New-Item -ItemType Directory -Force $script:TL_TestDir | Out-Null
    $script:TL_LockFile = Join-Path $PSScriptRoot 'mock.lock'
    'mock' | Set-Content $script:TL_LockFile
}
function Complete-TestRun { }
function Exit-TestRun { Remove-Item $script:TL_LockFile -Force }
function Get-ClientLog($tag) { Join-Path $script:TL_TestDir ($tag + '.log') }
'@ | Set-Content (Join-Path $mock 'test-launch.ps1')
    @'
param($Clients, $HarnessSec, $TimeoutSec)
$run = $env:ORICOOP_SWEEP_DIR; $next = [int]$env:ORICOOP_SWEEP_RESUME
if ($env:ORICOOP_NET_ONLY -ne 'SWEEP' -or $env:ORICOOP_FAIL_FAST -ne '0') { throw 'Missing sweep environment' }
if ([int]$TimeoutSec -le [int]$HarnessSec) { throw 'Missing timeout margin' }
if ($env:ORICOOP_SWEEP_SKIP -ne '^skip$') { throw 'Missing skip environment' }
foreach ($side in @('host','guest')) {
    if (-not (Test-Path (Join-Path $run ($side + '.catalog.csv')))) { throw 'Catalog not reused' }
    '1' | Set-Content (Join-Path $run ($side + '.catalog.csv.complete'))
}
"index,kind,scene,pedestal`n0,scene,a,-1`n1,save,a,0" | Set-Content (Join-Path $run 'host.plan.csv')
"RUN $([DateTime]::UtcNow.ToString('o'))" | Set-Content (Join-Path $PSScriptRoot 'test\net-launch.log')
foreach ($side in @('host','guest')) {
    $verdict = if ($next -eq 0) { 'RUNNING' } else { 'PASS' }
    $end = if ($next -eq 0) { '' } else { [DateTime]::UtcNow.ToString('o') }
    $kind = if ($next -eq 0) { 'scene' } else { 'save' }
    $csv = "index,side,kind,scene,verdict,fails,t0,t1,note`n$next,$side,$kind,a,$verdict,,2026-11-18T01:00:00Z,$end,mock"
    $csv | Set-Content (Join-Path $PSScriptRoot ('test\net-' + $side + '.txt.sweep.csv'))
    if ($next -eq 1) { 'done' | Set-Content (Join-Path $run ($side + '.done')) }
}
'@ | Set-Content (Join-Path $mock 'run-nettest.ps1')
    $loopRun = Join-Path $fixture 'loop'
    $output = & (Join-Path $PSHOME 'powershell.exe') -NoProfile -ExecutionPolicy Bypass -File (Join-Path $mock 'run-scenesweep.ps1') -Resume $loopRun -MaxRestarts 1 -Skip '^skip$' -Catalog $legacy 2>&1
    if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'SWEEP DONE pass=1 fail=1 inconclusive=0') { throw ($output -join "`n") }
    if (@(Get-ChildItem $loopRun -Directory -Filter 'attempt-*').Count -ne 2) { throw 'Expected one restart' }
    if ((Import-Csv (Join-Path $loopRun 'host.csv') | Select-Object -Last 1).index -ne '1') { throw 'Restart did not skip crashed entry' }
    Write-Host 'PASS runner restart cursor, skip environment, budgets, lock lifecycle and merged report (fake launchers only)'
    $cache = Join-Path $fixture 'scene-catalog'
    if (-not (Test-Path (Join-Path $cache 'catalog.key'))) { throw 'Complete catalogs not promoted' }
    $cachedRun = Join-Path $fixture 'cached'
    $output = & (Join-Path $PSHOME 'powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $runner -Resume $cachedRun -MaxRestarts 1 -Skip '^skip$' 2>&1
    if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'SWEEP catalog reuse:') { throw ('Automatic catalog reuse failed: ' + ($output -join "`n")) }
    'wrong-key' | Set-Content (Join-Path $legacy 'catalog.key')
    $ErrorActionPreference = 'Continue' # Expected native stderr from the rejected key.
    try { $output = & (Join-Path $PSHOME 'powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $runner -Resume (Join-Path $fixture 'bad-key') -Catalog $legacy 2>&1 }
    finally { $ErrorActionPreference = 'Stop' }
    if ($LASTEXITCODE -eq 0 -or ($output -join "`n") -notmatch 'key mismatch') { throw 'Mismatched explicit catalog was accepted' }
    Write-Host 'PASS explicit legacy catalog reuse, automatic cache key match, promotion and mismatch rejection'
    # A durable one-sided ABORT must stop the restart loop and mark the untouched tail not-run.
    '1,host,abort,a,ABORT,,now,now,SWEEP ABORT recoveries=3' | Add-Content (Join-Path $fixture 'host.csv')
    'SWEEP ABORT recoveries=3' | Set-Content (Join-Path $fixture 'host.abort.txt')
    $output = & (Join-Path $PSHOME 'powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $runner -Resume $fixture -MaxRestarts 1 2>&1
    if ($LASTEXITCODE -ne 0 -or (Test-Path (Join-Path $fixture 'attempt-*'))) { throw 'Aborted run restarted' }
    $csv = Get-ChildItem $fixture -Filter 'scene-sweep-*.csv' | Sort-Object LastWriteTimeUtc | Select-Object -Last 1
    $tail = Import-Csv $csv.FullName | Select-Object -Last 1
    if ($tail.verdict -ne 'INCONCLUSIVE' -or $tail.fails -ne 'not-run') { throw 'Abort tail not marked not-run' }
    Write-Host 'PASS runner abort stops restart and reports not-run tail'
} finally { Remove-Item -LiteralPath $fixture -Recurse -Force }
