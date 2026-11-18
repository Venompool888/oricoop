# Overnight two-client scene sweep. Launch ONLY through run-nettest/test-launch, never directly.
# Zero-based inclusive -From/-To; -Shard k/n selects original indices congruent to k.
# -Resume <run directory> continues cached discovery/plan and journals. -ReportOnly never launches anything.
param(
    [string]$Shard = '', [ValidateRange(0, 3600)][double]$Linger = 8, [string]$Scenes = '',
    [ValidateRange(0, 2147483647)][int]$From = 0, [ValidateRange(0, 2147483647)][int]$To = [int]::MaxValue,
    [ValidateRange(0, 2147483647)][int]$HarnessSec = 0, [ValidateRange(0, 2147483647)][int]$TimeoutSec = 0,
    [ValidateRange(0, 1000)][int]$MaxRestarts = 40, [string]$Resume = '', [switch]$ReportOnly,
    [string]$Skip = $env:ORICOOP_SWEEP_SKIP, [string]$Catalog = '',
    [switch]$Relay, [switch]$CServer
)
$ErrorActionPreference = 'Stop'
if ($Relay -and $CServer) { throw 'Choose either -Relay or -CServer.' }
if (-not ('OriCoop.Sweep.SweepPlan' -as [type])) {
    Add-Type -Path @((Join-Path $PSScriptRoot 'OriCoop\Sweep\SweepPlan.cs'), (Join-Path $PSScriptRoot 'OriCoop\Sweep\SweepReport.cs'))
}
# Validate even if discovery has not run yet.
$empty = New-Object 'System.Collections.Generic.List[OriCoop.Sweep.SweepScene]'
if ($null -eq $Skip -or $Skip -eq '') { $Skip = [OriCoop.Sweep.SweepPlan]::DefaultSkip }
[void][OriCoop.Sweep.SweepPlan]::Build($empty, [string]$From, [string]$To, $Shard, $Scenes, $Skip)
$root = 'C:\OriCoop\test-reports'
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$suffix = if ($Shard) { '-shard-' + $Shard.Replace('/', '-') } else { '' }
$run = if ($Resume) { [IO.Path]::GetFullPath($Resume) } else { Join-Path $root ('scene-sweep-' + $stamp + $suffix + '-run') }
if ($ReportOnly -and -not $Resume) { throw '-ReportOnly requires -Resume <run directory>.' }
New-Item -ItemType Directory -Force $run | Out-Null
$options = Join-Path $run 'options.json'
if (Test-Path -LiteralPath $options) {
    $saved = Get-Content -LiteralPath $options -Raw | ConvertFrom-Json
    # Resume must use the original selection; FROM is not repurposed as the restart cursor.
    $Shard = [string]$saved.Shard; $Scenes = [string]$saved.Scenes; $Linger = [double]$saved.Linger
    $From = [int]$saved.From; $To = [int]$saved.To
    if ($null -ne $saved.Skip) { $Skip = [string]$saved.Skip }
} else {
    @{ Shard = $Shard; Scenes = $Scenes; Skip = $Skip; Linger = $Linger; From = $From; To = $To } | ConvertTo-Json | Set-Content -LiteralPath $options -Encoding UTF8
}
function Read-SweepRecords([string]$path) {
    if (Test-Path -LiteralPath $path) {
        foreach ($line in [IO.File]::ReadAllLines($path)) {
            if (-not $line -or $line -eq [OriCoop.Sweep.SweepRecord]::Header) { continue }
            try { [OriCoop.Sweep.SweepRecord]::ParseCsv($line) } catch [FormatException] { Write-Warning "Ignoring torn CSV row in $path" }
        }
    }
}
function Get-Records {
    $list = New-Object 'System.Collections.Generic.List[OriCoop.Sweep.SweepRecord]'
    foreach ($side in @('host', 'guest')) { foreach ($r in @(Read-SweepRecords (Join-Path $run ($side + '.csv')))) { $list.Add($r) } }
    return ,$list
}
function Get-Plan {
    $plan = New-Object 'System.Collections.Generic.List[OriCoop.Sweep.SweepEntry]'
    $path = Join-Path $run 'host.plan.csv'
    if (-not (Test-Path -LiteralPath $path)) { $path = Join-Path $run 'guest.plan.csv' }
    if (Test-Path -LiteralPath $path) {
        foreach ($r in @(Import-Csv -LiteralPath $path)) {
            $e = New-Object OriCoop.Sweep.SweepEntry
            $e.Index = [int]$r.index; $e.Kind = $r.kind; $e.Scene = $r.scene; $e.Pedestal = [int]$r.pedestal; $plan.Add($e)
        }
    }
    return ,$plan
}
function Append-Record($r) {
    $path = Join-Path $run ($r.Side + '.csv')
    if (-not (Test-Path -LiteralPath $path)) { [IO.File]::WriteAllText($path, [OriCoop.Sweep.SweepRecord]::Header + "`r`n") }
    [IO.File]::AppendAllText($path, $r.ToCsv() + "`r`n")
}
function Close-Interrupted {
    foreach ($r in [OriCoop.Sweep.SweepReport]::Interrupted((Get-Records), [DateTime]::UtcNow.ToString('o'))) { Append-Record $r }
}

# The key deliberately excludes OriCoop.dll: rebuilding the sweep must not invalidate game discovery.
function Get-CatalogKey([string]$exe) {
    if (-not (Test-Path -LiteralPath $exe)) { return $null }
    $stream = [IO.File]::OpenRead($exe); $sha = [Security.Cryptography.SHA256]::Create()
    try { return 'scene-catalog-v1:' + $stream.Length + ':' + [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}
function Copy-Catalogs([string]$source, [string]$destination) {
    foreach ($side in @('host','guest')) {
        $name = $side + '.catalog.csv'
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $destination $name) -Force
    }
}
$cache = Join-Path $root 'scene-catalog'
$catalogKey = $null
if (-not $ReportOnly) {
    $catalogKey = Get-CatalogKey 'C:\OriCoop\game\oriDE.exe'
    $source = $Catalog
    if ($source -and (Test-Path -LiteralPath $source -PathType Leaf)) { $source = Split-Path -Parent ([IO.Path]::GetFullPath($source)) }
    if (-not $source -and $catalogKey -and (Test-Path -LiteralPath (Join-Path $cache 'catalog.key'))) {
        if ([IO.File]::ReadAllText((Join-Path $cache 'catalog.key')).Trim() -eq $catalogKey) { $source = $cache }
    }
    if ($source) {
        foreach ($side in @('host','guest')) {
            if (-not (Test-Path -LiteralPath (Join-Path $source ($side + '.catalog.csv')))) { throw "Catalog directory missing $side.catalog.csv: $source" }
        }
        $keyPath = Join-Path $source 'catalog.key'
        if ((Test-Path -LiteralPath $keyPath) -and [IO.File]::ReadAllText($keyPath).Trim() -ne $catalogKey) { throw 'Catalog game executable key mismatch.' }
        # Explicit legacy catalogs have no key. Cached rows are still checked against live metadata by the game;
        # any missing rows are discovered normally. Never overwrite a resumed run's append-only discovery.
        foreach ($side in @('host','guest')) {
            $name = $side + '.catalog.csv'
            if (-not (Test-Path -LiteralPath (Join-Path $run $name))) {
                Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $run $name)
            }
        }
        Write-Host "SWEEP catalog reuse: $source"
    }
    # Parent holds the copy through result collection; run-nettest's child reuses our unique session lock.
    . (Join-Path $PSScriptRoot 'test-launch.ps1')
    $keys = @('ORICOOP_NET_ONLY','ORICOOP_FAIL_FAST','ORICOOP_SWEEP_DIR','ORICOOP_SWEEP_SHARD','ORICOOP_SWEEP_LINGER',
        'ORICOOP_SWEEP_SCENES','ORICOOP_SWEEP_SKIP','ORICOOP_SWEEP_FROM','ORICOOP_SWEEP_TO','ORICOOP_SWEEP_RESUME','ORICOOP_TEST_DLL','ORICOOP_SESSION','ORICOOP_TEST_COPY')
    $before = @{}; foreach ($key in $keys) { $before[$key] = [Environment]::GetEnvironmentVariable($key) }
    try {
        $env:ORICOOP_NET_ONLY = 'SWEEP'; $env:ORICOOP_FAIL_FAST = '0'; $env:ORICOOP_SWEEP_DIR = $run
        $env:ORICOOP_SWEEP_SHARD = $Shard; $env:ORICOOP_SWEEP_SCENES = $Scenes; $env:ORICOOP_SWEEP_SKIP = $Skip
        $env:ORICOOP_SWEEP_LINGER = $Linger.ToString([Globalization.CultureInfo]::InvariantCulture)
        $env:ORICOOP_SWEEP_FROM = [string]$From; $env:ORICOOP_SWEEP_TO = [string]$To
        $env:ORICOOP_SESSION = 'T-070-' + $PID + '-' + $stamp
        if (-not $env:ORICOOP_TEST_DLL) { $env:ORICOOP_TEST_DLL = Join-Path $PSScriptRoot 'bin\OriCoop.dll' }
        $ownerMarker = Join-Path $run 'stopped-for-owner'
        if (Test-Path -LiteralPath $ownerMarker) { Remove-Item -LiteralPath $ownerMarker -Force } else { Close-Interrupted }
        for ($attempt = 0; $attempt -le $MaxRestarts; $attempt++) {
            $plan = Get-Plan; $records = Get-Records
            $next = [OriCoop.Sweep.SweepReport]::ResumeIndex($plan, $records)
            if ($plan.Count -gt 0 -and $next -gt $plan[$plan.Count - 1].Index) { break }
            foreach ($side in @('host','guest')) { $ab = Join-Path $run ($side + '.abort.txt'); if (Test-Path -LiteralPath $ab) { Write-Warning ('SWEEP ABORT (' + $side + '): restarting the game at entry ' + $next); Move-Item -LiteralPath $ab -Destination (Join-Path $run ($side + '.abort-' + $attempt + '.txt')) -Force } }
            if ((Test-Path (Join-Path $run 'host.fatal.txt')) -or (Test-Path (Join-Path $run 'guest.fatal.txt'))) { Write-Warning 'Plan mismatch: refusing to resume.'; break }
            $env:ORICOOP_SWEEP_RESUME = [string]$next
            # Discovery size is not known offline. 300 scenes were observed in the existing story-scan snapshot.
            # Once cached, compute the budget from the exact selected plan and remaining discovery rows.
            $entries = if ($plan.Count -gt 0) { @($plan | Where-Object { $_.Index -ge $next }).Count } else { [Math]::Min([long]600, [long]$To - $From + 1) }
            $discovery = if ($plan.Count -gt 0) { 0 } else { 300 * 85 }
            $hsec = if ($HarnessSec) { $HarnessSec } else { [int][Math]::Ceiling(300 + $discovery + $entries * ($Linger + 650)) }
            $tsec = if ($TimeoutSec) { $TimeoutSec } else { $hsec + 240 }
            $attemptDir = Join-Path $run ('attempt-' + $attempt + '-' + [DateTime]::UtcNow.ToString('HHmmssfff'))
            New-Item -ItemType Directory -Force $attemptDir | Out-Null
            Start-TestRun
            $env:ORICOOP_TEST_COPY = $TL_Copy
            # run-nettest only reuses a HAND-MADE lock (no pid= line) carrying our session name; a launcher lock would
            # make the child wait on its own parent. We refresh it below and delete it ourselves in finally.
            $heldLock = $TL_LockFile
            [IO.File]::WriteAllText($heldLock, "session=$env:ORICOOP_SESSION`r`nscript=run-scenesweep.ps1 (held for its run-nettest child)`r`ntime=$([DateTime]::UtcNow.ToString('o'))`r`ncopy=$TL_Copy`r`n")
            try {
                foreach ($side in @('host','guest')) {
                    Remove-Item -LiteralPath (Join-Path $TL_TestDir ('net-' + $side + '.txt.sweep.csv')) -Force -ErrorAction SilentlyContinue
                    Remove-Item -LiteralPath (Join-Path $run ($side + '.done')) -Force -ErrorAction SilentlyContinue
                }
                $args = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $PSScriptRoot 'run-nettest.ps1') + '" -Clients 2 -HarnessSec ' + $hsec + ' -TimeoutSec ' + $tsec
                if ($Relay) { $args += ' -Relay' }; if ($CServer) { $args += ' -CServer' }
                $child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList $args -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $attemptDir 'launcher.log') -RedirectStandardError (Join-Path $attemptDir 'launcher-error.log')
                $games = @{}; $oneExitedAt = $null
                try {
                    while (-not $child.WaitForExit(1000)) {
                        # Locks otherwise become stale at 90 min, even while the holder is alive.
                        if (Test-Path -LiteralPath $TL_LockFile) { (Get-Item -LiteralPath $TL_LockFile).LastWriteTimeUtc = [DateTime]::UtcNow }
                        $launchLog = Join-Path $TL_TestDir 'net-launch.log'
                        if (Test-Path -LiteralPath $launchLog) {
                            foreach ($line in @(Get-Content -LiteralPath $launchLog -ErrorAction SilentlyContinue)) {
                                if ($line -match '^LAUNCH (\S+) .* pid=(\d+)$') {
                                    $launched = [DateTime]::Parse($Matches[1]).ToUniversalTime(); $id = [int]$Matches[2]
                                    if ($launched -lt $child.StartTime.ToUniversalTime() -or $games.ContainsKey([string]$id)) { continue }
                                    # A process that already disappeared is still an observed exited client.
                                    $games[[string]$id] = $null
                                    try {
                                        $p = Get-Process -Id $id -ErrorAction Stop
                                        if ($p.StartTime.ToUniversalTime() -ge $child.StartTime.ToUniversalTime() -and [string]::Equals((Get-ProcPath $p), $TL_TestExe, [StringComparison]::OrdinalIgnoreCase)) {
                                            $null = $p.Handle; $games[[string]$id] = $p
                                        }
                                    } catch { }
                                }
                            }
                        }
                        # run-nettest normally waits for both. Stop its surviving PC when the other crashes,
                        # rather than burning the remaining overnight budget on unpaired barriers.
                        if ($games.Count -eq 2 -and @($games.Values | Where-Object { $null -eq $_ -or $_.HasExited }).Count -eq 1) {
                            if (-not $oneExitedAt) { $oneExitedAt = [DateTime]::UtcNow }
                            if (([DateTime]::UtcNow - $oneExitedAt).TotalSeconds -gt 10) {
                                foreach ($p in $games.Values) { if ($null -ne $p -and -not $p.HasExited) { $p.Kill(); $p.WaitForExit() } }
                            }
                        }
                    }
                } finally {
                    foreach ($p in $games.Values) { if ($null -ne $p -and -not $p.HasExited) { $p.Kill(); $p.WaitForExit() } }
                    if (-not $child.HasExited) { $child.WaitForExit(30000) | Out-Null }
                    if (-not $child.HasExited) { $child.Kill(); $child.WaitForExit() }
                }
                foreach ($side in @('host','guest')) {
                    $result = Join-Path $TL_TestDir ('net-' + $side + '.txt')
                    foreach ($r in @(Read-SweepRecords ($result + '.sweep.csv'))) { Append-Record $r }
                    foreach ($file in @($result, ($result + '.sweep.csv'), (Get-ClientLog ($side + '-1')), (Join-Path $TL_TestDir ($side + '-1-unity.log')))) {
                        if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination $attemptDir -Force }
                    }
                }
                Copy-Item -LiteralPath (Join-Path $TL_TestDir 'net-launch.log') -Destination $attemptDir -Force -ErrorAction SilentlyContinue
                # Owner/locks stop (exit 2/3/4): the launcher closed both games on purpose, so the open entry is no crash;
                # leave it unfinalized (INCONCLUSIVE, rerun on resume) instead of Close-Interrupted's FAIL crash.
                if ($child.ExitCode -in @(2,3,4)) { $script:StoppedForOwner = $true; Set-Content -LiteralPath $ownerMarker -Value $child.ExitCode; Write-Warning ('Launcher stopped for owner/locks (exit ' + $child.ExitCode + '); resume later.'); break }
                Close-Interrupted
                $aborted = (Test-Path (Join-Path $run 'host.abort.txt')) -or (Test-Path (Join-Path $run 'guest.abort.txt'))
                if (-not $aborted -and (Test-Path (Join-Path $run 'host.done')) -and (Test-Path (Join-Path $run 'guest.done'))) { break }
            } finally {
                Complete-TestRun; Exit-TestRun
                if ($heldLock -and (Test-Path -LiteralPath $heldLock) -and ((Get-Content -LiteralPath $heldLock -Raw -ErrorAction SilentlyContinue) -match [regex]::Escape('session=' + $env:ORICOOP_SESSION))) {
                    Remove-Item -LiteralPath $heldLock -Force -ErrorAction SilentlyContinue
                }
            }
        }
    } finally { foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key, $before[$key]) } }
}
# Promote only a complete pair, including when discovery finished but a later entry failed.
if (-not $ReportOnly -and $catalogKey) {
    $complete = $true
    foreach ($side in @('host','guest')) {
        $path = Join-Path $run ($side + '.catalog.csv')
        $marker = $path + '.complete'
        if (-not (Test-Path -LiteralPath $marker)) { $complete = $false; continue }
        $expected = 0
        if (-not [int]::TryParse([IO.File]::ReadAllText($marker).Trim(), [ref]$expected) -or $expected -le 0) { $complete = $false; continue }
        $rows = @(Read-SweepRecords $path)
        if ($rows.Count -ne $expected -or @($rows | Select-Object -ExpandProperty Index -Unique).Count -ne $expected) { $complete = $false }
    }
    if ($complete) {
        New-Item -ItemType Directory -Force $cache | Out-Null
        # Invalidate the old key before replacing the pair, so an interrupted promotion cannot be reused.
        Remove-Item -LiteralPath (Join-Path $cache 'catalog.key') -Force -ErrorAction SilentlyContinue
        Copy-Catalogs $run $cache
        [IO.File]::WriteAllText((Join-Path $cache 'catalog.key'), $catalogKey)
        Write-Host "SWEEP catalog saved: $cache"
    }
}
if (-not $script:StoppedForOwner) { Close-Interrupted }
$plan = Get-Plan
if ($plan.Count -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $run 'host.plan.csv'))) {
    Write-Warning ('Discovery not complete; plan size unknown. Resume with -Resume "' + $run + '"')
}
$merged = [OriCoop.Sweep.SweepReport]::Merge($plan, (Get-Records))
# Timestamp includes milliseconds to keep parallel shards and repeated runs separate.
$report = Join-Path $root ('scene-sweep-' + $stamp + $(if ($Shard) { '-shard-' + $Shard.Replace('/', '-') } else { '' }))
New-Item -ItemType Directory -Force $root | Out-Null
[IO.File]::WriteAllText(($report + '.csv'), [OriCoop.Sweep.SweepReport]::Csv($merged))
$evidence = "`nRun artifacts: $run`nPlan hash: $([OriCoop.Sweep.SweepPlan]::Hash($plan))`n"
foreach ($side in @('host','guest')) {
    $fatal = Join-Path $run ($side + '.fatal.txt')
    if (Test-Path -LiteralPath $fatal) { $evidence += "`nFAIL ${side}: $([IO.File]::ReadAllText($fatal))`n" }
    foreach ($abort in @(Get-ChildItem -LiteralPath $run -Filter ($side + '.abort*.txt') -ErrorAction SilentlyContinue)) { $evidence += "`n$([IO.File]::ReadAllText($abort.FullName)) (${side}, restarted)`n" }
}
[IO.File]::WriteAllText(($report + '.md'), [OriCoop.Sweep.SweepReport]::Markdown($merged) + $evidence)
$pass = @($merged | Where-Object { $_.Verdict -eq 'PASS' }).Count
$fail = @($merged | Where-Object { $_.Verdict -eq 'FAIL' }).Count
$inconclusive = @($merged | Where-Object { $_.Verdict -eq 'INCONCLUSIVE' }).Count
Write-Host ('Report: ' + $report + '.md; resume: ' + $run)
Write-Host "SWEEP DONE pass=$pass fail=$fail inconclusive=$inconclusive"
