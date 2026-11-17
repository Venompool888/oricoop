# Live, opt-in standalone patch probe. Uses only a locked test copy; never touches the owner's game.
param([int]$TimeoutSec = 120, [ValidateSet('', 'a', 'b', 'c')][string]$Copy = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'test-launch.ps1')
# Do not let Start-TestRun overwrite the copy's plugins before we can back them up.
function Install-TestPlugins { }
$backup = $null
$moved = @()
$installed = @()
$cfg = $null
$cfgBefore = $null
$cfgSaved = $false
$out = $null
# The watchdog calls Exit-CopyLock before exiting. Defer release until our finally restores plugins.
$releaseLock = (Get-Command Exit-CopyLock).ScriptBlock
function Exit-CopyLock { }
try {
    Start-TestRun -Copy $Copy
    & (Join-Path $PSScriptRoot 'build-zhpatch.ps1') -NoInstall -Game $TL_GameDir
    $plugins = Join-Path $TL_GameDir 'BepInEx\plugins'
    $backup = Join-Path $TL_RuntimeDir ('zhpatch-backup-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $backup | Out-Null
    $quiet = Join-Path $plugins 'OriCoop.TestQuiet.dll'
    $quietSource = if (Test-Path -LiteralPath $quiet) { $quiet } else { Join-Path $TL_Root 'src\bin\OriCoop.TestQuiet.dll' }
    if (-not (Test-Path -LiteralPath $quietSource)) { throw 'TestQuiet is required for muted, Steam-free testing; build src\TestQuiet\build-testquiet.ps1 first.' }
    # Keep this test-only helper under a non-OriCoop filename. It contains no NetPlay/co-op plugin.
    Copy-Item -LiteralPath $quietSource (Join-Path $backup 'testquiet-helper.dll')
    foreach ($dll in @(Get-ChildItem -LiteralPath $plugins -Filter *.dll -Recurse | Where-Object {
        $_.Name -like 'OriCoop*.dll' -or $_.Name -eq 'OriZhPatch.dll' -or $_.Name -eq 'ZhPatchTestQuiet.dll'
    })) {
        $relative = $dll.FullName.Substring($plugins.Length).TrimStart('\')
        $saved = Join-Path (Join-Path $backup 'plugins') $relative
        New-Item -ItemType Directory -Force (Split-Path -Parent $saved) | Out-Null
        Move-Item -LiteralPath $dll.FullName -Destination $saved
        $moved += @{ Original = $dll.FullName; Saved = $saved }
    }
    $patch = Join-Path $plugins 'OriZhPatch.dll'
    $helper = Join-Path $plugins 'ZhPatchTestQuiet.dll'
    $installed += $patch
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin\OriZhPatch.dll') -Destination $patch
    $installed += $helper
    Copy-Item -LiteralPath (Join-Path $backup 'testquiet-helper.dll') -Destination $helper
    $cfg = Join-Path $TL_GameDir 'BepInEx\config\oricoop.zhpatch.cfg'
    if (Test-Path -LiteralPath $cfg) { $cfgBefore = [IO.File]::ReadAllBytes($cfg) }
    $cfgSaved = $true
    Remove-Item -LiteralPath $cfg -Force -ErrorAction SilentlyContinue
    $out = Join-Path $TL_TestDir 'zhpatch-result.txt'
    Remove-Item -LiteralPath $out -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $TL_TestDir 'zhpatch_title_zh.png') -Force -ErrorAction SilentlyContinue
    $vars = @{
        LOCALAPPDATA = $TL_AppData[0]; ORICOOP_ZHPATCH_PROBE = '1'; ORICOOP_TEST_OUT = $out
        ORICOOP_TEST = ''; ORICOOP_FORCE_ZH = ''; ORICOOP_LANG_PROBE = ''; ORICOOP_ZH_SCAN = ''
        ORICOOP_TIMESCALE = ''; ORICOOP_FPS_LOG = ''
    }
    $unityLog = Join-Path $TL_TestDir 'zhpatch-unity.log'
    $p = Start-TestGame -Arguments ("-screen-fullscreen 0 -screen-width 960 -screen-height 540 -logFile `"$unityLog`"") -Vars $vars -Tag 'zhpatch'
    if (-not (Wait-TestGames @($p) $TimeoutSec $null @($out))) { throw "TIMEOUT: standalone Chinese probe did not quit within $TimeoutSec seconds" }
    if (-not (Test-Path -LiteralPath $out)) { throw ('No probe result: ' + (Get-NoResultReason)) }
    $result = Get-Content -LiteralPath $out -Encoding UTF8
    # Host output remains visible even when a caller captures this script's success stream.
    foreach ($line in $result) { Write-Host $line }
    if (-not ($result -match '^RESULT PASS [0-9]+/[0-9]+$')) { throw 'Standalone Chinese patch probe failed' }
}
finally {
    try { Complete-TestRun }
    finally {
        try {
            foreach ($path in $installed) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force } }
            foreach ($file in $moved) { Move-Item -LiteralPath $file.Saved -Destination $file.Original -Force }
            if ($cfgSaved) {
                if ($cfgBefore -ne $null) { [IO.File]::WriteAllBytes($cfg, $cfgBefore) }
                elseif (Test-Path -LiteralPath $cfg) { Remove-Item -LiteralPath $cfg -Force }
            }
            if ($backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
        }
        finally {
            Set-Item -Path Function:Exit-CopyLock -Value $releaseLock
            Exit-TestRun
        }
    }
}
