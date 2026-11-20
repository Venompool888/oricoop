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
# T-091: the DLL is a clean build of THIS commit (build-commit.ps1), never the test copy's plugins folder: any test
# session installs its own branch build there (a kit named 077a18d once carried be474be, an older menu).
$dll = (& (Join-Path $PSScriptRoot 'build-commit.ps1') $commit | Select-Object -Last 1)
if (-not (Test-Path -LiteralPath $dll)) { throw "build of $commit failed: no DLL at '$dll'" }
Copy-Item -LiteralPath $dll "$stage\BepInEx\plugins\OriCoop.dll"
if ($WithGlyphs -and (Test-Path "$game\BepInEx\plugins\OriPSGlyphs.dll")) { Copy-Item "$game\BepInEx\plugins\OriPSGlyphs.dll" "$stage\BepInEx\plugins" }
Copy-Item (Join-Path $root 'TWO-PC-TEST.md') "$stage\OriCoop-README-online.md"
# Licence notices for the bundled BepInEx / HarmonyX / MonoMod / Mono.Cecil (LGPL-2.1 and MIT require them).
Copy-Item (Join-Path $PSScriptRoot 'assets\kit\THIRD-PARTY-LICENSES.txt') "$stage\THIRD-PARTY-LICENSES.txt"
# The DLL embeds a few game-derived items (marker image, zh text); this notice says whose they are.
Copy-Item (Join-Path (Split-Path -Parent $PSScriptRoot) 'public\GAME-ASSETS-NOTICE.md') "$stage\GAME-ASSETS-NOTICE.md"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
Remove-Item $stage -Recurse -Force
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'check-kit.ps1') $zip
if ($LASTEXITCODE -ne 0) { Remove-Item $zip -Force -ErrorAction SilentlyContinue; throw "kit check failed: $zip removed" }
"KIT OK: $zip (" + [int]((Get-Item $zip).Length / 1KB) + " KB)"
