#!/usr/bin/env bash
# Fetch and unpack the AliceInCradle Windows build.
#   download_game.sh <url> <work dir>
# Prints the game root directory (the folder containing AliceInCradle_Data).
set -euo pipefail
URL=$1
WORK=$(realpath -m "$2")
mkdir -p "$WORK"
ARCHIVE="$WORK/game.archive"
if [[ "$URL" == *"ug.link/"* ]]; then
  npm install --no-save --prefix "$WORK/node" playwright@1.48.2 >/dev/null
  (cd "$WORK/node" && npx playwright install --with-deps chromium >/dev/null)
  NODE_PATH="$WORK/node/node_modules" node "$(dirname "$0")/ugreen_download.js" "$URL" "$ARCHIVE" >&2
else
  curl -fL --retry 4 --retry-delay 5 -o "$ARCHIVE" "$URL" >&2
fi
ls -la "$ARCHIVE" >&2
mkdir -p "$WORK/game"
7z x -y -o"$WORK/game" "$ARCHIVE" >/dev/null
rm -f "$ARCHIVE"
DATA=$(find "$WORK/game" -maxdepth 4 -type d -name '*_Data' | head -1)
[ -n "$DATA" ] || { echo "no *_Data folder found in archive" >&2; exit 1; }
dirname "$DATA"
