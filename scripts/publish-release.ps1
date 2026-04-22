# Promote a drafted Koh release to public. Release workflows create
# drafts by default; run this after verifying the draft assets so
# the release shows up on the public releases page and is reachable
# by the extension's auto-updater.
#
# Usage:  scripts\publish-release.ps1 <tag>
#   e.g.  scripts\publish-release.ps1 tools-v0.1.3-beta
#         scripts\publish-release.ps1 ext-v0.1.4

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Tag
)

$ErrorActionPreference = 'Stop'

$repo = 'retro-dev-tools/koh'

$isDraft = gh release view $Tag --repo $repo --json isDraft --jq .isDraft
if ($LASTEXITCODE -ne 0) { throw "release $Tag not found in $repo" }
if ($isDraft.Trim() -ne 'true') {
    throw "release $Tag is not a draft (already published)"
}

gh release edit $Tag --repo $repo --draft=$false
if ($LASTEXITCODE -ne 0) { throw "failed to publish $Tag" }

Write-Host "published $Tag" -ForegroundColor Green
Write-Host "  https://github.com/$repo/releases/tag/$Tag"
