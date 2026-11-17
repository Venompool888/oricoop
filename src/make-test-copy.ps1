# Makes an extra test copy (b -> C:\OriCoop\game-b, c -> game-c, d -> game-d, e -> game-e) so several sessions can run game tests in parallel.
# Large read-only game data is shared with C:\OriCoop\game: files are HARD LINKS, oriDE_Data subfolders JUNCTIONS
# (never write into them). Each copy has its own oriDE.exe path (Unity's single-instance mutex is named after the exe
# path, so copies never block each other or the owner's D: game), its own BepInEx (core/config/patchers/plugins are
# real copies: install your own OriCoop.dll there), its own saves (appdata-b, appdata2-b, appdata3-b), test output
# (test-b), runtime dir (test-runtime-b) and UDP ports (+100 for b, +200 for c, +300 for d, +400 for e; its config has Port = 47011+offset).
#   -Refresh  re-copy BepInEx (core, config, patchers, plugins) from C:\OriCoop\game; takes the copy's lock first.
# Never touches C:\OriCoop\game itself or the owner's D: copy.
param([Parameter(Mandatory = $true)][ValidateSet('b', 'c', 'd', 'e')][string]$Name, [switch]$Refresh)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'test-launch.ps1')
$src = 'C:\OriCoop\game'
$copy = Get-TestCopies | Where-Object { $_.Name -eq $Name }
$dst = $copy.GameDir

function Link-File([string]$from, [string]$to) {
    if (Test-Path -LiteralPath $to) { return }
    New-Item -ItemType HardLink -Path $to -Target $from | Out-Null
}
function Link-Dir([string]$from, [string]$to) {
    if (Test-Path -LiteralPath $to) { return }
    New-Item -ItemType Junction -Path $to -Target $from | Out-Null
}
function Copy-BepInEx {
    foreach ($d in 'core', 'config', 'patchers', 'plugins') {
        $from = Join-Path $src "BepInEx\$d"; $to = Join-Path $dst "BepInEx\$d"
        if (-not (Test-Path -LiteralPath $from)) { continue }
        New-Item -ItemType Directory -Force -Path $to | Out-Null
        & robocopy $from $to /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy $from failed ($LASTEXITCODE)" }
    }
    # Own UDP port so copies never collide (direct tests that use the config port, e.g. run-titlejoin).
    $cfg = Join-Path $dst 'BepInEx\config\local.oricoop.cfg'
    if (Test-Path -LiteralPath $cfg) {
        $port = 47011 + $copy.PortOffset
        $lines = [IO.File]::ReadAllLines($cfg); $inNet = $false
        for ($i = 0; $i -lt $lines.Length; $i++) {
            if ($lines[$i] -match '^\[(.+)\]') { $inNet = ($Matches[1] -eq 'Net') }
            elseif ($inNet -and $lines[$i] -match '^Port\s*=') { $lines[$i] = "Port = $port" }
        }
        [IO.File]::WriteAllLines($cfg, $lines)
    }
}

if ((Test-Path -LiteralPath $copy.Exe) -and -not $Refresh) { "copy $Name already exists: $dst (use -Refresh to re-copy BepInEx)"; return }

if ($Refresh) {
    # Take the copy's lock so no run is using it while its plugins change.
    if (-not (Enter-CopyLock $copy 'make-test-copy.ps1')) { "copy $Name is locked: " + ((Read-LockInfo $copy.Lock).GetEnumerator() | ForEach-Object { $_.Key + '=' + $_.Value }) -join ' '; exit 2 }
    $script:TL_LockFile = $copy.Lock
    try { Copy-BepInEx; "copy $Name refreshed from $src" } finally { Exit-CopyLock }
    return
}

New-Item -ItemType Directory -Force -Path $dst, (Join-Path $dst 'oriDE_Data'), (Join-Path $dst 'oriDE_Data\output'), (Join-Path $dst 'BepInEx') | Out-Null
# Root: binaries hard-linked (oriDE.exe is a hard link, but its PATH differs, which is what Unity's mutex uses);
# small text files copied.
foreach ($f in Get-ChildItem -LiteralPath $src -File) {
    $to = Join-Path $dst $f.Name
    if ($f.Extension -in '.exe', '.dll') { Link-File $f.FullName $to }
    elseif ($f.Name -notlike 'preloader_*.log' -and -not (Test-Path -LiteralPath $to)) { Copy-Item -LiteralPath $f.FullName $to }
}
foreach ($f in Get-ChildItem -LiteralPath (Join-Path $src 'oriDE_Data') -File) { Link-File $f.FullName (Join-Path $dst "oriDE_Data\$($f.Name)") }
foreach ($d in Get-ChildItem -LiteralPath (Join-Path $src 'oriDE_Data') -Directory) {
    if ($d.Name -eq 'output') { continue }
    Link-Dir $d.FullName (Join-Path $dst "oriDE_Data\$($d.Name)")
}
Copy-BepInEx
# Saves and settings of the three test players, from copy a's (scripts reset the slots from save-backup every run).
for ($i = 0; $i -lt 3; $i++) {
    $from = "C:\OriCoop\" + @('appdata', 'appdata2', 'appdata3')[$i]
    $to = $copy.AppData[$i]
    if ((Test-Path -LiteralPath $from) -and -not (Test-Path -LiteralPath $to)) {
        & robocopy $from $to /E /XD OriCoop-backups /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy $from failed ($LASTEXITCODE)" }
    }
}
New-Item -ItemType Directory -Force -Path $copy.TestDir, $copy.LogsDir | Out-Null
"copy $Name ready: $dst (lock $($copy.Lock), ports +$($copy.PortOffset))"
