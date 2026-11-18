# Screenshots of the online lobby + HUD (LobbyProbe.cs). Starts a local relay server, runs one game copy that walks
# the real menu flow, then stops both. Pictures: C:\OriCoop\logs\lobbyprobe\lobby_*.png
param([int]$TimeoutSec = 240, [switch]$Force)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'test-launch.ps1')   # quiet launch + owner watchdog (docs\testing-rules.md)
# LobbyProbe.cs uses the relay at 127.0.0.1:47020 (hard-coded), so this runs only in copy a.
Start-TestRun -Force:$Force -Copy a
$pristine = 'C:\OriCoop\save-backup\20261114-150138'
$testSaves = Join-Path $TL_AppData[0] 'Ori and the Blind Forest DE'
if (Test-Path $pristine) { Copy-Item "$pristine\*" $testSaves -Force }
$dir = 'C:\OriCoop\logs\lobbyprobe'
New-Item -ItemType Directory -Force $dir | Out-Null
Remove-Item "$dir\*" -Force -ErrorAction SilentlyContinue
$relay = Start-Process -FilePath (Get-TestServerExe 'RelayServer') -ArgumentList '47020' -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $dir 'relay-server.log')
try {
    $vars = @{ LOCALAPPDATA = $TL_AppData[0]; ORICOOP_LOBBY_PROBE = '1'; ORICOOP_SLOT = '1'
        ORICOOP_TEST_OUT = (Join-Path $dir 'result.txt'); ORICOOP_NET_RELAY = '127.0.0.1:47020' }
    $p = Start-TestGame -Arguments '-screen-fullscreen 0 -screen-width 1280 -screen-height 720' -Vars $vars -Tag 'lobbyprobe'
    if (-not (Wait-TestGames @($p) $TimeoutSec)) { $p.Kill(); "TIMEOUT: game killed after $TimeoutSec s" }
}
finally { if ($relay -and -not $relay.HasExited) { $relay | Stop-Process -Force -Confirm:$false }; Complete-TestRun }
Get-Content (Join-Path $TL_GameDir 'BepInEx\LogOutput.log') | Select-String 'LOBBYPROBE|online lobby|online co-op|net: relay|Exception' | Select-Object -Last 40
Get-ChildItem $dir -Filter *.png | Select-Object Name, Length
Exit-TestRun   # release the test copy only now, after the results were read
