# Run the KohUI counter demo locally.
#
# The server prints its http://127.0.0.1:PORT URL on startup — open it in
# your browser. Ctrl-C to stop.
#
# Flags:
#   -Open     After the server is ready, auto-launch the system default
#             browser at the printed URL.

[CmdletBinding()]
param(
    [switch]$Open
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')

Push-Location $repoRoot
try {
    if (-not $Open) {
        dotnet run --project samples/KohUI.Demo
        return
    }

    # Stream the server's stdout; match the listen URL and launch the
    # default browser exactly once.
    $opened = $false
    dotnet run --project samples/KohUI.Demo 2>&1 | ForEach-Object {
        $line = $_
        $line
        if (-not $opened -and $line -match 'http://127\.0\.0\.1:\d+') {
            Start-Process $matches[0]
            $opened = $true
        }
    }
}
finally {
    Pop-Location
}
