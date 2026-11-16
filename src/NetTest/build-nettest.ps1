$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $output | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @(
    Get-ChildItem (Join-Path $root 'OriCoop\Net') -Filter *.cs | ForEach-Object { $_.FullName }
    Get-ChildItem $PSScriptRoot -Filter *.cs | ForEach-Object { $_.FullName }
)
$exe = Join-Path $output 'NetTest.exe'
& $csc /nologo /target:exe /optimize+ /langversion:5 /out:$exe @sources
if ($LASTEXITCODE -ne 0) { throw 'NetTest compilation failed' }
& $exe
exit $LASTEXITCODE
