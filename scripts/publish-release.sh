#!/usr/bin/env bash
# Promote a drafted Koh release to public. Release workflows create
# drafts by default; run this after verifying the draft assets so
# the release shows up on the public releases page and is reachable
# by the extension's auto-updater.
#
# Usage: scripts/publish-release.sh <tag>
#   e.g. scripts/publish-release.sh tools-v0.1.3-beta
#        scripts/publish-release.sh ext-v0.1.4

set -euo pipefail

if [[ $# -ne 1 ]]; then
    echo "usage: $0 <tag>" >&2
    exit 1
fi

tag="$1"
repo="retro-dev-tools/koh"

if ! gh release view "$tag" --repo "$repo" --json isDraft --jq .isDraft | grep -q true; then
    echo "release $tag is not a draft (already published, or doesn't exist)" >&2
    exit 1
fi

gh release edit "$tag" --repo "$repo" --draft=false
echo "published $tag"
echo "  https://github.com/$repo/releases/tag/$tag"
