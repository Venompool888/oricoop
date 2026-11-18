# Builds OriCoop.dll (C# 5, .NET 3.5 profile via the game's own mscorlib) and installs it into the test copy.
# -Game: an Ori DE install with BepInEx 5 (default: <repo>\game). Paths are relative to this script.
param([switch]$NoInstall, [string]$Game)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$game = if ($Game) { $Game } else { Join-Path $root 'game' }
$m = "$game\oriDE_Data\Managed"
$core = "$game\BepInEx\core"
$out = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $out | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$srcs = @(Get-ChildItem (Join-Path $PSScriptRoot 'OriCoop') -Filter *.cs -Recurse | ForEach-Object { $_.FullName })
$refs = @("$m\mscorlib.dll","$m\System.dll","$m\System.Core.dll","$m\UnityEngine.dll","$m\Assembly-CSharp.dll","$m\Assembly-CSharp-firstpass.dll","$m\J2i.Net.XInputWrapper.dll","$m\SteamworksManaged.dll","$core\BepInEx.dll","$core\0Harmony.dll") | ForEach-Object { "/r:$_" }
# Embedded pictures and text (an override file in the plugins folder wins at runtime; see DeadMarker.cs).
# Each one is optional: the plugin runs without it (no downed marker picture / no Chinese glyph atlas / no zh fixes).
$a = Join-Path $PSScriptRoot 'assets'
$res = @()
foreach ($r in @(@('dead-marker\dead_marker.png', 'OriCoop.dead_marker.png'),
                 @('zh-glyphs\zh_glyphs.png', 'OriCoop.zh_glyphs.png'),
                 @('zh-glyphs\zh_glyphs.txt', 'OriCoop.zh_glyphs.txt'),
                 @('zh-text\zh_terms.txt', 'OriCoop.zh_terms.txt'),
                 @('zh-text\zh_extra.txt', 'OriCoop.zh_extra.txt'))) {
    $p = Join-Path $a $r[0]
    if (Test-Path $p) { $res += "/resource:$p,$($r[1])" } else { Write-Host "note: $($r[0]) not found, not embedded" }
}
# Build info (T-032): the commit this DLL comes from, "+local" when src\OriCoop has uncommitted changes (Ui\BuildInfo.cs).
$sha = (git -C $root rev-parse --short HEAD 2>$null); if (-not $sha) { $sha = 'unknown' }
$dirty = (git -C $root status --porcelain -- src/OriCoop 2>$null); if ($dirty) { $sha = "$sha+local" }
$buildTxt = Join-Path $out 'build.txt'
[IO.File]::WriteAllText($buildTxt, "commit=$sha`n")
$res += "/resource:$buildTxt,OriCoop.build.txt"
& $csc /nologo /noconfig /nostdlib+ /target:library /optimize+ /langversion:5 /out:"$out\OriCoop.dll" @refs @res @srcs
if ($LASTEXITCODE -ne 0) { throw "csc failed" }
if ($NoInstall) { "BUILD OK (not installed)"; return }
New-Item -ItemType Directory -Force "$game\BepInEx\plugins" | Out-Null
Copy-Item "$out\OriCoop.dll" "$game\BepInEx\plugins\OriCoop.dll" -Force
"BUILD OK"
