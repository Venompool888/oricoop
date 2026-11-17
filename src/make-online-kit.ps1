# Builds <repo>\dist\OriCoop-kit-v*.zip: BepInEx + the OriCoop mod, to unzip into another PC's own
# "Ori and the Blind Forest Definitive Edition" install folder (next to oriDE.exe). No game files, no saves.
param([switch]$WithGlyphs, [string]$Game)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$game = if ($Game) { $Game } else { Join-Path $root 'game' }
$stage = Join-Path $root 'dist\stage'
# The zip name carries the version: plugin version (Plugin.cs BepInPlugin), date and commit, so two PCs can
# compare kits at a glance. Example: OriCoop-kit-v0.2.0-20261115-1a2b3c4.zip
$ver = '0.0.0'
# Plugin.cs keeps it in `public const string Version = "x.y.z"` (older builds had it inline in BepInPlugin).
$m = Select-String -Path (Join-Path $PSScriptRoot 'OriCoop\Plugin.cs') -Pattern 'const string Version\s*=\s*"(\d+\.\d+\.\d+)"', 'BepInPlugin\([^)]*"(\d+\.\d+\.\d+)"\)' | Select-Object -First 1
if ($m) { $ver = $m.Matches[0].Groups[1].Value }
$commit = (& git -C $root rev-parse --short=7 HEAD).Trim()
$zip = (Join-Path $root 'dist\OriCoop-kit-v') + $ver + '-' + (Get-Date -Format 'yyyyMMdd') + '-' + $commit + '.zip'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$stage\BepInEx\core", "$stage\BepInEx\plugins", "$stage\BepInEx\config" | Out-Null
foreach ($f in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version') { Copy-Item "$game\$f" $stage }
Copy-Item "$game\BepInEx\core\*" "$stage\BepInEx\core" -Recurse
Copy-Item "$game\BepInEx\config\BepInEx.cfg" "$stage\BepInEx\config"
Copy-Item "$game\BepInEx\plugins\OriCoop.dll" "$stage\BepInEx\plugins"
if ($WithGlyphs -and (Test-Path "$game\BepInEx\plugins\OriPSGlyphs.dll")) { Copy-Item "$game\BepInEx\plugins\OriPSGlyphs.dll" "$stage\BepInEx\plugins" }
Copy-Item (Join-Path $root 'TWO-PC-TEST.md') "$stage\OriCoop-README-online.md"
# Relay server manager, opened from the game folder; it edits BepInEx\config\oricoop-relays.txt (RelayList.cs).
Copy-Item (Join-Path $PSScriptRoot 'assets\relay-manager\OriCoop-Relays.html') "$stage\OriCoop-Relays.html"
# Licence notices for the bundled BepInEx / HarmonyX / MonoMod / Mono.Cecil (LGPL-2.1 and MIT require them).
Copy-Item (Join-Path $PSScriptRoot 'assets\kit\THIRD-PARTY-LICENSES.txt') "$stage\THIRD-PARTY-LICENSES.txt"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
Remove-Item $stage -Recurse -Force
"KIT OK: $zip (" + [int]((Get-Item $zip).Length / 1KB) + " KB)"
