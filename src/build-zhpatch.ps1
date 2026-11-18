# Standalone Chinese patch: compile shared localisation sources, never the co-op plugin.
param([switch]$NoInstall, [string]$Game)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$game = if ($Game) { $Game } else { Join-Path $root 'game' }
$m = "$game\oriDE_Data\Managed"
$core = "$game\BepInEx\core"
$out = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $out | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$srcs = @('ZhPatch\ZhPatchPlugin.cs', 'OriCoop\LanguageUnlock.cs', 'OriCoop\FontGlyphs.cs', 'OriCoop\ZhFallback.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$refs = @("$m\mscorlib.dll","$m\System.dll","$m\System.Core.dll","$m\UnityEngine.dll","$m\Assembly-CSharp.dll","$m\Assembly-CSharp-firstpass.dll","$m\J2i.Net.XInputWrapper.dll","$m\SteamworksManaged.dll","$core\BepInEx.dll","$core\0Harmony.dll") | ForEach-Object { "/r:$_" }
$res = @()
foreach ($r in @(@('zh-glyphs\zh_glyphs.png', 'OriCoop.zh_glyphs.png'),
                 @('zh-glyphs\zh_glyphs.txt', 'OriCoop.zh_glyphs.txt'),
                 @('zh-text\zh_terms.txt', 'OriCoop.zh_terms.txt'),
                 @('zh-text\zh_extra.txt', 'OriCoop.zh_extra.txt'))) {
    $p = Join-Path (Join-Path $PSScriptRoot 'assets') $r[0]
    # The glyph atlas is required. zh-text (game-derived text, see GAME-ASSETS-NOTICE.md) is optional, as in build.ps1:
    # without it the patch still works, only the fallback translations and term fixes are missing.
    if (Test-Path -LiteralPath $p) { $res += "/resource:$p,$($r[1])" }
    elseif ($r[0] -like 'zh-text\*') { Write-Host "note: $($r[0]) not found, not embedded" }
    else { throw "Required Chinese patch resource missing: $p" }
}
& $csc /nologo /noconfig /nostdlib+ /target:library /optimize+ /langversion:5 /define:ZHPATCH /out:"$out\OriZhPatch.dll" @refs @res @srcs
if ($LASTEXITCODE -ne 0) { throw 'csc failed' }
if ($NoInstall) { 'BUILD OK (not installed)'; return }
New-Item -ItemType Directory -Force "$game\BepInEx\plugins" | Out-Null
Copy-Item "$out\OriZhPatch.dll" "$game\BepInEx\plugins\OriZhPatch.dll" -Force
'BUILD OK'
