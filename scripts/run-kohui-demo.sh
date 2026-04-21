#!/usr/bin/env bash
# Run the KohUI counter demo locally.
#
# Debug build (default): opens the native SDL window AND starts the
#   Kestrel-backed DOM dev preview on a localhost port. Both surfaces
#   share one Runner — a click in either reflects in the other. The
#   preview URL is printed in cyan; copy it into a browser, or pass
#   --open to have the script pop the default browser.
#
# Release build / publish: native-only. The preview channel is compiled
#   out of the AOT binary for size and to avoid exposing a localhost
#   server in a shipped app.
#
# Flags:
#   --preview      Run preview-only (no SDL window). Used by CI /
#                  Playwright and any headless environment.
#   --native-only  Suppress the preview even in a Debug build.
#   --open         Whichever mode is running, open the printed URL in
#                  the default browser. In --native-only mode no URL
#                  is printed, so --open is a no-op.
set -euo pipefail

PREVIEW=0
NATIVE_ONLY=0
OPEN=0
for arg in "$@"; do
    case "$arg" in
        --preview)     PREVIEW=1 ;;
        --native-only) NATIVE_ONLY=1 ;;
        --open)        OPEN=1 ;;
        --help|-h)
            sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
            exit 0
            ;;
    esac
done

cd "$(dirname "$0")/.."

CYAN=$'\033[1;36m'
RESET=$'\033[0m'
OPENED=0

open_url() {
    if   command -v xdg-open >/dev/null 2>&1; then xdg-open "$1"  &>/dev/null &
    elif command -v open     >/dev/null 2>&1; then open     "$1"  &>/dev/null &
    elif command -v cmd.exe  >/dev/null 2>&1; then cmd.exe /c start "" "$1" &>/dev/null &
    fi
}

CMD=(dotnet run --project samples/KohUI.Demo)
if   [[ $PREVIEW -eq 1 ]];     then CMD+=(-- --preview)
elif [[ $NATIVE_ONLY -eq 1 ]]; then CMD+=(-- --native)
fi

"${CMD[@]}" 2>&1 | while IFS= read -r line; do
    if [[ "$line" =~ (http://127\.0\.0\.1:[0-9]+) ]]; then
        url="${BASH_REMATCH[1]}"
        echo "${line/$url/$CYAN$url$RESET}"
        if [[ $OPEN -eq 1 && $OPENED -eq 0 ]]; then
            open_url "$url"
            OPENED=1
        fi
    else
        echo "$line"
    fi
done
