#!/usr/bin/env bash
# Run the Unity editor (with iOS build support) in the game-ci docker image.
#   unity_docker.sh <work dir mounted at /work> <unity args...>
#
# Licensing (environment):
#   UNITY_LICENSE                     contents of a Unity_lic.ulf (Personal or Pro), or
#   UNITY_SERIAL + UNITY_EMAIL + UNITY_PASSWORD   serial activation (returned afterwards)
set -euo pipefail
WORK=$(realpath "$1"); shift
IMAGE=${UNITY_IMAGE:-unityci/editor:ubuntu-2022.3.62f2-ios-3.2.2}

ARGS=""
for a in "$@"; do ARGS+=" $(printf '%q' "$a")"; done

cat > "$WORK/.unity_run.sh" <<EOS
#!/usr/bin/env bash
set -uo pipefail
mkdir -p /root/.local/share/unity3d/Unity
if [ -n "\${UNITY_LICENSE:-}" ]; then
  printf '%s' "\$UNITY_LICENSE" > /root/.local/share/unity3d/Unity/Unity_lic.ulf
elif [ -n "\${UNITY_SERIAL:-}" ]; then
  unity-editor -batchmode -nographics -quit -logFile /dev/stdout \
    -serial "\$UNITY_SERIAL" -username "\$UNITY_EMAIL" -password "\$UNITY_PASSWORD" || exit 1
else
  echo "No Unity license configured (UNITY_LICENSE or UNITY_SERIAL secrets)"; exit 1
fi
# package sources of built-in packages (needed to remap script references)
if [ ! -d /work/builtin_packages ]; then
  cp -r /opt/unity/Editor/Data/Resources/PackageManager/BuiltInPackages /work/builtin_packages || true
fi
unity-editor -batchmode -nographics $ARGS
code=\$?
if [ -n "\${UNITY_SERIAL:-}" ]; then
  unity-editor -batchmode -nographics -quit -returnlicense -username "\$UNITY_EMAIL" -password "\$UNITY_PASSWORD" || true
fi
exit \$code
EOS
chmod +x "$WORK/.unity_run.sh"

code=0
docker run --rm \
  -v "$WORK":/work -w /work \
  -e UNITY_LICENSE -e UNITY_SERIAL -e UNITY_EMAIL -e UNITY_PASSWORD \
  "$IMAGE" /work/.unity_run.sh || code=$?
sudo chown -R "$(id -u):$(id -g)" "$WORK" || true
exit $code
