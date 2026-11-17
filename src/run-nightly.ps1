# Nightly full regression (layer 3 of docs\testing-rules.md): every game suite in normal windowed mode, fail-fast OFF
# (full diagnostics), DLL built from a clean worktree of HEAD, suites spread over the test copies by testcopy*.lock.
# Exits at once (code 0, "skipped") if the owner's game is running; a suite that sees the owner start playing stops
# itself (exit 3) and is reported as STOPPED. One summary file: C:\OriCoop\logs\nightly\<yyyyMMdd-HHmm>.txt.
# Do not schedule it on a PC where someone plays: it starts real game clients.
param([int]$Parallel = 5, [switch]$NoMutations)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$root = Split-Path -Parent $src
. (Join-Path $src 'test-launch.ps1')
$owner = @(Get-OwnerGames)
if ($owner.Count -gt 0) { 'NIGHTLY skipped: the owner is playing (' + (Format-Games $owner) + ')'; exit 0 }
$dir = Join-Path $root 'logs\nightly'
New-Item -ItemType Directory -Force $dir | Out-Null
$summary = Join-Path $dir ([DateTime]::Now.ToString('yyyyMMdd-HHmm') + '.txt')
$sha = (git -C $root rev-parse --short HEAD).Trim()
$env:ORICOOP_SESSION = 'nightly ' + $sha
$env:ORICOOP_FAIL_FAST = '0'
$env:ORICOOP_TIMESCALE = ''
$env:ORICOOP_TEST_COPY = ''
$env:ORICOOP_TEST_DLL = (& (Join-Path $src 'build-commit.ps1') $sha | Select-Object -Last 1)
& (Join-Path $src 'run-affected.ps1') -All -Parallel $Parallel -Summary $summary
$code = $LASTEXITCODE

# Mutation checks: every test-only "break the fix" switch must make ITS OWN check fail (Expect: a regex on the failing
# check's name). Fail-fast is OFF here: with it on, any baseline failure earlier in the suite (e.g. E3) stopped the run
# before the mutated check and was counted as "caught". Verdicts: caught (an Expect check failed), MISSED (it passed),
# BLOCKED (the suite died or never reached it; other failures listed), ERROR (the script failed, see <log>.err).
# A MISSED mutation makes the nightly fail. ORICOOP_NET_ONLY (P3 = CUT, WO) skips the earlier net phases.
$Mutations = @(
    @{ Name = 'MUTATE_NO_LIFECYCLE';    Env = @{ ORICOOP_MUTATE_NO_LIFECYCLE = '1' };    Script = @('run-test.ps1', '-Scenarios', 'title');    Expect = '^title: ' }
    @{ Name = 'MUTATE_KEEP_NET';        Env = @{ ORICOOP_MUTATE_KEEP_NET = '1' };        Script = @('run-test.ps1', '-Scenarios', 'title');    Expect = '^title: ' }
    @{ Name = 'MUTATE_NO_PLATFORMFIX';  Env = @{ ORICOOP_MUTATE_NO_PLATFORMFIX = '1' };  Script = @('run-test.ps1', '-Scenarios', 'platform'); Expect = '^platform: ' }
    @{ Name = 'MUTATE_SIGNPOST';        Env = @{ ORICOOP_MUTATE_SIGNPOST = '1'; ORICOOP_FORCE_ZH = '1' }; Script = @('run-test.ps1', '-Scenarios', 'signpost'); Expect = '^signpost: ' }
    @{ Name = 'MUTATE_KEEP_MODAL';      Env = @{ ORICOOP_MUTATE_KEEP_MODAL = '1'; ORICOOP_TITLEJOIN_LEAVE = 'drop' }; Script = @('run-titlejoin.ps1'); Expect = '回到标题|disconnect|dialog' }
    @{ Name = 'TEST_THROW';             Env = @{ ORICOOP_TEST_THROW = 'mutation' };      Script = @('run-test.ps1', '-Scenarios', 'move');     Expect = '^move: |TestThrow' }
    @{ Name = 'P3_OFF (CUT)';           Env = @{ ORICOOP_P3_OFF = '1'; ORICOOP_NET_ONLY = 'P3' }; Script = @('run-nettest.ps1');               Expect = '^CUT ' }
    @{ Name = 'S4_NO_HOLD (S4 skew)';   Env = @{ ORICOOP_S4_NO_HOLD = '1'; ORICOOP_S4_SKEW = '0.3' }; Script = @('run-nettest.ps1');          Expect = '^S4 ' }
    @{ Name = 'PHYS_OFF (P)';           Env = @{ ORICOOP_PHYS_OFF = '1' };               Script = @('run-nettest.ps1');                        Expect = '^P\w* ' }
    @{ Name = 'WORLDOBJ_OFF (WO)';      Env = @{ ORICOOP_WORLDOBJ_OFF = '1'; ORICOOP_NET_ONLY = 'WO' }; Script = @('run-nettest.ps1');        Expect = '^WO' }
)
if (-not $NoMutations) {
    $mdir = Join-Path $env:TEMP ('oricoop-mutations-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force $mdir | Out-Null
    $queue = New-Object System.Collections.Queue; foreach ($m in $Mutations) { $queue.Enqueue($m) }
    $running = @(); $mres = @()
    while ($queue.Count -gt 0 -or $running.Count -gt 0) {
        while ($queue.Count -gt 0 -and $running.Count -lt $Parallel) {
            $m = $queue.Dequeue(); $log = Join-Path $mdir (($m.Name -replace '[^\w]+', '_') + '.txt')
            $saved = @{}; foreach ($k in $m.Env.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $m.Env[$k]) }
            $env:ORICOOP_SESSION = 'nightly ' + $sha + ' ' + $m.Name
            $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $src $m.Script[0])) + @($m.Script | Select-Object -Skip 1)
            $p = Start-Process powershell -ArgumentList $args -PassThru -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError ($log + '.err')
            foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
            $running += [pscustomobject]@{ M = $m; P = $p; Log = $log }
            'MUTATION START ' + $m.Name
            Start-Sleep -Seconds 5
        }
        Start-Sleep -Seconds 5
        foreach ($r in @($running)) {
            if (-not $r.P.HasExited) { continue }
            $text = $(if (Test-Path $r.Log) { Get-Content $r.Log -Raw } else { '' })
            $err = $(if (Test-Path ($r.Log + '.err')) { (Get-Content ($r.Log + '.err') -Raw) } else { '' })
            $fails = @([regex]::Matches($text, '(?m)(?:\]|log:) FAIL (.*)$') | ForEach-Object { $_.Groups[1].Value.Trim() } | Select-Object -Unique)
            $hit = @($fails | Where-Object { $_ -match $r.M.Expect })
            $other = @($fails | Where-Object { $_ -notmatch $r.M.Expect })
            $complete = $text -match '(?m)(\] RESULT |NETTEST (PASS|FAIL)|TITLEJOIN \w+ (PASS|FAIL))'
            $status = $(if ($text -cmatch 'OWNER IS PLAYING') { 'STOPPED (owner playing)' } elseif ($hit.Count -gt 0) { 'caught' }
                elseif ($err -match '\S' -and -not $complete) { 'ERROR' } elseif (-not $complete -or $text -match 'no result file') { 'BLOCKED' } else { 'MISSED' })
            $detail = $(if ($hit.Count -gt 0) { $hit[0] } elseif ($status -eq 'ERROR') { ($err -split "`n")[0].Trim() } elseif ($other.Count -gt 0) { 'other failures: ' + (($other | Select-Object -First 2) -join ' | ') } else { '' })
            if ($detail.Length -gt 160) { $detail = $detail.Substring(0, 160) + '...' }
            $line = $r.M.Name + ': ' + $status + $(if ($detail) { ' (' + $detail + ')' } else { '' })
            $mres += $line; 'MUTATION ' + $line
            $running = @($running | Where-Object { $_ -ne $r })
        }
    }
    $env:ORICOOP_FAIL_FAST = '0'
    if (Test-Path $summary) { Add-Content -LiteralPath $summary -Value (@('--- mutation checks (each must FAIL), logs ' + $mdir) + $mres) }
    if (@($mres | Where-Object { $_ -match ': MISSED' }).Count -gt 0 -and $code -eq 0) { $code = 1 }
}
if (Test-Path $summary) { Add-Content -LiteralPath $summary -Value ('commit ' + $sha + ', exit ' + $code) }
'NIGHTLY summary: ' + $summary
exit $code
