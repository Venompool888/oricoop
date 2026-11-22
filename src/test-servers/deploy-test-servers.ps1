# T-155: deploy committed server code (HEAD by default) to a remote test slot. -Dirty uses the working tree.
param([ValidateRange(0, 3)][int]$Slot = 0, [string]$Ref = 'HEAD',
    [ValidateSet('cserver', 'rooms')][string[]]$Services = @('cserver', 'rooms'), [switch]$Dirty)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestServers.ps1')
$config = Get-TestServersConfig
if (-not $config -or -not $config.SshHost) { throw ((Get-NoTestServerMessage) + ' (SshHost is needed to deploy)') }
if ($Dirty -and $Slot -eq 0) { throw 'Slot 0 is shared: deploy committed main code there, use -Slot 1-3 for -Dirty' }
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$commit = (& git -C $repo rev-parse --verify --short "$Ref^{commit}")
if ($LASTEXITCODE -ne 0) { throw "Unknown ref $Ref" }
if ($Slot -eq 0) {
    & git -C $repo merge-base --is-ancestor $commit main
    if ($LASTEXITCODE -ne 0) { throw 'Slot 0 is only for commits on main; use -Slot 1-3 for branches.' }
}
$label = $commit + $(if ($Dirty) { '+dirty' } else { '' })
$Services = @($Services | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique)
# Source directories used by CServer.csproj and RoomServer.csproj.
$paths = @('src/CServer', 'src/RoomServer', 'src/OriCoop/Authority', 'src/OriCoop/Net', 'src/OriCoop/Rooms')
$id = 'oricoop-test-' + $Slot + '-' + [Guid]::NewGuid().ToString('N')
$tmp = Join-Path $env:TEMP $id
$stage = Join-Path $tmp 'stage'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    $tar = Join-Path $tmp 'src.tar.gz'
    if ($Dirty) {
        & tar -czf $tar -C $repo --exclude=bin --exclude=obj --exclude=*.exe @paths
    } else {
        & git -C $repo archive --format=tar.gz -o $tar $commit -- @paths
    }
    if ($LASTEXITCODE -ne 0) { throw 'Packing the server source failed' }
    & tar -xzf $tar -C $stage
    if ($LASTEXITCODE -ne 0) { throw 'Extracting the server source failed' }
    $src = Join-Path $stage 'src'
    $csHash = Get-TestServerSourceHash 'CServer' $src
    $roomsHash = Get-TestServerSourceHash 'RoomServer' $src
    # Use the deploy tooling from this checkout, even when -Ref predates it.
    $tools = New-Item -ItemType Directory -Path (Join-Path $src 'test-servers')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'units') -Destination $tools.FullName -Recurse
    & tar -czf $tar -C $stage src
    if ($LASTEXITCODE -ne 0) { throw 'Packing the deployment failed' }
    $remoteTar = "/tmp/$id.tar.gz"
    $remoteScript = "/tmp/$id.sh"
    & scp -q -o BatchMode=yes $tar ("{0}:{1}" -f $config.SshHost, $remoteTar)
    if ($LASTEXITCODE -ne 0) { throw 'Source upload failed' }
    & scp -q -o BatchMode=yes (Join-Path $PSScriptRoot 'remote-deploy.sh') ("{0}:{1}" -f $config.SshHost, $remoteScript)
    if ($LASTEXITCODE -ne 0) { throw 'Script upload failed' }
    "Deploying $label to test slot $Slot ($($Services -join ', '))..."
    & ssh -o BatchMode=yes $config.SshHost "tr -d '\r' < $remoteScript > $remoteScript.lf && sh $remoteScript.lf $Slot $remoteTar '$label' '$($Services -join ' ')' $csHash $roomsHash; rc=`$?; rm -f $remoteScript $remoteScript.lf; exit `$rc"
    if ($LASTEXITCODE -ne 0) { throw "Deploy failed (exit $LASTEXITCODE)" }
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
if ($Services -contains 'rooms' -and $config.Host) {
    $tcp = New-Object Net.Sockets.TcpClient
    try { $tcp.Connect($config.Host, (Get-TestServerPort 'rooms' $Slot)); 'rooms reachable from this PC' }
    catch { Write-Warning 'rooms not reachable from this PC' }
    finally { $tcp.Close() }
}
