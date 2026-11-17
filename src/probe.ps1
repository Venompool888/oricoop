# Launches the test copy windowed for N seconds, screenshots the game window, then closes it.
# Quiet launch (test-launch.ps1): the window stays behind everything, so the picture is taken with PrintWindow
# (PW_RENDERFULLCONTENT), not by copying the screen. Never closes a game it did not start.
param([switch]$NoBepInEx, [switch]$RealAppData, [int]$Seconds = 45, [string]$Tag = 'probe')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'test-launch.ps1')
Add-Type -AssemblyName System.Drawing
if (-not ('ProbeShot' -as [type])) {
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class ProbeShot { [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags); }
'@
}
Start-TestRun
$vars = @{}
if (-not $RealAppData) { $vars['LOCALAPPDATA'] = $TL_AppData[0] }
if ($NoBepInEx) { $vars['DOORSTOP_DISABLE'] = 'TRUE' }
try {
  $p = Start-TestGame -Arguments '-screen-fullscreen 0 -screen-width 960 -screen-height 540' -Vars $vars -Tag $Tag
  Wait-TestSeconds $Seconds
  $p.Refresh()
  if (-not $p.HasExited) {
    $h = @([OriTestLaunch5]::Windows($p.Id))[0]
    $r = New-Object OriTestLaunch5+RECT; [void][OriTestLaunch5]::GetWindowRect($h, [ref]$r)
    $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
    if ($w -gt 0 -and $hh -gt 0) {
      $bmp = New-Object System.Drawing.Bitmap $w, $hh
      $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
      [void][ProbeShot]::PrintWindow($h, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
      $png = Join-Path $TL_TestDir "$Tag.png"; $bmp.Save($png); "screenshot $png ($w x $hh)"
    } else { "no window rect" }
  } else { "game exited early, code $($p.ExitCode)" }
} finally { Complete-TestRun }
Exit-TestRun   # release the test copy only now, after the results were read
