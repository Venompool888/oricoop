# Builds and runs every module's unit tests (no game launch). Each src\Tests\<Module>\ folder has a
# sources.txt listing the production files it compiles against (paths relative to src\, one per line,
# e.g. OriCoop\Core\Seams.cs); every *.cs in the folder itself is the test code (drivers, stubs, fakes).
# The tests compile against the game's .NET 3.5 mscorlib only (no UnityEngine), into a console exe that
# must exit 0. Usage: src\run-unittests.ps1 [-Only Enemies] [-Game <Ori DE install>]
param([string]$Only, [string]$Game)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$game = if ($Game) { $Game } else { Join-Path $root 'game' }
$m = "$game\oriDE_Data\Managed"
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $PSScriptRoot 'bin\unittests'
New-Item -ItemType Directory -Force $out | Out-Null
$failed = @()
foreach ($dir in Get-ChildItem (Join-Path $PSScriptRoot 'Tests') -Directory) {
    if ($Only -and $dir.Name -ne $Only) { continue }
    $list = Join-Path $dir.FullName 'sources.txt'
    $prod = @()
    if (Test-Path $list) { $prod = @(Get-Content $list | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { Join-Path $PSScriptRoot $_.Trim() }) }
    $tests = @(Get-ChildItem $dir.FullName -Filter *.cs | ForEach-Object { $_.FullName })
    $exe = Join-Path $out ($dir.Name + 'Tests.exe')
    & $csc /nologo /noconfig /nostdlib+ /target:exe /langversion:5 /out:$exe "/r:$m\mscorlib.dll" "/r:$m\System.dll" "/r:$m\System.Core.dll" @prod @tests
    if ($LASTEXITCODE -ne 0) { Write-Host "UNITTEST $($dir.Name): BUILD FAILED"; $failed += $dir.Name; continue }
    & $exe
    if ($LASTEXITCODE -ne 0) { Write-Host "UNITTEST $($dir.Name): FAIL"; $failed += $dir.Name } else { Write-Host "UNITTEST $($dir.Name): PASS" }
}
if ($failed.Count) { Write-Host ("UNITTESTS FAILED: " + ($failed -join ', ')); exit 1 }
Write-Host 'UNITTESTS PASS'
