# Co-op save lineage test (CoopSaves.cs): two isolated test copies (appdata = host, appdata2 = guest), five short
# online sessions in a row, each only running the lineage handshake (ORICOOP_COOPSAVE_TEST=1):
#   1 new              fresh pair: guest keeps a tagged copy of the host's slot in its first empty slot
#   2 same             unchanged since session 1: plays without asking
#   3 diverged-host    guest copy changed alone (+5 h play time): host is asked, picks host progress
#   4 diverged-partner same divergence, host picks partner progress (host loads it, both files = partner copy)
#   5 cancel           same divergence, host cancels: online play ends, neither file changes
# The owner's real saves are never touched (LOCALAPPDATA points at C:\OriCoop\appdata*). Never closes a running game.
param([int]$TimeoutSec = 150, [switch]$Force, [string]$Only = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'test-launch.ps1')   # quiet launch + owner watchdog (docs\testing-rules.md)
Start-TestRun -Force:$Force
$pristine = 'C:\OriCoop\save-backup\20261114-150138'
$hostSaves = Join-Path $TL_AppData[0] 'Ori and the Blind Forest DE'
$guestSaves = Join-Path $TL_AppData[1] 'Ori and the Blind Forest DE'
$testDir = $TL_TestDir
New-Item -ItemType Directory -Force $testDir | Out-Null
$cfg = Join-Path $TL_GameDir 'BepInEx\config\local.oricoop.cfg'
$cfgBefore = if (Test-Path $cfg) { [IO.File]::ReadAllBytes($cfg) } else { $null }

function Reset-Saves([string]$dir) {
    Get-ChildItem $dir -File | Where-Object { $_.Name -like 'saveFile*' -or $_.Name -like '*.oricoop*' -or $_.Name -eq 'oricoop-player.txt' } | Remove-Item -Force
    if (Test-Path "$dir\OriCoop-backups") { Remove-Item "$dir\OriCoop-backups" -Recurse -Force }
    Copy-Item "$pristine\*" $dir -Force
}
function Sha1([string]$path) {
    if (-not (Test-Path $path)) { return '' }
    $sha = [Security.Cryptography.SHA1]::Create()
    try { return (($sha.ComputeHash([IO.File]::ReadAllBytes($path)) | ForEach-Object { $_.ToString('x2') }) -join '') } finally { $sha.Dispose() }
}
function Read-Link([string]$path) {
    $h = @{}
    if (Test-Path $path) { foreach ($l in [IO.File]::ReadAllLines($path)) { $i = $l.IndexOf('='); if ($i -gt 0 -and -not $l.StartsWith('#')) { $h[$l.Substring(0, $i)] = $l.Substring($i + 1) } } }
    $h
}
# Simulate the guest playing its co-op copy alone: +5 hours in the SaveSlotInfo header (same playthrough id).
function Diverge([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    $areaLen = $b[12]
    if ($areaLen -ge 128) { throw 'area name too long for this simple parser' }
    $off = 12 + 1 + $areaLen + 20
    $hours = [BitConverter]::ToInt32($b, $off) + 5
    [BitConverter]::GetBytes([int]$hours).CopyTo($b, $off)
    [IO.File]::WriteAllBytes($path, $b)
}
function Client-Spec([string]$role, [string]$appData, [string]$out, [hashtable]$envs) {
    $e = @{}
    $e['LOCALAPPDATA'] = $appData; $e['ORICOOP_TEST'] = '1'; $e['ORICOOP_SLOT'] = '1'; $e['ORICOOP_TEST_OUT'] = $out
    $e['ORICOOP_NET'] = $role; $e['ORICOOP_NET_ADDR'] = '127.0.0.1'; $e['ORICOOP_NET_PORT'] = [string](47013 + $TL_PortOffset); $e['ORICOOP_NET_SIM'] = ''
    $e['ORICOOP_COOPSAVE_TEST'] = '1'
    $e['ORICOOP_PLAYER_NAME'] = $(if ($role -eq 'host') { 'HostPC' } else { 'GuestPC' })
    foreach ($k in $envs.Keys) { $e[$k] = $envs[$k] }
    @{ Tag = ('coopsave-' + $role); Arguments = '-screen-fullscreen 0 -screen-width 640 -screen-height 360'; Vars = $e }
}
function Run-Pair([string]$name, [hashtable]$envs) {
    "=== $name ==="
    $hostOut = Join-Path $testDir "coopsave-$name-host.txt"; $guestOut = Join-Path $testDir "coopsave-$name-guest.txt"
    Remove-Item $hostOut, $guestOut -Force -ErrorAction SilentlyContinue
    $clients = @()
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        # Both games start back to back (Unity refuses a copy started after another finished starting).
        $clients = @(Start-TestClients @((Client-Spec 'host' $TL_AppData[0] $hostOut $envs), (Client-Spec 'join' $TL_AppData[1] $guestOut $envs)))
        [void](Wait-TestGames $clients $TimeoutSec $watch)
    } finally {
        foreach ($c in $clients) { if (-not $c.HasExited) { $c.Kill(); $c.WaitForExit(); "TIMEOUT: killed game $($c.Id)" }; $c.Dispose() }
        foreach ($role in 'host', 'join') {
            $src = Join-Path $TL_RuntimeDir "coopsave-$role\BepInEx\LogOutput.log"
            if (Test-Path $src) { Copy-Item $src (Join-Path $testDir "coopsave-$name-$role-LogOutput.log") -Force }
        }
    }
    $ok = $true
    foreach ($o in @($hostOut, $guestOut)) {
        if (Test-Path $o) {
            $lines = @(Get-Content $o)
            $lines | Where-Object { $_ -match 'PASS |FAIL |RESULT|coop save' }
            if (-not ($lines -match 'RESULT PASS') -or ($lines -match 'RESULT FAIL')) { $ok = $false }
        } else { "no result file $o"; $ok = $false }
    }
    $ok
}
$results = [ordered]@{}
function Disk([string]$name, [bool]$cond, [string]$what) {
    "  DISK " + $(if ($cond) { 'PASS ' } else { 'FAIL ' }) + $what
    if (-not $cond) { $script:results[$name] = $false }
}

try {
    Reset-Saves $hostSaves; Reset-Saves $guestSaves
    $guestOwn = Sha1 "$guestSaves\saveFile1.sav"

    $r = Run-Pair 'new' @{ ORICOOP_COOPSAVE_EXPECT = 'new' }; $results['new'] = $r[-1]; $r[0..($r.Count - 2)]
    $hl = Read-Link "$hostSaves\saveFile1.oricoop"; $gl = Read-Link "$guestSaves\saveFile2.oricoop"
    Disk 'new' ($hl['coop'] -and $hl['coop'] -eq $gl['coop']) "host slot 1 and guest slot 2 share co-op id $($hl['coop'])"
    Disk 'new' ((Sha1 "$hostSaves\saveFile1.sav") -eq (Sha1 "$guestSaves\saveFile2.sav")) 'guest slot 2 = host slot 1'
    Disk 'new' ($gl['partner'] -eq 'HostPC' -and $hl['partner'] -eq 'GuestPC') "partner names recorded ($($hl['partner']) / $($gl['partner']))"
    Disk 'new' ((Sha1 "$guestSaves\saveFile1.sav") -eq $guestOwn) "guest's own slot 1 untouched"

    if (-not $Only) {
    $r = Run-Pair 'same' @{ ORICOOP_COOPSAVE_EXPECT = 'same' }; $results['same'] = $r[-1]; $r[0..($r.Count - 2)]

    Diverge "$guestSaves\saveFile2.sav"; $solo = Sha1 "$guestSaves\saveFile2.sav"
    $r = Run-Pair 'diverged-host' @{ ORICOOP_COOPSAVE_EXPECT = 'diverged-host'; ORICOOP_COOPSAVE_CHOICE = 'host' }; $results['diverged-host'] = $r[-1]; $r[0..($r.Count - 2)]
    Disk 'diverged-host' ((Sha1 "$hostSaves\saveFile1.sav") -eq (Sha1 "$guestSaves\saveFile2.sav")) 'guest slot 2 = host progress again'
    $bk = @(Get-ChildItem "$guestSaves\OriCoop-backups" -Filter 'saveFile2-*.sav' -ErrorAction SilentlyContinue | Where-Object { (Sha1 $_.FullName) -eq $solo })
    Disk 'diverged-host' ($bk.Count -ge 1) "guest's solo copy kept in OriCoop-backups"
    }

    Diverge "$guestSaves\saveFile2.sav"; $solo = Sha1 "$guestSaves\saveFile2.sav"; $hostBefore = Sha1 "$hostSaves\saveFile1.sav"
    $r = Run-Pair 'diverged-partner' @{ ORICOOP_COOPSAVE_EXPECT = 'diverged-partner'; ORICOOP_COOPSAVE_CHOICE = 'partner'; ORICOOP_COOPSAVE_PARTNER_HASH = $solo }; $results['diverged-partner'] = $r[-1]; $r[0..($r.Count - 2)]
    Disk 'diverged-partner' ((Sha1 "$hostSaves\saveFile1.sav") -eq $solo -and (Sha1 "$guestSaves\saveFile2.sav") -eq $solo) 'both slots hold the partner progress'
    $bk = @(Get-ChildItem "$hostSaves\OriCoop-backups" -Filter 'saveFile1-*.sav' -ErrorAction SilentlyContinue | Where-Object { (Sha1 $_.FullName) -eq $hostBefore })
    Disk 'diverged-partner' ($bk.Count -ge 1) "host's previous slot kept in OriCoop-backups"

    Diverge "$guestSaves\saveFile2.sav"; $g = Sha1 "$guestSaves\saveFile2.sav"; $h = Sha1 "$hostSaves\saveFile1.sav"
    $r = Run-Pair 'cancel' @{ ORICOOP_COOPSAVE_EXPECT = 'cancel'; ORICOOP_COOPSAVE_CHOICE = 'cancel' }; $results['cancel'] = $r[-1]; $r[0..($r.Count - 2)]
    Disk 'cancel' ((Sha1 "$hostSaves\saveFile1.sav") -eq $h -and (Sha1 "$guestSaves\saveFile2.sav") -eq $g) 'cancel left both slots unchanged'
} finally {
    Stop-TestGames
    if ($cfgBefore -ne $null) { [IO.File]::WriteAllBytes($cfg, $cfgBefore) }
    # Leave the test copies as the other scripts expect them.
    Reset-Saves $hostSaves; Reset-Saves $guestSaves
    Complete-TestRun
}
'--- summary ---'
$all = $true
foreach ($k in $results.Keys) { "$k : " + $(if ($results[$k]) { 'PASS' } else { 'FAIL' }); if (-not $results[$k]) { $all = $false } }
if ($all -and $results.Count -eq 5) { 'COOPSAVE PASS' } else { 'COOPSAVE FAIL' }
