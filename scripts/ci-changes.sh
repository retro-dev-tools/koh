#!/usr/bin/env bash
# Reads changed paths on stdin; prints area=true|false lines for $GITHUB_OUTPUT.
# Fail-safe: no input, or any path no rule recognizes, selects every area.
set -euo pipefail

areas=(rust references dotnet emulator vscode sample)
declare -A on
for area in "${areas[@]}"; do on[$area]=false; done
all=false
seen=0

while IFS= read -r path; do
  [[ -z "$path" ]] && continue
  seen=1
  case "$path" in
    .github/* | .config/* | Cargo.toml | Cargo.lock | global.json | Directory.* | Koh.* \
      | build.proj | rust-toolchain* | scripts/ci-changes.sh)
      all=true ;;
    licenses/*)
      on[rust]=true; on[emulator]=true ;;
    docs/* | .claude/* | *.md | *.png | LICENSE*)
      ;;
    editors/vscode/*)
      on[vscode]=true ;;
    rust/koh-asm/* | rust/koh-link/* | rust/koh-objects/*)
      on[rust]=true; on[references]=true; on[dotnet]=true; on[emulator]=true; on[sample]=true ;;
    rust/*)
      on[rust]=true; on[dotnet]=true; on[emulator]=true ;;
    src/Asm/Koh.Lsp/* | src/Asm/Koh.Native/*)
      on[rust]=true; on[dotnet]=true; on[emulator]=true ;;
    samples/asm/gb-2048/*)
      on[dotnet]=true; on[emulator]=true; on[sample]=true ;;
    src/* | tests/* | samples/* | tools/*)
      on[dotnet]=true; on[emulator]=true ;;
    scripts/stage-rust-notices.py)
      on[rust]=true ;;
    scripts/verify-rust-assembler.py)
      on[references]=true ;;
    scripts/verify-gpui* | scripts/test-verify-gpui*)
      on[emulator]=true ;;
    *)
      all=true ;;
  esac
done

[[ $seen -eq 0 ]] && all=true
for area in "${areas[@]}"; do
  [[ $all == true ]] && on[$area]=true
  echo "$area=${on[$area]}"
done
