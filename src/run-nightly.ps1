# Nightly full regression (layer 3 of docs\testing-rules.md): every game suite in normal windowed mode, fail-fast OFF
# (full diagnostics), DLL built from a clean worktree of HEAD, suites spread over the test copies by testcopy*.lock.
# Exits at once (code 0, "skipped") if the owner's game is running; a suite that sees the owner start playing stops
# itself (exit 3) and is reported as STOPPED. One summary file: C:\OriCoop\logs\nightly\<yyyyMMdd-HHmm>.txt.
# Do not schedule it on a PC where someone plays: it starts real game clients.
param([int]$Parallel = 3)
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
if (Test-Path $summary) { Add-Content -LiteralPath $summary -Value ('commit ' + $sha + ', exit ' + $code) }
'NIGHTLY summary: ' + $summary
exit $code
