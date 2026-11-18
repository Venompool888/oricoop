# Builds OriCoop.TestQuiet.dll and installs it into the TEST COPY ONLY (<repo>\game). Never copy it elsewhere.
param([switch]$NoInstall)
$ErrorActionPreference = 'Stop'
$src = Split-Path -Parent $PSScriptRoot
$game = Join-Path (Split-Path -Parent $src) 'game'
# A worktree has no game folder: use the main checkout's (the parent of git's common dir), like build-commit.ps1.
if (-not (Test-Path "$game\oriDE_Data\Managed")) { $game = Join-Path (Split-Path -Parent (git -C $src rev-parse --path-format=absolute --git-common-dir).Trim()) 'game' }
$m = "$game\oriDE_Data\Managed"
$core = "$game\BepInEx\core"
$out = Join-Path $src 'bin'
New-Item -ItemType Directory -Force $out | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$refs = @("$m\mscorlib.dll", "$m\System.dll", "$m\System.Core.dll", "$m\UnityEngine.dll", "$m\Assembly-CSharp.dll", "$m\J2i.Net.XInputWrapper.dll", "$core\BepInEx.dll", "$core\0Harmony.dll") | ForEach-Object { "/r:$_" }
& $csc /nologo /noconfig /nostdlib+ /target:library /optimize+ /langversion:5 /out:"$out\OriCoop.TestQuiet.dll" @refs (Join-Path $PSScriptRoot 'TestQuiet.cs') (Join-Path $PSScriptRoot 'InputBlock.cs')
if ($LASTEXITCODE -ne 0) { throw 'csc failed' }
if ($NoInstall) { 'BUILD OK (not installed)'; return }
Copy-Item "$out\OriCoop.TestQuiet.dll" "$game\BepInEx\plugins\OriCoop.TestQuiet.dll" -Force
'BUILD OK (test copy only)'
