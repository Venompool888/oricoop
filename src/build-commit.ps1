# Builds OriCoop.dll from a clean worktree of a commit (default HEAD).
# -Game selects an Ori DE installation with BepInEx 5. Output: <OutDir>\OriCoop.dll (default %TEMP%\oricoop-builds\<sha>).
# Prints the DLL path last, so: $env:ORICOOP_TEST_DLL = (& src\build-commit.ps1 abc1234 | Select-Object -Last 1)
param([string]$Commit = 'HEAD', [string]$OutDir = '', [string]$Game)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$sha = (git -C $repo rev-parse --short "$Commit").Trim()
if (-not $OutDir) { $OutDir = Join-Path $env:TEMP ('oricoop-builds\' + $sha) }
$dll = Join-Path $OutDir 'OriCoop.dll'
if (Test-Path -LiteralPath $dll) { Write-Host "already built: $sha"; $dll; return }
$wt = Join-Path $env:TEMP ('oricoop-wt-' + $sha + '-' + $PID)
$ErrorActionPreference = 'Continue'; & git -C $repo worktree add --detach $wt $sha *> $null; $ErrorActionPreference = 'Stop'
if (-not (Test-Path "$wt\src\OriCoop")) { throw "worktree for $sha failed" }
try {
    if (-not $Game) {
        $Game = Join-Path $repo 'game'
        if (-not (Test-Path "$Game\oriDE_Data\Managed")) { $Game = Join-Path (Split-Path -Parent (git -C $repo rev-parse --path-format=absolute --git-common-dir).Trim()) 'game' }
    }
    $gameDir = (Get-Item -LiteralPath $Game).FullName
    $m = "$gameDir\oriDE_Data\Managed"; $core = "$gameDir\BepInEx\core"
    $csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $refs = @("$m\mscorlib.dll", "$m\System.dll", "$m\System.Core.dll", "$m\UnityEngine.dll", "$m\Assembly-CSharp.dll", "$m\Assembly-CSharp-firstpass.dll",
        "$m\J2i.Net.XInputWrapper.dll", "$m\SteamworksManaged.dll", "$core\BepInEx.dll", "$core\0Harmony.dll") | ForEach-Object { "/r:$_" }
    $srcs = @(Get-ChildItem "$wt\src\OriCoop" -Filter *.cs -Recurse | ForEach-Object { $_.FullName })
    $a = "$wt\src\assets"
    $names = [ordered]@{ 'dead-marker\dead_marker.png' = 'OriCoop.dead_marker.png'; 'zh-glyphs\zh_glyphs.png' = 'OriCoop.zh_glyphs.png'
        'zh-glyphs\zh_glyphs.txt' = 'OriCoop.zh_glyphs.txt'; 'zh-text\zh_terms.txt' = 'OriCoop.zh_terms.txt'; 'zh-text\zh_extra.txt' = 'OriCoop.zh_extra.txt' }
    $res = @($names.Keys | Where-Object { Test-Path (Join-Path $a $_) } | ForEach-Object { '/resource:' + (Join-Path $a $_) + ',' + $names[$_] })
    foreach ($g in @('ps', 'xbox')) { foreach ($f in @(Get-ChildItem (Join-Path $a "$g-glyphs") -Filter *.png -ErrorAction SilentlyContinue)) { $res += "/resource:$($f.FullName),OriCoop.$($g)_$($f.Name)" } }   # T-178
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $buildTxt = Join-Path $OutDir 'build.txt'   # build info (T-032, Ui\BuildInfo.cs): a clean commit, no "+local"
    [IO.File]::WriteAllText($buildTxt, "commit=$sha`n")
    $res += "/resource:$buildTxt,OriCoop.build.txt"
    # The absolute paths for a clean checkout can exceed Windows' command-line limit.
    $response = Join-Path $wt 'build-commit.rsp'
    @('/nostdlib+', '/target:library', '/optimize+', '/langversion:5', "/out:$dll") + $refs + $res + $srcs |
        ForEach-Object { '"' + $_ + '"' } | Set-Content -LiteralPath $response -Encoding UTF8
    & $csc /nologo /noconfig "@$response"
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $dll)) { Remove-Item -LiteralPath $dll -ErrorAction SilentlyContinue; throw "csc failed for $sha" }
} finally { $ErrorActionPreference = 'Continue'; & git -C $repo worktree remove --force $wt *> $null }
Write-Host "built $sha"
$dll
