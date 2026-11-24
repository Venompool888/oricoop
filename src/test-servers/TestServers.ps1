# T-155: remote integration-test instances, configured outside the repo (see README.md).
$script:TS_SourceRoot = Split-Path -Parent $PSScriptRoot
$script:TS_Offsets = @{ rooms = 1; cserver = 2 }
$script:TS_Projects = @{ rooms = 'RoomServer'; cserver = 'CServer' }

function Get-TestServersConfigPath {
    if ($env:ORICOOP_TEST_SERVERS_FILE) { return $env:ORICOOP_TEST_SERVERS_FILE }
    Join-Path $env:USERPROFILE '.oricoop\test-servers.psd1'
}

function Get-TestServersConfig {
    $path = Get-TestServersConfigPath
    if (-not (Test-Path -LiteralPath $path)) { return }
    # Import-PowerShellDataFile may be unavailable in Windows PowerShell children with an inherited PSModulePath.
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput((Get-Content -LiteralPath $path -Raw), [ref]$null, [ref]$errors)
    $data = $ast.Find({ $args[0] -is [System.Management.Automation.Language.HashtableAst] }, $false)
    if ($errors.Count -or -not $data) { throw 'Invalid test server configuration: expected a PowerShell data hashtable.' }
    $data.SafeGetValue()
}

function Get-NoTestServerMessage {
    'NO TEST SERVER: integration tests use remote test servers and never start a local one (T-155). Create ' +
        (Get-TestServersConfigPath) + " with @{ Host = '<address>'; SshHost = '<ssh alias>'; Slot = 0 } (src\test-servers\README.md)."
}

function Get-TestServerSlot($config) {
    $slot = $env:ORICOOP_TEST_SERVER_SLOT
    if (-not $slot -and $config) { $slot = [string]$config.Slot }
    if (-not $slot) { $slot = '0' }
    if ($slot -notmatch '^[0-3]$') { throw 'Test server slot must be 0-3 (ORICOOP_TEST_SERVER_SLOT or config Slot).' }
    return [int]$slot
}

function Get-TestServerPort([ValidateSet('cserver', 'rooms')][string]$kind, [ValidateRange(0, 3)][int]$slot) {
    47100 + 10 * $slot + $script:TS_Offsets[$kind]
}

function Get-TestServerOverride([string]$kind) {
    [Environment]::GetEnvironmentVariable('ORICOOP_TEST_' + $kind.ToUpperInvariant())
}

function Get-RemoteTestServer([ValidateSet('cserver', 'rooms')][string]$kind) {
    $override = Get-TestServerOverride $kind
    if ($override) { return $override }
    $config = Get-TestServersConfig
    if (-not $config -or -not $config.Host) { throw (Get-NoTestServerMessage) }
    '{0}:{1}' -f $config.Host, (Get-TestServerPort $kind (Get-TestServerSlot $config))
}

function Get-TestServerSources([ValidateSet('CServer', 'RoomServer')][string]$name, [string]$src = $script:TS_SourceRoot) {
    # T-088's source list, including the project files and CServerTest.cs compiled by CServer.csproj.
    $files = switch ($name) {
        'CServer' {
            # Keep the historical hash order for refs that predate join diagnostics.
            $baseline = @('CServer\CServer.cs', 'OriCoop\Authority\CsWire.cs', 'OriCoop\Authority\Arbiter.cs', 'OriCoop\Net\NetPeer.cs', 'OriCoop\Net\Transport.cs', 'CServer\CServerTest.cs', 'CServer\CServer.csproj')
            $baseline
            $project = Join-Path $src 'CServer\CServer.csproj'
            $known = @($baseline | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $src $_)) })
            foreach ($entry in ([xml](Get-Content -Raw -LiteralPath $project)).Project.ItemGroup.Compile) {
                $relative = Join-Path 'CServer' $entry.Include
                if ($known -notcontains [IO.Path]::GetFullPath((Join-Path $src $relative))) { $relative }
            }
        }
        'RoomServer' { @('RoomServer\RoomServer.cs', 'OriCoop\Rooms\RoomWire.cs', 'RoomServer\RoomServer.csproj') }
    }
    $files | ForEach-Object { Join-Path $src $_ }
}

function Get-TestServerSourceHash([ValidateSet('CServer', 'RoomServer')][string]$name, [string]$src = $script:TS_SourceRoot) {
    # T-088: SHA256 of each file name, LF and raw file bytes, in source-list order.
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = New-Object 'System.Collections.Generic.List[byte]'
        foreach ($f in Get-TestServerSources $name $src) {
            $bytes.AddRange([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFileName($f) + "`n"))
            $bytes.AddRange([IO.File]::ReadAllBytes($f))
        }
        (-join ($sha.ComputeHash($bytes.ToArray()) | ForEach-Object { $_.ToString('x2') })).Substring(0, 12)
    } finally { $sha.Dispose() }
}

function Assert-TestServerCurrent([ValidateSet('cserver', 'rooms')][string[]]$kinds) {
    $check = @(foreach ($kind in $kinds) {
        if (Get-TestServerOverride $kind) { Write-Host "TEST SERVER: $kind from env override, version not checked" }
        else { $kind.ToLowerInvariant() }
    })
    if (-not $check.Count) { return }
    $config = Get-TestServersConfig
    if (-not $config -or -not $config.Host) { throw (Get-NoTestServerMessage) }
    if (-not $config.SshHost) { Write-Warning 'TEST SERVER: no SshHost configured, version not checked'; return }
    $slot = Get-TestServerSlot $config
    $command = 'set -e; for svc in {0}; do f=/opt/oricoop/test/{1}/$svc.hash; h=missing; if [ -f $f ]; then h=$(cat $f); fi; printf ''%s %s\n'' $svc $h; done' -f ($check -join ' '), $slot
    $ssh = Get-Command ssh -ErrorAction Stop
    $lines = & {
        $ErrorActionPreference = 'Continue'  # Native stderr can contain the private address or alias.
        & $ssh -o BatchMode=yes -o ConnectTimeout=10 $config.SshHost $command 2>$null
    }
    if ($LASTEXITCODE -eq 255) { throw "TEST SERVER: SSH connection or authentication failed for slot $slot (exit 255). Check SshHost and SSH access." }
    if ($LASTEXITCODE -ne 0) { throw "TEST SERVER: SSH could not read slot $slot hash files (exit $LASTEXITCODE)." }
    $hashes = @{}
    foreach ($line in $lines) { if ($line -cmatch '^(cserver|rooms) ([0-9a-f]{12})$') { $hashes[$Matches[1]] = $Matches[2] } }
    foreach ($kind in $check) {
        $local = Get-TestServerSourceHash $script:TS_Projects[$kind]
        $remote = $hashes[$kind]
        if (-not $remote) { $remote = 'missing' }
        if ($remote -cne $local) {
            throw ('TEST SERVER STALE: slot {0} runs {1}, this checkout''s {2} source is {3}. Deploy it: src\test-servers\deploy-test-servers.ps1 -Slot <1-3> -Services {2}, then $env:ORICOOP_TEST_SERVER_SLOT = ''<slot>'' (slot 0 only from main).' -f $slot, $remote, $kind, $local)
        }
    }
}
