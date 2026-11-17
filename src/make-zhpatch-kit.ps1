# Standalone Chinese kit: loader + OriZhPatch only. Never copies game assemblies, assets or saves.
param([string]$Game)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$game = if ($Game) { $Game } else { Join-Path $root 'game' }
& (Join-Path $PSScriptRoot 'build-zhpatch.ps1') -NoInstall -Game $game
$commit = (& git -C $root rev-parse --short=7 HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine kit commit' }
$dist = Join-Path $root 'dist'
$zip = Join-Path $dist ('OriZhPatch-v1.0.0-' + (Get-Date -Format 'yyyyMMdd') + '-' + $commit + '.zip')
$stage = Join-Path $dist ('stage-zhpatch-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force "$stage\BepInEx\core", "$stage\BepInEx\plugins", "$stage\BepInEx\config" | Out-Null
    # Same BepInEx 5 bootstrap files and notices as make-online-kit.ps1; no OriCoop plugins or menus.
    foreach ($f in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version') { Copy-Item -LiteralPath "$game\$f" -Destination $stage }
    Copy-Item "$game\BepInEx\core\*" "$stage\BepInEx\core" -Recurse
    Copy-Item -LiteralPath "$game\BepInEx\config\BepInEx.cfg" -Destination "$stage\BepInEx\config"
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin\OriZhPatch.dll') -Destination "$stage\BepInEx\plugins"
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets\kit\THIRD-PARTY-LICENSES.txt') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $root 'public\GAME-ASSETS-NOTICE.md') -Destination $stage
    Copy-Item -Path (Join-Path $PSScriptRoot 'assets\zhpatch-kit\*') -Destination $stage
    # The source README keeps an ASCII name (git ls-tree quotes non-ASCII paths, which the public sync skips);
    # in the kit it gets its Chinese name (README + "simplified Chinese patch"), spelled with char codes so the script stays ASCII.
    $zhName = 'README-' + (-join ([char[]](0x7B80, 0x4F53, 0x4E2D, 0x6587, 0x8865, 0x4E01))) + '.txt'
    Rename-Item -LiteralPath (Join-Path $stage 'README-zh-CN.txt') -NewName $zhName
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
    "KIT OK: $zip (" + [int]((Get-Item -LiteralPath $zip).Length / 1KB) + ' KB)'
    # Emit the actual archive manifest so the release can be checked without launching the game.
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try { $archive.Entries | Sort-Object FullName | ForEach-Object { $_.FullName } }
    finally { $archive.Dispose() }
}
finally { if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force } }
