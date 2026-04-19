#!/usr/bin/env bash
# Run the KohUI counter demo locally.
#
# The server prints its http://127.0.0.1:PORT URL on startup — open it in
# your browser. Ctrl-C to stop.
#
# Flags:
#   --open    After the server is ready, auto-launch the system default
#             browser at the printed URL.
set -euo pipefail

OPEN=0
for arg in "$@"; do
    case "$arg" in
        --open) OPEN=1 ;;
        --help|-h)
            sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'
            exit 0
            ;;
    esac
done

cd "$(dirname "$0")/.."

if [[ $OPEN -eq 0 ]]; then
    exec dotnet run --project samples/KohUI.Demo
fi

# Tee mode: watch stdout for the listen URL, fork a browser open.
dotnet run --project samples/KohUI.Demo 2>&1 | while IFS= read -r line; do
    echo "$line"
    if [[ $OPEN -eq 1 ]] && [[ "$line" =~ (http://127\.0\.0\.1:[0-9]+) ]]; then
        URL="${BASH_REMATCH[1]}"
        if   command -v xdg-open >/dev/null 2>&1; then xdg-open "$URL"  &>/dev/null &
        elif command -v open     >/dev/null 2>&1; then open     "$URL"  &>/dev/null &
        elif command -v cmd.exe  >/dev/null 2>&1; then cmd.exe /c start "" "$URL" &>/dev/null &
        fi
        OPEN=0   # only once per run
    fi
done
