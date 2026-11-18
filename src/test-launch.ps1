# Shared launcher for every script that starts the TEST COPY of the game (C:\OriCoop\game\oriDE.exe).
# Dot-source it:   . (Join-Path $PSScriptRoot 'test-launch.ps1')
# Rules (docs\testing-rules.md):
#  * never disturb the owner: windows open without activation, are pushed to the bottom of the z-order, never TOPMOST,
#    and are tiled in the bottom-right corner of the primary screen; a focus steal is undone at once;
#  * Normal CPU priority (BelowNormal starved net tests: 2.5 s localhost RTT; the owner watchdog stops every test when the owner plays, so tests never compete with the game; $env:ORICOOP_TEST_PRIORITY=BelowNormal to lower it); muted (ORICOOP_QUIET=1 -> OriCoop.TestQuiet.dll sets AudioListener.volume = 0);
#  * the test copy does NOT register with Steam (ORICOOP_NO_STEAM=1 skips Ori's Steamworks.Awake), so Steam never
#    marks "Ori running" because of a test and the owner's Play button keeps working. Pass -NeedsSteam only for a
#    test that really needs the Steam client, and never while the owner may play;
#  * the owner's game is told apart by PATH: anything that is not C:\OriCoop\game\oriDE.exe is the owner's
#    (D:\SteamLibrary\steamapps\common\Ori DE\OriDE.exe). Start refuses while any oriDE.exe runs. During the run the
#    watchdog checks every 2 s: if an owner game appears, only the PIDs this run started are killed, the run is
#    cleaned up and the script exits with code 3 ("owner is playing, test stopped, rerun later");
#  * nothing is ever killed by name; Unity's display prefs in HKCU (shared with the owner's copy) are restored.
# Simulation hook for checking the watchdog: set $env:ORICOOP_FAKE_OWNER_FILE to a path; while that file exists the
# watchdog behaves as if the owner's game were running.
#
# Test copies and locks: copy a = C:\OriCoop\game (lock C:\OriCoop\testcopy.lock), b = game-b (testcopy-b.lock),
# c = game-c (testcopy-c.lock); b/c are made by src\make-test-copy.ps1. Start-TestRun takes the first free copy
# (or the one named by $env:ORICOOP_TEST_COPY = a|b|c, or -Copy), WAITS while all are locked (prints the holder;
# a lock older than 90 min or whose PowerShell has exited is stale), and fills, for the calling script:
#   $TL_Copy, $TL_GameDir, $TL_TestExe, $TL_TestDir, $TL_AppData (4 LOCALAPPDATA dirs), $TL_RuntimeDir, $TL_LogsDir,
#   $TL_PortOffset (0 / 100 / 200: add it to every UDP port; the copy's own config already has Port = 47011+offset).
# $env:ORICOOP_SESSION names the holder in the lock; $env:ORICOOP_TEST_DLL = path of an OriCoop.dll to install
# into the locked copy's plugins before the run (so each session tests its own build).

$ErrorActionPreference = 'Stop'
$script:TL_Root = 'C:\OriCoop'
$script:TL_SrcDir = $PSScriptRoot   # this checkout's src (a worktree's own, for its TestQuiet build)
$script:TL_Copy = $null
$script:TL_GameDir = 'C:\OriCoop\game'
$script:TL_TestExe = 'C:\OriCoop\game\oriDE.exe'
$script:TL_TestDir = 'C:\OriCoop\test'
$script:TL_AppData = @('C:\OriCoop\appdata', 'C:\OriCoop\appdata2', 'C:\OriCoop\appdata3', 'C:\OriCoop\appdata4')
$script:TL_RuntimeDir = 'C:\OriCoop\test-runtime'
$script:TL_LogsDir = 'C:\OriCoop\logs'
$script:TL_PortOffset = 0
# Relay server binary for -Relay tests; ORICOOP_RELAY_EXE picks another build (e.g. src\RelayServer\RelayServer-v2.exe).
$script:TL_RelayExe = $(if ($env:ORICOOP_RELAY_EXE) { $env:ORICOOP_RELAY_EXE } else { 'C:\OriCoop\src\RelayServer\RelayServer.exe' })
$script:TL_LockFile = $null
$script:TL_LockStaleMin = 90
$script:TL_Pids = New-Object 'System.Collections.Generic.List[int]'
$script:TL_Procs = New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'
$script:TL_Placed = @{}
$script:TL_LastOwnerCheck = [DateTime]::MinValue
$script:TL_RegPath = 'Software\Microsoft Studios\Ori And The Blind Forest: Definitive Edition'
$script:TL_RegBefore = $null
$script:TL_FocusFixes = 0
$script:TL_PrevFg = [IntPtr]::Zero

if (-not ('OriTestLaunch5' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class OriTestLaunch5
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    public static IntPtr Foreground() { return GetForegroundWindow(); }
    public static int WindowPid(IntPtr h) { int p; GetWindowThreadProcessId(h, out p); return p; }
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder buf, ref int size);
    // Needs only PROCESS_QUERY_LIMITED_INFORMATION, so it also works for games started by other (sandboxed) sessions,
    // where .NET's Process.Path (MainModule) fails.
    public static string ImagePath(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try { var sb = new StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageNameW(h, 0, sb, ref n) ? sb.ToString(0, n) : null; }
        finally { CloseHandle(h); }
    }

    const uint CREATE_UNICODE_ENVIRONMENT = 0x400, BELOW_NORMAL_PRIORITY_CLASS = 0x4000;
    const int STARTF_USESHOWWINDOW = 1, SW_MINIMIZE = 6, SW_SHOWNOACTIVATE = 4;
    const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
    static readonly IntPtr HWND_BOTTOM = new IntPtr(1), HWND_NOTOPMOST = new IntPtr(-2);
    const int GWL_EXSTYLE = -20, WS_EX_TOPMOST = 8;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr sa);
    static IntPtr s_desktop = IntPtr.Zero;
    // A separate desktop in WinSta0 for the test games: their windows can never take the owner's focus or input.
    // The handle stays open for this PowerShell's lifetime; the desktop lives while any of its processes runs.
    public static string EnsureDesktop(string name)
    {
        if (s_desktop == IntPtr.Zero)
        {
            s_desktop = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, 0x10000000 /* GENERIC_ALL */, IntPtr.Zero);
            if (s_desktop == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        return "WinSta0\\" + name;
    }

    // Starts without activating its first window (SW_SHOWNOACTIVATE) with exactly envPairs (BelowNormal if asked),
    // on desktop (e.g. "WinSta0\\OriCoopTests") when given.
    public static int Start(string exe, string args, string cwd, string[] envPairs, bool belowNormal, string desktop)
    {
        var block = new StringBuilder();
        foreach (var kv in envPairs) { block.Append(kv); block.Append('\0'); }
        block.Append('\0');
        IntPtr env = Marshal.StringToHGlobalUni(block.ToString());
        try
        {
            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            si.dwFlags = STARTF_USESHOWWINDOW;
            si.wShowWindow = SW_SHOWNOACTIVATE;
            if (!string.IsNullOrEmpty(desktop)) si.lpDesktop = desktop;
            PROCESS_INFORMATION pi;
            var cmd = new StringBuilder("\"" + exe + "\" " + args);
            if (!CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT | (belowNormal ? BELOW_NORMAL_PRIORITY_CLASS : 0u), env, cwd, ref si, out pi))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
            return pi.dwProcessId;
        }
        finally { Marshal.FreeHGlobal(env); }
    }

    public static IntPtr[] Windows(int pid)
    {
        var list = new List<IntPtr>();
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            int p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h) && GetWindow(h, 4 /* GW_OWNER */) == IntPtr.Zero) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }

    // Never topmost, never foreground, bottom of the z-order; moved to (x, y) when move is true.
    // Returns 1 if a focus steal was undone.
    // prev = the window the owner was using before the test game appeared (never a test window).
    public static int Quiet(IntPtr h, bool move, int x, int y, IntPtr prev)
    {
        int fixedFocus = 0;
        if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0)
            SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        if (GetForegroundWindow() == h && prev != IntPtr.Zero && IsWindow(prev))
        {
            // Give focus back to the owner's window. NOT by minimizing the game: Unity then shrinks its
            // resolution to the minimized size and saves it to the registry shared with the owner's copy.
            uint gameThread = GetWindowThreadProcessId(h, IntPtr.Zero), me = GetCurrentThreadId();
            AttachThreadInput(me, gameThread, true);
            SetForegroundWindow(prev);
            AttachThreadInput(me, gameThread, false);
            fixedFocus = 1;
        }
        uint f = SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | (move ? 0u : SWP_NOMOVE);
        SetWindowPos(h, HWND_BOTTOM, x, y, 0, 0, f);
        return fixedFocus;
    }
}
'@
}

# All test copies (existing or not). Copy a keeps the historical paths.
function Get-TestCopies {
    foreach ($c in @('a', 'b', 'c', 'd', 'e')) {
        $sfx = $(if ($c -eq 'a') { '' } else { '-' + $c })
        # ORICOOP_FAKE_OWNER_EXE: a test copy that stands in for the owner's game (never the owner's real game).
        if ($env:ORICOOP_FAKE_OWNER_EXE -and [string]::Equals($env:ORICOOP_FAKE_OWNER_EXE, "$($script:TL_Root)\game$sfx\oriDE.exe", [StringComparison]::OrdinalIgnoreCase)) { continue }
        $i = [int][char]$c - [int][char]'a'
        [pscustomobject]@{
            Name = $c; GameDir = "$($script:TL_Root)\game$sfx"; Exe = "$($script:TL_Root)\game$sfx\oriDE.exe"
            TestDir = "$($script:TL_Root)\test$sfx"
            AppData = @("$($script:TL_Root)\appdata$sfx", "$($script:TL_Root)\appdata2$sfx", "$($script:TL_Root)\appdata3$sfx", "$($script:TL_Root)\appdata4$sfx")
            RuntimeDir = "$($script:TL_Root)\test-runtime$sfx"
            LogsDir = $(if ($c -eq 'a') { "$($script:TL_Root)\logs" } else { "$($script:TL_Root)\logs\copy-$c" })
            Lock = "$($script:TL_Root)\testcopy$sfx.lock"; PortOffset = 100 * $i
        }
    }
}

# Path of a running process, or $null if it cannot be read (retried: a process that is just starting or exiting).
function Get-ProcPath($proc) {
    for ($i = 0; $i -lt 3; $i++) {
        $p = [OriTestLaunch5]::ImagePath($proc.Id)
        if (-not $p) { try { $p = $proc.Path } catch { } }
        if ($p) { return $p }
        if ($proc.HasExited) { return $null }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

# An oriDE.exe anywhere under C:\OriCoop\ is a test game (any copy, known to this script or added later): never the
# owner's. Only paths outside it (the owner's D:\SteamLibrary\...\Ori DE\OriDE.exe) count as the owner's game.
# (2026-11-18: copies d/e, unknown to older scripts, were taken for the owner's game and stopped other runs.)
# ORICOOP_FAKE_OWNER_EXE marks one test copy as the stand-in owner for experiments.
function Test-IsTestPath([string]$path) {
    if (-not $path) { return $false }
    if ($env:ORICOOP_FAKE_OWNER_EXE -and [string]::Equals($path, $env:ORICOOP_FAKE_OWNER_EXE, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    $root = [IO.Path]::GetFullPath($script:TL_Root).TrimEnd('\') + '\'
    return [IO.Path]::GetFullPath($path).StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
}

# T-075: a test server (relay / C-server / rooms) started by a run of the copy with port offset $offset: its exe is
# RelayServer*.exe, CServer*.exe or RoomServer*.exe and one argument is 47020, 47021 or 47022 + offset. Pure, so the
# self-test can check it.
function Test-IsCopyServer([string]$cmdLine, [int]$offset) {
    if ([string]::IsNullOrEmpty($cmdLine)) { return $false }
    $m = [regex]::Match($cmdLine, '^\s*"?([^"]*?)"?(\s|$)')
    $exe = [IO.Path]::GetFileName($m.Groups[1].Value)
    if ($exe -notmatch '^(RelayServer|CServer|RoomServer)[^\\/]*\.exe$') { return $false }
    $ports = @((47020 + $offset), (47021 + $offset), (47022 + $offset)) | ForEach-Object { [string]$_ }
    foreach ($tok in ($cmdLine.Substring($m.Length) -split '[\s=:]+')) { if ($ports -contains $tok.Trim('"')) { return $true } }
    return $false
}

# T-075: a run killed from outside (fail-fast, the owner watchdog, a closed shell) never reaches its finally block, so
# the relay / C-server it started keeps running and keeps its log open: every later run of that copy failed at
# "Remove-Item titlejoin-relay.log ... used by another process". When a run takes a copy, servers on that copy's ports
# whose launching PowerShell is gone are stopped (servers only, never a game; a parent that cannot be read counts as
# alive: never kill on doubt).
function Stop-OrphanTestServers([int]$offset, [string]$copyName) {
    $procs = @()
    try { $procs = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object { $_.Name -match '^(RelayServer|CServer|RoomServer)' }) } catch { return }
    foreach ($p in $procs) {
        if (-not (Test-IsCopyServer $p.CommandLine $offset)) { continue }
        $parentAlive = $true
        try { $parentAlive = [bool](Get-Process -Id $p.ParentProcessId -ErrorAction SilentlyContinue) } catch { $parentAlive = $true }
        if ($parentAlive) { continue }
        'ORPHAN test server in copy ' + $copyName + ' (its run is gone): ' + $p.ProcessId + ' ' + $p.CommandLine + ': stopped.'
        try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop; [void](Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue) } catch { }
    }
}

function Test-IsTestGame($proc) {
    return (Test-IsTestPath (Get-ProcPath $proc))
}

function Get-CopyGames($copy) {
    @(Get-Process oriDE -ErrorAction SilentlyContinue | Where-Object { $p = Get-ProcPath $_; $p -and [string]::Equals($p, $copy.Exe, [StringComparison]::OrdinalIgnoreCase) })
}

function Read-LockInfo([string]$path) {
    $h = @{}
    try { foreach ($l in [IO.File]::ReadAllLines($path)) { $i = $l.IndexOf('='); if ($i -gt 0) { $h[$l.Substring(0, $i)] = $l.Substring($i + 1) } } } catch { }
    $h
}

# Stale = (for locks written by this launcher) the PowerShell that took it has exited; foreign-format (hand-made)
# locks go stale after 90 min. A lock whose PowerShell is still alive is never stale, however old: long runs (scene
# sweeps, story sweeps) hold a copy for hours, and treating them as stale handed their copy (and games) to another run.
function Test-LockStale([string]$path) {
    $info = Read-LockInfo $path
    $age = $null
    try { $age = [DateTime]::UtcNow - (Get-Item -LiteralPath $path).LastWriteTimeUtc } catch { return $false }
    if (-not $info.ContainsKey('pid')) { return $age.TotalMinutes -gt $script:TL_LockStaleMin }   # foreign format
    $holder = Get-Process -Id ([int]$info['pid']) -ErrorAction SilentlyContinue
    if (-not $holder) { return $true }
    if ($info.ContainsKey('pidStart')) {
        try { if ($holder.StartTime.ToUniversalTime().Ticks -ne [long]$info['pidStart']) { return $true } } catch { }
    }
    return $false
}

function Enter-CopyLock($copy, [string]$script) {
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        try {
            $fs = [IO.File]::Open($copy.Lock, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
            $me = Get-Process -Id $PID
            $session = $(if ($env:ORICOOP_SESSION) { $env:ORICOOP_SESSION } else { 'unnamed session' })
            $text = "session=$session`r`nscript=$script`r`npid=$PID`r`npidStart=$($me.StartTime.ToUniversalTime().Ticks)`r`ntime=$([DateTime]::UtcNow.ToString('o'))`r`ncopy=$($copy.Name)`r`n"
            $bytes = [Text.Encoding]::UTF8.GetBytes($text)
            $fs.Write($bytes, 0, $bytes.Length); $fs.Close()
            return $true
        } catch [System.IO.IOException] {
            # Our own lock from an earlier script in this same PowerShell (kept until the process ends): reuse it.
            $own = Read-LockInfo $copy.Lock
            if ($own['pid'] -eq [string]$PID) {
                $session = $(if ($env:ORICOOP_SESSION) { $env:ORICOOP_SESSION } else { 'unnamed session' })
                $me = Get-Process -Id $PID
                [IO.File]::WriteAllText($copy.Lock, "session=$session`r`nscript=$script`r`npid=$PID`r`npidStart=$($me.StartTime.ToUniversalTime().Ticks)`r`ntime=$([DateTime]::UtcNow.ToString('o'))`r`ncopy=$($copy.Name)`r`n")
                return $true
            }
            if ($attempt -eq 0 -and (Test-Path -LiteralPath $copy.Lock) -and (Test-LockStale $copy.Lock)) {
                $old = Read-LockInfo $copy.Lock
                Write-Host ('stale lock on copy ' + $copy.Name + ' removed (session=' + $old['session'] + ' time=' + $old['time'] + ')')
                Remove-Item -LiteralPath $copy.Lock -Force -ErrorAction SilentlyContinue
                continue
            }
            return $false
        }
    }
    return $false
}

function Exit-AlongsideLock {
    $f = $script:TL_AlongsideLock
    if ($f -and (Test-Path -LiteralPath $f) -and (Read-LockInfo $f)['pid'] -eq [string]$PID) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue }
}

# A lock this session made BY HAND (no pid= line, its session name inside) may be reused by its own runs. A lock written
# by the launcher always has pid= and belongs to that run, even if another run of the same session wants the copy.
function Test-LockReusableByName([string]$lock) {
    if (-not $env:ORICOOP_SESSION -or -not (Test-Path -LiteralPath $lock)) { return $false }
    if ((Read-LockInfo $lock).ContainsKey('pid')) { return $false }
    return [bool]((Get-Content -LiteralPath $lock -Raw -ErrorAction SilentlyContinue) -match [regex]::Escape($env:ORICOOP_SESSION))
}

function Exit-CopyLock {
    Exit-AlongsideLock
    if ($script:TL_LockFile -and (Test-Path -LiteralPath $script:TL_LockFile)) {
        $info = Read-LockInfo $script:TL_LockFile
        if ($info['pid'] -eq [string]$PID) { Remove-Item -LiteralPath $script:TL_LockFile -Force -ErrorAction SilentlyContinue }
    }
    $script:TL_LockFile = $null
}

function Use-Copy($c) {
    $script:TL_Copy = $c.Name; $script:TL_GameDir = $c.GameDir; $script:TL_TestExe = $c.Exe; $script:TL_TestDir = $c.TestDir
    $script:TL_AppData = $c.AppData; $script:TL_RuntimeDir = $c.RuntimeDir; $script:TL_LogsDir = $c.LogsDir
    $script:TL_PortOffset = $c.PortOffset; $script:TL_LockFile = $c.Lock
    New-Item -ItemType Directory -Force -Path $c.TestDir, $c.LogsDir | Out-Null
}

# Owner games = every oriDE.exe that is not the test copy (and not one of ours). An unreadable path counts as the owner's.
function Get-OwnerGames {
    # Owner = a readable path that is not a test copy (normally D:\...\Ori DE\OriDE.exe). An unreadable path is NOT
    # taken as the owner's any more: games started by other (sandboxed) sessions are unreadable to .NET, and the
    # owner's Steam-launched game is always readable with limited query rights (false alarms seen 2026-11-16).
    $owner = @(Get-Process oriDE -ErrorAction SilentlyContinue | Where-Object {
        if ($script:TL_Pids.Contains($_.Id)) { return $false }
        $p = Get-ProcPath $_
        if (-not $p) { if (-not $_.HasExited) { Write-Host ('watchdog: cannot read the path of oriDE pid ' + $_.Id + '; not treated as the owner') }; return $false }
        -not (Test-IsTestGame $_)
    })
    if ($env:ORICOOP_FAKE_OWNER_FILE -and (Test-Path -LiteralPath $env:ORICOOP_FAKE_OWNER_FILE)) {
        $owner += [pscustomobject]@{ Id = 0; Path = 'FAKE OWNER GAME (' + $env:ORICOOP_FAKE_OWNER_FILE + ')' }
    }
    $owner
}

function Format-Games($games) { ($games | ForEach-Object { $p = $null; if ($_.Id -ne 0) { $p = Get-ProcPath $_ } else { $p = $_.Path }; '' + $_.Id + ' ' + $p }) -join '; ' }

# Call first. Refuses (exit 2) while the owner's game runs; otherwise takes a free test copy, waiting while every
# copy is locked. Never stops anything. -Force is accepted for old callers but no longer closes games (it used to
# kill by name, which once closed the owner's live session). -Copy a|b|c (or $env:ORICOOP_TEST_COPY) pins a copy.
function Start-TestRun([switch]$Force, [string]$Copy = '', [int]$WaitMinutes = -1) {
    if (-not $Copy -and $env:ORICOOP_TEST_COPY) { $Copy = $env:ORICOOP_TEST_COPY }
    if ($WaitMinutes -lt 0) { $WaitMinutes = $(if ($env:ORICOOP_LOCK_WAIT_MIN) { [int]$env:ORICOOP_LOCK_WAIT_MIN } else { 180 }) }
    $script = $(if ($MyInvocation.ScriptName) { Split-Path -Leaf $MyInvocation.ScriptName } else { 'interactive' })
    $candidates = @(Get-TestCopies | Where-Object { (Test-Path -LiteralPath $_.Exe) -and (-not $Copy -or $_.Name -eq $Copy) })
    if ($candidates.Count -eq 0) { 'NO TEST COPY ' + $Copy + ' (make it with src\make-test-copy.ps1)'; exit 2 }
    $sw = [System.Diagnostics.Stopwatch]::StartNew(); $lastNote = -999
    while ($true) {
        $capped = -1
        $owner = @(Get-OwnerGames)
        if ($owner.Count -gt 0 -and -not (Enter-Alongside $owner $script)) {
            if (Test-PlayAlongside) {
                $i = Read-LockInfo $script:TL_AlongsideLock
                $min = [int][Math]::Floor($sw.Elapsed.TotalMinutes)
                if ($min -ge $WaitMinutes) { 'OWNER IS PLAYING and another test runs alongside: gave up after ' + $WaitMinutes + ' min (exit 4).'; exit 4 }
                if ($min -ne $lastNote) { $lastNote = $min; 'WAITING: the owner is playing; one test at a time, held by ' + $i['session'] + ' (' + $i['script'] + ')' }
                Start-Sleep -Seconds 15; continue
            }
            'OWNER IS PLAYING (' + (Format-Games $owner) + '): not starting the test; no process stopped. Rerun later. / 有人在玩游戏，测试未启动，稍后再跑。'
            exit 2
        }
        foreach ($c in $candidates) {
            # A lock this session took by hand (same ORICOOP_SESSION name, e.g. New-Item per the playtest rules) is
            # reused and left for the session to delete.
            $held = Read-LockInfo $c.Lock
            # Only a HAND-MADE lock (no pid= line) is reused by name: a lock written by another run of the same session
            # (e.g. two nightly suites, same ORICOOP_SESSION) belongs to that run (2026-11-18: two runs shared copy a/e).
            $mine = Test-LockReusableByName $c.Lock
            if (-not $mine -and -not (Enter-CopyLock $c $script)) { continue }
            # Concurrency cap (T-078): at most ORICOOP_MAX_RUNS runs (default 3, about 6 games) at once. Measured over 180
            # full 2-player net runs (2026-11-16..18): median own time 463 s at 2-3 games running, 535 s at 4-5, 633 s
            # at 6-7, the 660 s timeout beyond that, and the pass rate fell from ~50 % to ~20 %: fewer runs at once pass
            # more runs per hour. A copy pinned with -Copy / ORICOOP_TEST_COPY is not capped.
            $maxRuns = $(if ($env:ORICOOP_MAX_RUNS) { [int]$env:ORICOOP_MAX_RUNS } else { 3 })
            if (-not $mine -and -not $Copy) {
                $others = @(Get-TestCopies | Where-Object { $_.Name -ne $c.Name -and (Test-Path -LiteralPath $_.Lock) -and -not (Test-LockStale $_.Lock) }).Count
                if ($others -ge $maxRuns) {
                    Remove-Item -LiteralPath $c.Lock -Force -ErrorAction SilentlyContinue
                    $capped = $others; break
                }
            }
            $orphans = @(Get-CopyGames $c)
            if ($orphans.Count -gt 0) {
                # Games of this copy while nobody holds its lock. If the PowerShell that started them is gone, they are
                # orphans of a dead run: stop them (test games only, by pid) and take the copy. If it is alive, a run
                # holds the copy without its lock: leave everything alone and skip the copy.
                # A game whose parent cannot be read (CIM query failed, e.g. under load) counts as alive: never kill on doubt.
                $alive = @($orphans | Where-Object { $pp = $null; try { $pp = (Get-CimInstance Win32_Process -Filter ("ProcessId=" + $_.Id) -ErrorAction Stop).ParentProcessId } catch { return $true }; (-not $pp) -or (Get-Process -Id $pp -ErrorAction SilentlyContinue) })
                if ($alive.Count -eq 0) {
                    'ORPHAN test games in copy ' + $c.Name + ' (their run is gone: ' + (Format-Games $orphans) + '): stopped.'
                    foreach ($o in $orphans) { if (Test-IsTestGame $o) { try { $o.Kill(); [void]$o.WaitForExit(10000) } catch { } } }
                } else {
                    if (-not $mine) { Remove-Item -LiteralPath $c.Lock -Force -ErrorAction SilentlyContinue }
                    'TEST GAME RUNNING in copy ' + $c.Name + ' without a lock, its run is alive (' + (Format-Games $alive) + '): skipped; no process stopped.'
                    continue
                }
            }
            Stop-OrphanTestServers $c.PortOffset $c.Name
            Use-Copy $c
            'TEST COPY ' + $c.Name + ': ' + $c.GameDir + ' (lock ' + $c.Lock + ', port offset ' + $c.PortOffset + ')'
            if ($Force) { 'NOTE: -Force no longer closes running games.' }
            Install-TestPlugins
            Save-OriDisplayPrefs
            return
        }
        $min = [int][Math]::Floor($sw.Elapsed.TotalMinutes)
        if ($min -ge $WaitMinutes) { 'ALL TEST COPIES BUSY for ' + $WaitMinutes + ' min: giving up (exit 4).'; exit 4 }
        if ($min -ne $lastNote) {
            $lastNote = $min
            if ($capped -ge 0) { 'WAITING: ' + $capped + ' test runs already going (cap ORICOOP_MAX_RUNS=' + $maxRuns + ', docs	esting-rules.md)' }
            foreach ($c in $candidates) {
                $i = Read-LockInfo $c.Lock
                if ($i.Count -gt 0) { 'WAITING: copy ' + $c.Name + ' held by ' + $i['session'] + ' (' + $i['script'] + ', since ' + $i['time'] + ')' }
                else { 'WAITING: copy ' + $c.Name + ' has test games running without a lock' }
            }
        }
        Start-Sleep -Seconds 15
    }
}

# The locked copy gets the session's own build ($env:ORICOOP_TEST_DLL) and the test-only TestQuiet plugin.
function Install-TestPlugins {
    $plugins = Join-Path $script:TL_GameDir 'BepInEx\plugins'
    if ($env:ORICOOP_TEST_DLL) {
        if (-not (Test-Path -LiteralPath $env:ORICOOP_TEST_DLL)) { throw ('ORICOOP_TEST_DLL not found: ' + $env:ORICOOP_TEST_DLL) }
        # A just-stopped game (orphan) can keep the DLL mapped for a few seconds: "user-mapped section open". Retry.
        for ($try = 1; ; $try++) {
            try { Copy-Item -LiteralPath $env:ORICOOP_TEST_DLL (Join-Path $plugins 'OriCoop.dll') -Force -ErrorAction Stop; break }
            catch [System.IO.IOException] { if ($try -ge 30) { throw }; Start-Sleep -Seconds 1 }
        }
        'installed ' + $env:ORICOOP_TEST_DLL + ' into copy ' + $script:TL_Copy
    }
    # TestQuiet of THIS launcher's checkout (worktree), rebuilt when its sources are newer, installed whenever the copy
    # holds a different one (T-085: it blocks the owner's real controller; every run must carry the current one).
    $quiet = Join-Path $plugins 'OriCoop.TestQuiet.dll'
    $built = Get-TestQuietBuild
    if (-not (Test-Path -LiteralPath $quiet) -or (Get-Sha256 $built) -ne (Get-Sha256 $quiet)) {
        for ($try = 1; ; $try++) {
            try { Copy-Item -LiteralPath $built $quiet -Force -ErrorAction Stop; break }
            catch [System.IO.IOException] { if ($try -ge 30) { throw }; Start-Sleep -Seconds 1 }
        }
    }
}

# .NET, not Get-FileHash: that cmdlet is missing when Windows PowerShell is started from pwsh (run-affected's suites).
function Get-Sha256([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    try { return [BitConverter]::ToString($sha.ComputeHash($fs)) } finally { $fs.Close(); $sha.Dispose() }
}

function Get-TestQuietBuild {
    $dir = Join-Path $script:TL_SrcDir 'TestQuiet'
    $dll = Join-Path $script:TL_SrcDir 'bin\OriCoop.TestQuiet.dll'
    $newest = (Get-ChildItem -LiteralPath $dir -Filter '*.cs' | Sort-Object LastWriteTimeUtc | Select-Object -Last 1).LastWriteTimeUtc
    if ((Test-Path -LiteralPath $dll) -and (Get-Item -LiteralPath $dll).LastWriteTimeUtc -ge $newest) { return $dll }
    $m = New-Object System.Threading.Mutex($false, 'OriCoopTestQuietBuild')
    [void]$m.WaitOne()
    try {
        if (-not (Test-Path -LiteralPath $dll) -or (Get-Item -LiteralPath $dll).LastWriteTimeUtc -lt $newest) {
            $out = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dir 'build-testquiet.ps1') -NoInstall 2>&1
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dll)) { throw ('TestQuiet build failed: ' + ($out -join ' ')) }
            Write-Host ('built TestQuiet: ' + $dll)
        }
    } finally { $m.ReleaseMutex() }
    return $dll
}

# Unity's screen prefs (HKCU, shared by every test copy and the owner's game). Each test game writes its own small
# window size there when it exits (seen: 640x360, 375x22), and with parallel runs a per-run snapshot can catch another
# run's value. So the OWNER's values live in C:\OriCoop\owner-display-prefs.json: refreshed only when no test copy is
# locked and no oriDE.exe runs (then the registry holds the owner's values) and they don't look like a test size;
# put back by the last run to finish (no other live lock) while the owner's game is not running. Never deletes values.
$script:TL_PrefsFile = 'C:\OriCoop\owner-display-prefs.json'

function Read-OriPrefsNow {
    $h = @{}
    $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:TL_RegPath)
    if ($k) {
        try {
            foreach ($n in $k.GetValueNames()) {
                if ($n -like 'Screenmanager*' -or $n -like 'UnitySelectMonitor*') { $h[$n] = @([string]$k.GetValueKind($n), $k.GetValue($n)) }
            }
        } finally { $k.Close() }
    }
    $h
}

function Test-OtherLiveLocks {
    @(Get-TestCopies | Where-Object { $_.Lock -ne $script:TL_LockFile -and (Test-Path -LiteralPath $_.Lock) -and -not (Test-LockStale $_.Lock) }).Count -gt 0
}

function Test-LooksLikeTestSize($prefs) {
    $w = $null; $h = $null
    foreach ($n in $prefs.Keys) {
        if ($n -like 'Screenmanager Resolution Width_*') { $w = [int]$prefs[$n][1] }
        if ($n -like 'Screenmanager Resolution Height_*') { $h = [int]$prefs[$n][1] }
    }
    if ($w -eq $null -or $h -eq $null) { return $false }
    return ($w -lt 800 -or $h -lt 450 -or "$($w)x$($h)" -in @('800x450', '960x540', '1280x720'))
}

function Save-OriDisplayPrefs {
    $now = Read-OriPrefsNow
    $quiet = -not (Test-OtherLiveLocks) -and @(Get-Process oriDE -ErrorAction SilentlyContinue).Count -eq 0
    if ($quiet -and $now.Count -gt 0 -and -not (Test-LooksLikeTestSize $now)) {
        $o = @{}; foreach ($n in $now.Keys) { $o[$n] = @($now[$n][0], $now[$n][1]) }
        try { ($o | ConvertTo-Json -Compress) | Set-Content -LiteralPath $script:TL_PrefsFile -Encoding UTF8 } catch { }
    }
    $script:TL_RegBefore = @{}
    if (Test-Path -LiteralPath $script:TL_PrefsFile) {
        try {
            $j = Get-Content -LiteralPath $script:TL_PrefsFile -Raw | ConvertFrom-Json
            foreach ($p in $j.PSObject.Properties) { $script:TL_RegBefore[$p.Name] = @([string]$p.Value[0], $p.Value[1]) }
        } catch { $script:TL_RegBefore = @{} }
    }
}

function Test-SameValue($a, $b) {
    if ($a -is [byte[]] -and $b -is [byte[]]) { return [Convert]::ToBase64String($a) -eq [Convert]::ToBase64String($b) }
    return ('' + $a) -eq ('' + $b)
}

function Restore-OriDisplayPrefs {
    if ($script:TL_RegBefore -eq $null -or $script:TL_RegBefore.Count -eq 0) { return }
    if (@(Get-OwnerGames | Where-Object { $_.Id -ne 0 }).Count -gt 0) { 'display prefs: owner game running, left alone'; return }
    if (Test-OtherLiveLocks) { return }   # another run is still going: the last one to finish restores
    $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:TL_RegPath, $true)
    if (-not $k) { return }
    $changed = 0
    try {
        foreach ($n in $script:TL_RegBefore.Keys) {
            $kind = $script:TL_RegBefore[$n][0]; $val = $script:TL_RegBefore[$n][1]
            if ($kind -eq 'DWord') { $val = [int]$val } elseif ($kind -eq 'QWord') { $val = [long]$val }
            if (-not (Test-SameValue $k.GetValue($n) $val)) { $k.SetValue($n, $val, [Microsoft.Win32.RegistryValueKind]$kind); $changed++ }
        }
    } finally { $k.Close() }
    if ($changed -gt 0) { 'display prefs: restored ' + $changed + ' owner value(s)' }
}

# Hidden desktop (default; ORICOOP_TEST_DESKTOP=0 = the owner's desktop, old behaviour): test games are created on a
# separate desktop "OriCoopTests" in WinSta0, so their windows can never take focus or input from the owner's desktop.
# Verified 2026-11-17: run-test PASS and 2P nettest 666/666 + 673/673 there; Unity renders normally.
function Get-TestDesktop {
    if ($env:ORICOOP_TEST_DESKTOP -eq '0') { return '' }
    return [OriTestLaunch5]::EnsureDesktop('OriCoopTests')
}

# Starts one test game quietly. -Vars: variables to set ('' = set empty, like ProcessStartInfo). Returns the Process.
function Start-TestGame([string]$Arguments, [hashtable]$Vars = @{}, [string]$Tag = 'game', [switch]$NeedsSteam, [string]$WorkingDirectory = '') {
    if (-not $WorkingDirectory) { $WorkingDirectory = $script:TL_GameDir }
    $all = @{}
    foreach ($e in [Environment]::GetEnvironmentVariables().GetEnumerator()) { $all[[string]$e.Key] = [string]$e.Value }
    foreach ($key in $Vars.Keys) { $all[[string]$key] = [string]$Vars[$key] }
    $all['ORICOOP_QUIET'] = '1'
    # No headless mode: with -batchmode (with or without -nographics) Ori throws "Graphics.SetRenderTarget called with
    # bad depth RenderBuffer" every frame and never leaves the title flow (2026-11-17, docs\testing-rules.md).
    if ($env:ORICOOP_TEST_HEADLESS) { Write-Host 'NOTE: ORICOOP_TEST_HEADLESS ignored: Ori cannot run in Unity batch mode (see docs\testing-rules.md).' }
    if ($Arguments -match '-screen-width\s+(\d+)' ) { $sw = $Matches[1]; if ($Arguments -match '-screen-height\s+(\d+)') { $all['ORICOOP_SCREEN'] = $sw + 'x' + $Matches[1] } }
    if ($NeedsSteam) { $all.Remove('ORICOOP_NO_STEAM') } else { $all['ORICOOP_NO_STEAM'] = '1' }
    # Every test game writes the mod's live tagged log (ORICOOP_LOG_TAG; harness runs only); fail-fast watches it,
    # because some suites write their result file only at the end.
    if (-not $all['ORICOOP_LOG_TAG']) {
        $all['ORICOOP_LOG_TAG'] = Get-ClientLogTag $Tag
        $live = Get-ClientLog $Tag
        if (Test-Path -LiteralPath $live) { Remove-Item -LiteralPath $live -Force -ErrorAction SilentlyContinue }
    }
    $script:TL_LiveLogs += Join-Path 'C:\OriCoop\test' ($all['ORICOOP_LOG_TAG'] + '.log')
    $pairs = @($all.Keys | Sort-Object | ForEach-Object { $_ + '=' + $all[$_] })
    Update-OwnerForeground
    $procId = [OriTestLaunch5]::Start($script:TL_TestExe, $Arguments, $WorkingDirectory, [string[]]$pairs, ($env:ORICOOP_TEST_PRIORITY -eq 'BelowNormal'), (Get-TestDesktop))
    $p = [System.Diagnostics.Process]::GetProcessById($procId)
    $null = $p.Handle   # keep a handle so HasExited / ExitCode work after it exits
    $script:TL_Pids.Add($procId)
    $script:TL_Procs.Add($p)
    # T-085: its BepInEx log must say TestQuiet blocked the real devices (checked by Invoke-TestWatch).
    $bepLog = $(if ($Arguments -match '--doorstop-target-assembly "([^"]+)\\core\\BepInEx\.Preloader\.dll"') { Join-Path $Matches[1] 'LogOutput.log' } else { Join-Path $script:TL_GameDir 'BepInEx\LogOutput.log' })
    $script:TL_InputChecks += [pscustomobject]@{ Tag = $Tag; Proc = $p; Log = $bepLog; Started = [DateTime]::UtcNow; ReadyAt = $null; Ok = $false }
    if ($script:TL_Alongside) { Set-AlongsideLimits $p }
    Write-Host ('TESTGAME ' + $Tag + ' pid=' + $procId + ' quiet (no focus, ' + $(if ($env:ORICOOP_TEST_PRIORITY -eq 'BelowNormal') { 'BelowNormal' } else { 'Normal priority' }) + ', muted' + $(if ($NeedsSteam) { ', Steam ON' } else { ', no Steam' }) + ')')
    Invoke-TestWatch -Force
    $p
}

# Several games of ONE copy must start back to back: Unity's single-instance guard (named after the exe path) makes a
# copy that starts after another has finished starting fail with "Player is already running" (no log at all), and
# copies sharing one BepInEx folder collide on BepInEx.cfg ("Sharing violation"). So each client gets a private
# BepInEx root in $TL_RuntimeDir\<tag>\BepInEx (core + config copied from the copy, plugins/patchers JUNCTIONS to it),
# all roots are prepared first, then all games are launched without waiting, then each one's start-up is awaited.
# Specs: @(@{ Tag = 'host'; Arguments = '-screen-...'; Vars = @{...} }, ...). Each client's Unity log is
# $TL_TestDir\<tag>-unity.log and its BepInEx log $TL_RuntimeDir\<tag>\BepInEx\LogOutput.log.
function New-TestClientRoot([string]$Tag) {
    $bepRoot = Join-Path $script:TL_RuntimeDir ($Tag + '\BepInEx')
    foreach ($dir in @($bepRoot, (Join-Path $bepRoot 'core'), (Join-Path $bepRoot 'config'))) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $src = Join-Path $script:TL_GameDir 'BepInEx'
    # A game of this copy stopped a moment ago (an orphan of a dead run) can keep its core DLLs mapped for a few seconds:
    # "user-mapped section open" (T-080, 2026-11-18). Retry like the DLL install.
    foreach ($dir in @('core', 'config')) {
        for ($try = 1; ; $try++) {
            try { Copy-Item -Path "$src\$dir\*" -Destination (Join-Path $bepRoot $dir) -Recurse -Force -ErrorAction Stop; break }
            catch [System.IO.IOException] { if ($try -ge 30) { throw }; Start-Sleep -Seconds 1 }
        }
    }
    foreach ($dir in @('plugins', 'patchers')) {
        $link = Join-Path $bepRoot $dir
        # A real folder in place of the junction (seen in copy e: empty plugins/patchers) starts the game with no mod
        # and no TestQuiet, so Steam tracks the test game as "Ori running". An empty one is replaced; anything else stops.
        if ((Test-Path -LiteralPath $link) -and -not ((Get-Item -LiteralPath $link -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            if (@(Get-ChildItem -LiteralPath $link -Force).Count -gt 0) { throw ('not a junction and not empty: ' + $link + ' (expected a link to ' + (Join-Path $src $dir) + ')') }
            Remove-Item -LiteralPath $link -Force
        }
        if (-not (Test-Path -LiteralPath $link)) { New-Item -ItemType Junction -Path $link -Target (Join-Path $src $dir) | Out-Null }
        if (-not (Test-Path -LiteralPath (Join-Path $link 'OriCoop.TestQuiet.dll')) -and $dir -eq 'plugins') { throw ('TestQuiet missing in ' + $link + ': the test game would register with Steam') }
    }
    foreach ($log in @((Join-Path $script:TL_TestDir ($Tag + '-unity.log')), (Join-Path $script:TL_TestDir ($Tag + '.log')), (Join-Path $bepRoot 'LogOutput.log'))) {
        if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
    }
    $bepRoot
}

# Unlinks the plugins/patchers junctions (rmdir removes only the link), so a later recursive delete of the runtime
# folder can never reach the copy's real plugins.
function Remove-TestClientLinks {
    Get-ChildItem -LiteralPath $script:TL_RuntimeDir -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        foreach ($j in @('plugins', 'patchers')) {
            $p = Join-Path $_.FullName ('BepInEx\' + $j)
            if ((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { cmd /c rmdir "$p" | Out-Null }
        }
    }
}

# The mod writes a client's private log (ORICOOP_LOG_TAG, ORICOOP_TEST=1 only) to the hard-coded C:\OriCoop\test, so in
# copies b/c the tag gets the copy name in front to keep copies apart. Use this to find it.
function Get-ClientLogTag([string]$Tag) { $(if ($script:TL_Copy -and $script:TL_Copy -ne 'a') { $script:TL_Copy + '-' + $Tag } else { $Tag }) }
function Get-ClientLog([string]$Tag) { Join-Path 'C:\OriCoop\test' ((Get-ClientLogTag $Tag) + '.log') }

function Start-TestClients([object[]]$Specs, [int]$StartupSec = 90) {
    $roots = @{}
    foreach ($s in $Specs) { $roots[$s.Tag] = New-TestClientRoot $s.Tag }
    $procs = @()
    foreach ($s in $Specs) {
        $root = $roots[$s.Tag]
        $vars = @{}; if ($s.Vars) { foreach ($k in $s.Vars.Keys) { $vars[$k] = $s.Vars[$k] } }
        $vars['ORICOOP_LOG_TAG'] = Get-ClientLogTag $s.Tag
        $tagLog = Get-ClientLog $s.Tag
        if (Test-Path -LiteralPath $tagLog) { Remove-Item -LiteralPath $tagLog -Force }
        $unityLog = Join-Path $script:TL_TestDir ($s.Tag + '-unity.log')
        $preloader = Join-Path $root 'core\BepInEx.Preloader.dll'
        $procs += Start-TestGame -Arguments ($s.Arguments + " -logFile `"$unityLog`" --doorstop-target-assembly `"$preloader`"") -Vars $vars -Tag $s.Tag -NeedsSteam:([bool]$s.NeedsSteam)
    }
    for ($i = 0; $i -lt $Specs.Count; $i++) {
        $tag = $Specs[$i].Tag; $p = $procs[$i]
        $logs = @((Get-ClientLog $tag), (Join-Path $script:TL_TestDir ($tag + '-unity.log')))
        $sw = [System.Diagnostics.Stopwatch]::StartNew(); $ready = $false
        while (-not $ready -and $sw.Elapsed.TotalSeconds -lt $StartupSec) {
            if ($p.HasExited) { break }
            foreach ($l in $logs) { if ((Test-Path -LiteralPath $l) -and ((Get-Content -LiteralPath $l -Raw -ErrorAction SilentlyContinue) -match 'Chainloader startup complete')) { $ready = $true; break } }
            if (-not $ready) { Invoke-TestWatch; Start-Sleep -Milliseconds 250 }
        }
        if ($ready) { Write-Host ('READY ' + $tag + ' pid=' + $p.Id + ' after ' + [int]$sw.Elapsed.TotalSeconds + ' s') }
        elseif ($p.HasExited) { Write-Host ('STARTUP FAILED ' + $tag + ' pid=' + $p.Id + ' exit=' + $p.ExitCode + ' (no "Chainloader startup complete"; "Player is already running" leaves no log)') }
        else { Write-Host ('STARTUP SLOW ' + $tag + ' pid=' + $p.Id + ': no "Chainloader startup complete" after ' + $StartupSec + ' s, continuing') }
    }
    $procs
}

# Window placement: tiles from the bottom-right corner of the primary work area, right to left, bottom to top.
function Get-TilePosition([int]$index, [int]$w, [int]$h) {
    Add-Type -AssemblyName System.Windows.Forms
    $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $perRow = [Math]::Max(1, [Math]::Floor($wa.Width / [Math]::Max(1, $w)))
    $col = $index % $perRow; $row = [Math]::Floor($index / $perRow)
    @([int]($wa.Right - ($col + 1) * $w), [int]([Math]::Max($wa.Top, $wa.Bottom - ($row + 1) * $h)))
}

# Remembers the window the owner is using (anything not owned by a test game) so a focus steal can be undone.
function Update-OwnerForeground {
    $fg = [OriTestLaunch5]::Foreground()
    if ($fg -ne [IntPtr]::Zero) {
        $fgPid = [OriTestLaunch5]::WindowPid($fg)
        if (-not $script:TL_Pids.Contains($fgPid) -and -not (@(Get-Process -Id $fgPid -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq 'oriDE' }).Count)) { $script:TL_PrevFg = $fg }
    }
}

function Set-TestWindowsQuiet {
    Update-OwnerForeground
    foreach ($p in $script:TL_Procs) {
        if ($p.HasExited) { continue }
        foreach ($h in [OriTestLaunch5]::Windows($p.Id)) {
            # Unity resizes/re-centres its window while starting up, so the window is put back in its corner tile
            # whenever its rectangle is not the one we last set.
            $key = [string]$h
            $r = New-Object OriTestLaunch5+RECT
            [void][OriTestLaunch5]::GetWindowRect($h, [ref]$r)
            $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
            if (-not $script:TL_Placed.ContainsKey($key)) {
                $script:TL_Placed[$key] = @{ Tile = $script:TL_Placed.Count + 3 * [int]($script:TL_PortOffset / 100); X = $null; Y = $null }
            }
            $slot = $script:TL_Placed[$key]
            $pos = Get-TilePosition $slot.Tile $w $hh
            $x = $pos[0]; $y = $pos[1]
            $move = ($r.Left -ne $x -or $r.Top -ne $y)
            $script:TL_FocusFixes += [OriTestLaunch5]::Quiet($h, $move, $x, $y, $script:TL_PrevFg)
        }
    }
}

$script:TL_StoppedPids = New-Object 'System.Collections.Generic.List[int]'
function Stop-TestGames {
    foreach ($p in $script:TL_Procs) {
        $alive = $false
        try { $alive = -not $p.HasExited } catch { continue }   # already disposed by the script = already handled
        if ($alive) {
            try { $script:TL_StoppedPids.Add($p.Id); $p.Kill(); [void]$p.WaitForExit(10000) }
            catch [System.InvalidOperationException] { }   # disposed by the script: already exited and handled
            catch { Write-Host ('could not stop test game ' + $p.Id + ': ' + $_) }
        }
    }
}

# Play alongside the owner (default with the hidden desktop; ORICOOP_PLAY_ALONGSIDE=0 = stop when the owner plays): while the owner's game runs, exactly one test run
# continues (it holds testcopy-alongside.lock in the repo root), its games pinned to ORICOOP_ALONGSIDE_CPUS (hex affinity mask,
# default 0xFC0 = logical CPUs 6-11), Normal priority. Every other run stops (exit 3) or waits at start. The owner's
# process is never touched; display prefs are left alone while it runs (Restore-OriDisplayPrefs).
$script:TL_AlongsideLock = Join-Path $script:TL_Root 'testcopy-alongside.lock'
$script:TL_Alongside = $false
function Test-PlayAlongside { return ($env:ORICOOP_PLAY_ALONGSIDE -ne '0' -and (Get-TestDesktop) -ne '') }
function Set-AlongsideLimits($p) {
    $mask = 0xFC0; if ($env:ORICOOP_ALONGSIDE_CPUS) { $mask = [Convert]::ToInt64(($env:ORICOOP_ALONGSIDE_CPUS -replace '^0x', ''), 16) }
    try { if (-not $p.HasExited) { $p.ProcessorAffinity = [IntPtr]$mask } } catch { Write-Host ('could not limit test game ' + $p.Id + ': ' + $_) }
}
function Enter-Alongside($owner, [string]$script) {
    if ($script:TL_Alongside) { return $true }
    if (-not (Test-PlayAlongside)) { return $false }
    $lock = [pscustomobject]@{ Name = 'alongside'; Lock = $script:TL_AlongsideLock }
    if (-not (Enter-CopyLock $lock $script)) { return $false }
    $script:TL_Alongside = $true
    foreach ($p in $script:TL_Procs) { Set-AlongsideLimits $p }
    Write-Host ('PLAYING ALONGSIDE the owner (' + (Format-Games $owner) + '): this run continues on the hidden desktop, CPUs mask ' + $(if ($env:ORICOOP_ALONGSIDE_CPUS) { $env:ORICOOP_ALONGSIDE_CPUS } else { '0xFC0' }) + '; other runs wait.')
    return $true
}

# Call from every wait loop (cheap). Keeps windows quiet; every 2 s checks for the owner's game.
$script:TL_InputChecks = @()
# T-085: a test game whose TestQuiet did not log "physical input blocked, vibration blocked" could vibrate or read the
# owner's controller: stop this run's games and refuse the run (exit 5). Checked once BepInEx finished loading.
function Test-InputBlocked {
    foreach ($c in $script:TL_InputChecks) {
        if ($c.Ok -or $c.Proc.HasExited) { continue }
        $text = ''
        try {
            if ((Test-Path -LiteralPath $c.Log) -and (Get-Item -LiteralPath $c.Log).LastWriteTimeUtc -ge $c.Started.AddSeconds(-2)) {
                $fs = [IO.File]::Open($c.Log, 'Open', 'Read', 'ReadWrite'); $rd = New-Object IO.StreamReader($fs); $text = $rd.ReadToEnd(); $rd.Close()
            }
        } catch { }
        if ($text -match 'TestQuiet: physical input blocked, vibration blocked') { $c.Ok = $true; continue }
        $why = $null
        if ($text -match 'TestQuiet: INPUT BLOCK FAILED: ([^\r\n]*)') { $why = $Matches[1] }
        elseif ($text -match 'Chainloader startup complete') {
            if (-not $c.ReadyAt) { $c.ReadyAt = [DateTime]::UtcNow }
            elseif (([DateTime]::UtcNow - $c.ReadyAt).TotalSeconds -gt 30) { $why = 'no "physical input blocked, vibration blocked" line 30 s after BepInEx started (TestQuiet missing or old?)' }
        }
        if ($why) {
            Stop-TestGames
            Write-Host ('INPUT NOT BLOCKED in test game ' + $c.Tag + ' (' + $c.Log + '): ' + $why + '. The run was stopped so it cannot vibrate or read the owner''s controller (T-085).')
            Restore-OriDisplayPrefs | Write-Host
            Exit-CopyLock
            exit 5
        }
    }
}

function Invoke-TestWatch([switch]$Force) {
    try { Set-TestWindowsQuiet } catch { }
    Test-InputBlocked
    $now = [DateTime]::UtcNow
    if (-not $Force -and ($now - $script:TL_LastOwnerCheck).TotalSeconds -lt 2) { return }
    $script:TL_LastOwnerCheck = $now
    $owner = @(Get-OwnerGames)
    if ($owner.Count -gt 0 -and (Enter-Alongside $owner 'watchdog')) { return }
    if ($owner.Count -gt 0) {
        Stop-TestGames
        Write-Host ('OWNER IS PLAYING (' + (Format-Games $owner) + '): test stopped, only this run''s games (pid ' + ($script:TL_Pids -join ',') + ') were closed. Rerun later. / 有人开始玩游戏了，测试已停止，稍后再跑。')
        Restore-OriDisplayPrefs | Write-Host
        Exit-CopyLock
        exit 3
    }
}

# Waits for the given test games to exit (or the deadline), running the watchdog. Returns $true if all exited.
# Fail fast (default on; $env:ORICOOP_FAIL_FAST=0 for a full diagnostic run): with -ResultFiles, the first "] FAIL "
# line in any of them starts a grace period ($env:ORICOOP_FAIL_FAST_GRACE, default 10 s, so the failing phase writes
# its remaining checks and the tagged logs catch up), then this run's games are stopped and the suite reports FAIL.
$script:TL_FailFastHit = $null
$script:TL_LiveLogs = @()
function Wait-TestGames($Processes, [int]$TimeoutSec, [System.Diagnostics.Stopwatch]$Stopwatch = $null, [string[]]$ResultFiles = @()) {
    if (-not $Stopwatch) { $Stopwatch = [System.Diagnostics.Stopwatch]::StartNew() }
    $failFast = $ResultFiles.Count -gt 0 -and $env:ORICOOP_FAIL_FAST -ne '0'
    $grace = 10; if ($env:ORICOOP_FAIL_FAST_GRACE) { $grace = [int]$env:ORICOOP_FAIL_FAST_GRACE }
    $failAt = $null; $nextScan = 0
    while (@($Processes | Where-Object { -not $_.HasExited }).Count -gt 0 -and $Stopwatch.Elapsed.TotalSeconds -lt $TimeoutSec) {
        Invoke-TestWatch
        if ($failFast -and -not $failAt -and $Stopwatch.Elapsed.TotalSeconds -ge $nextScan) {
            $nextScan = $Stopwatch.Elapsed.TotalSeconds + 2
            foreach ($f in @($ResultFiles) + @($script:TL_LiveLogs)) {
                if (-not (Test-Path -LiteralPath $f)) { continue }
                $hit = Select-String -LiteralPath $f -Pattern '\] FAIL |\] TEST FAIL ' -List -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($hit) { $failAt = $Stopwatch.Elapsed.TotalSeconds; $script:TL_FailFastHit = $hit.Line.Trim(); break }
            }
        }
        if ($failAt -and $Stopwatch.Elapsed.TotalSeconds -ge $failAt + $grace) {
            Write-Host ('FAIL-FAST: stopping after the first failure; set ORICOOP_FAIL_FAST=0 for a full run. Failures so far:')
            foreach ($f in @($ResultFiles) + @($script:TL_LiveLogs)) {
                if (Test-Path -LiteralPath $f) { Select-String -LiteralPath $f -Pattern '\] FAIL |\] TEST FAIL ' -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ('  ' + (Split-Path -Leaf $f) + ': ' + ($_.Line -replace '^.*\] (TEST )?', '')) } }
            }
            Stop-TestGames
            break
        }
        Start-Sleep -Milliseconds 500
    }
    # A game still running when the time is up: the caller kills it. Remember it, so Get-NoResultReason says
    # "timed out" instead of "killed from outside" (2026-11-18: run-tracetest -Net passed -TimeoutSec 300 to a
    # ~10-minute run-nettest, and the result blamed an outside kill).
    if (@($Processes | Where-Object { -not $_.HasExited }).Count -gt 0 -and $Stopwatch.Elapsed.TotalSeconds -ge $TimeoutSec) { $script:TL_TimedOutSec = $TimeoutSec }
    return (@($Processes | Where-Object { -not $_.HasExited }).Count -eq 0)
}

# Call in finally: kills only this run's games, restores display prefs. The copy lock is NOT released here: the
# script still has to read its result files, and the next run would delete them (2026-11-17: 3 c-server runs lost).
# The script calls Exit-TestRun as its LAST step, after reading its results. If it dies before that, the lock goes
# stale as soon as this PowerShell exits (its pid is gone). A later script in the same PowerShell reuses it.
function Complete-TestRun {
    Stop-TestGames
    try { Remove-TestClientLinks } catch { Write-Host ('could not unlink client junctions: ' + $_) }
    Restore-OriDisplayPrefs
    if ($script:TL_FocusFixes -gt 0) { 'focus: ' + $script:TL_FocusFixes + ' focus steal(s) by test windows undone' }
}

# Last line of every test script: releases the copy lock once the results have been read.
function Exit-TestRun { Exit-CopyLock }

# Why a result file is missing, for the "no result file" line of a suite.
function Get-NoResultReason {
    if ($script:TL_FailFastHit) { return 'fail-fast stopped the run after: ' + ($script:TL_FailFastHit -replace '^.*\] (TEST )?', '') }
    if ($script:TL_TimedOutSec) { return 'the run timed out after ' + $script:TL_TimedOutSec + ' s and stopped its games (-TimeoutSec too short for this suite?)' }
    if ($script:TL_LockFile) {
        if (-not (Test-Path -LiteralPath $script:TL_LockFile)) { return 'the copy lock ' + $script:TL_LockFile + ' was removed during the run (taken over by another run?)' }
        $i = Read-LockInfo $script:TL_LockFile
        if ($i['pid'] -and $i['pid'] -ne [string]$PID) { return 'the copy lock was taken over during the run by ' + $i['session'] + ' (' + $i['script'] + ', ' + $i['time'] + ')' }
    }
    $crashed = @()
    foreach ($p in $script:TL_Procs) {
        $code = $null; try { $code = $p.ExitCode } catch { }
        if ($code -ne $null -and $code -ne 0 -and -not $script:TL_StoppedPids.Contains($p.Id)) { $crashed += ('pid ' + $p.Id + ' exit code ' + $code) }
    }
    if ($crashed.Count) { return 'a game crashed or was killed from outside (' + ($crashed -join ', ') + ')' }
    if ($script:TL_StoppedPids.Count) { return 'this run stopped its games (timeout) before they wrote results' }
    return 'the game exited without writing it (see the live logs: ' + (@($script:TL_LiveLogs) -join ', ') + ')'
}

# Sleep that keeps the watchdog running.
function Wait-TestSeconds([double]$Seconds) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $Seconds) { Invoke-TestWatch; Start-Sleep -Milliseconds 250 }
}
