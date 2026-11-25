# Resolve the local binaries used by the isolated game tests. The public server builds them in its own repository.
function Get-LocalCServerExecutable {
    param([Parameter(Mandatory)][ValidateSet('Test','Server')][string]$Kind, [string]$Path)
    if ($Path) {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Local C-server executable not found: $Path" }
        return (Resolve-Path -LiteralPath $Path).Path
    }

    $src = Split-Path -Parent $PSScriptRoot
    $project = Join-Path $src 'CServer'
    $name = if ($Kind -eq 'Test') { 'CServerTest.exe' } else { 'CServer.exe' }
    $target = Join-Path $project $name
    $build = Join-Path $project 'build-cserver.ps1'
    if (-not (Test-Path -LiteralPath $build -PathType Leaf)) {
        if (Test-Path -LiteralPath $target -PathType Leaf) { return $target }
        $where = if ($Kind -eq 'Test') { 'pass tests\CServerTest.exe with -Observer or ORICOOP_QUIT_OBSERVER' } else { 'set ORICOOP_LOCAL_CSERVER_EXE to server\CServer.exe' }
        throw "Build the companion oricoop-server repository with its build-cserver.ps1, then $where."
    }

    $mutex = New-Object Threading.Mutex($false, 'OriCoopLocalCServerBuild')
    try {
        try { [void]$mutex.WaitOne() } catch [Threading.AbandonedMutexException] { }
        try {
            if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
                & powershell -NoProfile -ExecutionPolicy Bypass -File $build | Out-Host
                if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Failed to build $target." }
            }
        } finally { $mutex.ReleaseMutex() }
    } finally { $mutex.Dispose() }
    return $target
}
