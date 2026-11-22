# T-178: screenshots of the PlayStation / Xbox button icons (Glyphs\GlyphProbe.cs). One game copy, title menus only
# (save slots, Options > Controls), icon set switched Xbox <-> PlayStation on each page.
# Pictures + result: C:\OriCoop\logs\glyphprobe\glyph_*.png, result.txt (last line PASS/FAIL).
param([int]$TimeoutSec = 180, [switch]$Force)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'test-launch.ps1')   # quiet launch + owner watchdog (docs\testing-rules.md)
Start-TestRun -Force:$Force
$dir = 'C:\OriCoop\logs\glyphprobe'
New-Item -ItemType Directory -Force $dir | Out-Null
Remove-Item "$dir\*" -Force -ErrorAction SilentlyContinue
# The old stand-alone OriPSGlyphs.dll (still in the test copies) would make OriCoop leave the icons alone: park it.
$old = Join-Path $TL_GameDir 'BepInEx\plugins\OriPSGlyphs.dll'; $parked = "$old.t178-parked"
if (Test-Path $old) { Move-Item -LiteralPath $old $parked -Force }
try {
    $vars = @{ LOCALAPPDATA = $TL_AppData[0]; ORICOOP_GLYPH_PROBE = '1'; ORICOOP_TEST_OUT = (Join-Path $dir 'result.txt') }
    $p = Start-TestGame -Arguments '-screen-fullscreen 0 -screen-width 1280 -screen-height 720' -Vars $vars -Tag 'glyphprobe'
    if (-not (Wait-TestGames @($p) $TimeoutSec)) { $p.Kill(); "TIMEOUT: game killed after $TimeoutSec s" }
}
finally { Complete-TestRun; if (Test-Path $parked) { Move-Item -LiteralPath $parked $old -Force } }
Get-Content (Join-Path $TL_GameDir 'BepInEx\LogOutput.log') | Select-String 'GLYPHPROBE|icons|pads:|PlayStation|Exception' | Select-Object -Last 40
Get-ChildItem $dir -Filter *.png | Select-Object Name, Length
Exit-TestRun   # release the test copy only now, after the results were read
