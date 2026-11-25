# Close a real game while it owns the fourth seat of a disposable local C-server room.
param([string]$Observer = $env:ORICOOP_QUIT_OBSERVER, [int]$TimeoutSec = 175)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'test-servers\LocalCServer.ps1')
$observerExe = Get-LocalCServerExecutable -Kind Test -Path $Observer
$dir = Join-Path ([IO.Path]::GetTempPath()) ('oricoop-quitseat-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $dir
$room = Join-Path $dir 'room.txt'
$stdout = Join-Path $dir 'observer.log'
$stderr = Join-Path $dir 'observer.err'
$previousRoom = $env:ORICOOP_QUIT_ROOM_FILE
$process = $null
try {
    $process = Start-Process -FilePath $observerExe -ArgumentList @('--game-quit', ('"' + $room + '"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $limit = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $room) -and -not $process.HasExited -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 50 }
    if (-not (Test-Path -LiteralPath $room)) { throw "The local observer did not open a room. See $dir" }
    $env:ORICOOP_QUIT_ROOM_FILE = $room
    $gameLog = Join-Path $dir 'game.log'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'run-test.ps1') -Scenarios quitseat -TimeoutSec $TimeoutSec | Tee-Object -FilePath $gameLog
    $gameExit = $LASTEXITCODE
    if ($gameExit -ne 0) { Write-Output "[quitseat] RESULT FAIL game launcher exited $gameExit"; exit $gameExit }
    if (-not (Select-String -LiteralPath $gameLog -Pattern '^\[[^\]]+\] RESULT PASS' -Quiet)) { throw "The game did not pass. See $gameLog" }
    if (-not $process.WaitForExit(25000)) { throw "The observer did not finish. See $dir" }
    Get-Content -LiteralPath $stdout, $stderr | ForEach-Object { Write-Output "[quitseat] $_" }
    if ($process.ExitCode -ne 0 -or -not (Select-String -LiteralPath $stdout -Pattern '^RESULT PASS' -Quiet)) { throw "The quit-seat check failed. See $dir" }
}
catch {
    Write-Output "[quitseat] RESULT FAIL $($_.Exception.Message)"
    throw
}
finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    if ($process) { $process.Dispose() }
    $env:ORICOOP_QUIT_ROOM_FILE = $previousRoom
    Write-Host "Quit-seat reports in $dir"
}
