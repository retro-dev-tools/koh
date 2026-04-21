# Run the KohUI counter demo locally.
#
# Debug build (default): opens the native SDL window AND starts the
#   Kestrel-backed DOM dev preview on a localhost port. Both surfaces
#   share one Runner — a click in either reflects in the other. The
#   preview URL is printed in cyan; copy it into a browser, or pass
#   -Open to have the script pop the default browser.
#
# Release build / publish: native-only. The preview channel is compiled
#   out of the AOT binary for size and to avoid exposing a localhost
#   server in a shipped app.
#
# Flags:
#   -Preview     Run preview-only (no SDL window). Used by CI /
#                Playwright and any headless environment.
#   -NativeOnly  Suppress the preview even in a Debug build.
#   -Open        Implies nothing about -Preview — whichever mode is
#                running, open the printed URL in the default browser.
#                In -NativeOnly mode no URL is printed, so -Open is a
#                no-op.

[CmdletBinding()]
param(
    [switch]$Preview,
    [switch]$NativeOnly,
    [switch]$Open
)

$ErrorActionPreference = 'Stop'

# ANSI escape sequences — PowerShell 5.1 doesn't know about `e, so use
# the numeric char to keep the script portable to legacy hosts.
$esc   = [char]27
$cyan  = "$esc[1;36m"
$reset = "$esc[0m"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')

Push-Location $repoRoot
try {
    $dotnetArgs = @('run', '--project', 'samples/KohUI.Demo')
    if ($Preview)    { $dotnetArgs += '--';  $dotnetArgs += '--preview' }
    elseif ($NativeOnly) { $dotnetArgs += '--';  $dotnetArgs += '--native' }

    $opened = $false
    & dotnet $dotnetArgs 2>&1 | ForEach-Object {
        $line = [string]$_
        if ($line -match 'http://127\.0\.0\.1:\d+') {
            $url = $matches[0]
            $coloured = $line -replace [regex]::Escape($url), "$cyan$url$reset"
            Write-Host $coloured
            if ($Open -and -not $opened) {
                Start-Process $url
                $opened = $true
            }
        } else {
            Write-Host $line
        }
    }
}
finally {
    Pop-Location
}
