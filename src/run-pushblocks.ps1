# Run the two-player packet-loss, seat and rejoin scenarios in their own game session.
param([switch]$CServer, [ValidateSet('PI', 'PR', 'PJ', 'PK')][string]$Scenario)
$previous = @($env:ORICOOP_PHASES, $env:ORICOOP_P_ONLY, $env:ORICOOP_P_COOP)
try {
    $env:ORICOOP_PHASES = 'P'
    $env:ORICOOP_P_ONLY = 'COOP'
    $env:ORICOOP_P_COOP = if ($Scenario) { $Scenario } elseif ($CServer) { 'PK' } else { 'PI,PR,PJ' }
    & (Join-Path $PSScriptRoot 'run-nettest.ps1') -CServer:$CServer
    $result = $LASTEXITCODE
} finally {
    $env:ORICOOP_PHASES, $env:ORICOOP_P_ONLY, $env:ORICOOP_P_COOP = $previous
}
exit $result
