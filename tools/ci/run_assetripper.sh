#!/usr/bin/env bash
# Export the Windows build as a Unity project with AssetRipper (headless).
#   run_assetripper.sh <AssetRipper dir> <game root (folder with the .exe)> <output dir>
# Result: <output dir>/ExportedProject/{Assets,ProjectSettings}
set -euo pipefail
AR_DIR=$1
GAME_DIR=$(realpath "$2")
OUT_DIR=$(realpath -m "$3")
PORT=${ASSETRIPPER_PORT:-8899}
URL="http://127.0.0.1:$PORT"

chmod +x "$AR_DIR/AssetRipper.GUI.Free"
"$AR_DIR/AssetRipper.GUI.Free" --headless --port "$PORT" --log true --log-path "$OUT_DIR.log" &
AR_PID=$!
trap 'kill $AR_PID 2>/dev/null || true' EXIT

for _ in $(seq 1 90); do
  if curl -sf "$URL/" >/dev/null; then break; fi
  sleep 2
done
curl -sf "$URL/" >/dev/null || { echo "AssetRipper did not start"; exit 1; }

# Settings must be set before loading. Boolean settings absent from the form are set to false.
curl -sf -o /dev/null -X POST "$URL/Settings/Update" \
  --data-urlencode "ScriptExportMode=DllExportWithoutRenaming" \
  --data-urlencode "ShaderExportMode=Dummy" \
  --data-urlencode "BundledAssetsExportMode=DirectExport" \
  --data-urlencode "StreamingAssetsMode=Extract" \
  --data-urlencode "ImageExportFormat=Png" \
  --data-urlencode "SpriteExportMode=Yaml" \
  --data-urlencode "TextExportMode=Parse" \
  --data-urlencode "AudioExportFormat=Default" \
  --data-urlencode "EnableStaticMeshSeparation=on"

echo "[assetripper] loading $GAME_DIR"
time curl -sf -o /dev/null -X POST "$URL/LoadFolder" --data-urlencode "Path=$GAME_DIR"
echo "[assetripper] exporting to $OUT_DIR"
rm -rf "$OUT_DIR"
time curl -sf -o /dev/null -X POST "$URL/Export/UnityProject" --data-urlencode "Path=$OUT_DIR"

test -d "$OUT_DIR/ExportedProject/Assets" || { echo "export failed"; tail -100 "$OUT_DIR.log" || true; exit 1; }
du -sh "$OUT_DIR/ExportedProject/Assets"
find "$OUT_DIR/ExportedProject/Assets" -maxdepth 2 -type d | head -50
