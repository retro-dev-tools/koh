$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

$dirs = @(
    'src/Koh.Emulator.App',
    'src/Koh.Emulator.Core',
    'src/Koh.Debugger',
    'src/Koh.Linker.Core'
)

$exts = @('.cs', '.razor', '.csproj', '.js', '.html', '.css', '.json')

$files = Get-ChildItem -Recurse -File -Path $dirs |
    Where-Object { $exts -contains $_.Extension } |
    Sort-Object FullName

$dotnetVersion = (dotnet --version 2>$null)
if (-not $dotnetVersion) { $dotnetVersion = 'unknown' }

$sb = [System.Text.StringBuilder]::new()
foreach ($file in $files) {
    $h = (Get-FileHash -Algorithm SHA256 -Path $file.FullName).Hash.ToLower()
    $rel = [System.IO.Path]::GetRelativePath($repoRoot, $file.FullName) -replace '\\', '/'
    [void]$sb.Append("$h  $rel`n")
}
[void]$sb.Append("dotnet:$dotnetVersion`n")

$bytes = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
$sha = [System.Security.Cryptography.SHA256]::Create()
$hashBytes = $sha.ComputeHash($bytes)
$hex = ($hashBytes | ForEach-Object { $_.ToString('x2') }) -join ''
Write-Output $hex
