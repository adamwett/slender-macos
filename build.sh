#!/usr/bin/env bash
# Builds a native Apple Silicon Slender.app from your own copy of
# Slender: The Eight Pages v0.9.6 (Windows).
#
# usage: ./build.sh /path/to/Slender      (the folder that contains Slender_Data)
set -euo pipefail

UNITY_VERSION=2022.3.62f3
ASSETRIPPER_VERSION=1.3.14
ASSETRIPPER_URL=https://github.com/AssetRipper/AssetRipper/releases/download/$ASSETRIPPER_VERSION/AssetRipper_mac_arm64.zip
ASSETRIPPER_SHA256=680890b97c58be02075438814b3f41ec2a928ae0be8e94fa24ad51faa81078aa

ROOT=$(cd "$(dirname "$0")" && pwd)
CACHE=$ROOT/.cache
WORK=$ROOT/work
LOGS=$WORK/logs
PROJ=$WORK/Slender
DIST=$ROOT/dist

die() { echo "error: $*" >&2; exit 1; }
step() { printf '\n==> %s\n' "$*"; }

[ $# -eq 1 ] || { echo "usage: $0 /path/to/Slender   (the folder that contains Slender_Data)" >&2; exit 2; }
[ -d "$1/Slender_Data" ] || die "'$1' has no Slender_Data folder"
GAME=$(cd "$1" && pwd)

[ "$(uname -s)" = Darwin ] && [ "$(uname -m)" = arm64 ] || die "this builds on Apple silicon macOS only"

UNITY=${UNITY:-/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity}
[ -x "$UNITY" ] || die "Unity $UNITY_VERSION not found at $UNITY
  Install it from Unity Hub (unityhub://$UNITY_VERSION/96770f904ca7), or set UNITY=/path/to/Unity.app/Contents/MacOS/Unity"

step "Verifying game files"
(cd "$GAME" && shasum -a 256 --quiet -c "$ROOT/game.sha256") \
    || die "these are not the Slender v0.9.6 Windows files this build was made for"

step "Fetching AssetRipper $ASSETRIPPER_VERSION"
AR_DIR=$CACHE/AssetRipper-$ASSETRIPPER_VERSION
AR=$AR_DIR/AssetRipper.GUI.Free
if [ ! -x "$AR" ]; then
    mkdir -p "$CACHE"
    curl -fL --progress-bar -o "$CACHE/assetripper.zip" "$ASSETRIPPER_URL"
    echo "$ASSETRIPPER_SHA256  $CACHE/assetripper.zip" | shasum -a 256 --quiet -c \
        || die "AssetRipper download checksum mismatch"
    rm -rf "$AR_DIR"
    ditto -x -k "$CACHE/assetripper.zip" "$AR_DIR"
    rm "$CACHE/assetripper.zip"
fi

rm -rf "$WORK"
mkdir -p "$LOGS"

step "Extracting the game with AssetRipper"
# AssetRipper has no ripping CLI; it is a local web app driven over HTTP.
"$AR" --headless --port 0 --log=false >"$LOGS/assetripper.log" 2>&1 &
AR_PID=$!
trap 'kill $AR_PID 2>/dev/null || true' EXIT
URL=
for _ in $(seq 120); do
    URL=$(sed -nE 's#.*Now listening on: (http://[^ ]+).*#\1#p' "$LOGS/assetripper.log" | head -1)
    [ -n "$URL" ] && break
    kill -0 $AR_PID 2>/dev/null || { cat "$LOGS/assetripper.log" >&2; die "AssetRipper exited"; }
    sleep 0.5
done
[ -n "$URL" ] || die "AssetRipper did not start; see $LOGS/assetripper.log"
curl -sf -o /dev/null -X POST "$URL/Settings/Update" --data "ScriptExportMode=Hybrid"
curl -sf -o /dev/null -X POST "$URL/LoadFolder" --data-urlencode "Path=$GAME"
curl -sf -o /dev/null -X POST "$URL/Export/UnityProject" --data-urlencode "Path=$WORK/rip"
kill $AR_PID; wait $AR_PID 2>/dev/null || true
trap - EXIT
[ -f "$WORK/rip/ExportedProject/Assets/Slender.unity" ] || die "AssetRipper export failed; see $LOGS/assetripper.log"
mv "$WORK/rip/ExportedProject" "$PROJ"
rm -rf "$WORK/rip"

step "Patching scripts"
A=$PROJ/Assets
# The scene only references these scripts. The rest are unused Unity 3.5
# Standard Assets that no longer compile, plus per-assembly AssemblyInfo files
# that collide once everything compiles into one assembly.
KEEP=(
    Plugins/Assembly-CSharp-firstpass/MouseLook.cs
    Plugins/Assembly-UnityScript-firstpass/CharacterMotor.cs
    Plugins/Assembly-UnityScript-firstpass/CharacterMotorJumping.cs
    Plugins/Assembly-UnityScript-firstpass/CharacterMotorMovement.cs
    Plugins/Assembly-UnityScript-firstpass/CharacterMotorMovingPlatform.cs
    Plugins/Assembly-UnityScript-firstpass/CharacterMotorSliding.cs
    Plugins/Assembly-UnityScript-firstpass/FPSInputController.cs
    Plugins/Assembly-UnityScript-firstpass/MovementTransferOnJump.cs
)
rm -rf "$A/Scripts/Assembly-UnityScript" "$A/Scripts/Assembly-UnityScript.meta"
find "$A" -type d -name Properties -prune -exec rm -rf {} +
find "$A" -name Properties.meta -delete
for f in "$A"/Plugins/*/*.cs; do
    rel=${f#"$A"/}
    [[ " ${KEEP[*]} " == *" $rel "* ]] || rm -f "$f" "$f.meta"
done
# AssetRipper cannot decompile D3D9 shaders and emits stubs that shadow Unity's
# built-in shaders. Delete them; PortFixup rebinds the materials.
rm -rf "$A/Shader" "$A/Shader.meta"
perl "$ROOT/scripts/patch-sources.pl" "$A"
cp -R "$ROOT/unity/Editor" "$A/Editor"

unity() {
    local name=$1; shift
    "$UNITY" -batchmode -quit -nographics -projectPath "$PROJ" -logFile "$LOGS/$name.log" "$@" && return
    grep -q 'No valid Unity Editor license found' "$LOGS/$name.log" \
        && die "Unity has no active license. Sign in to Unity Hub and activate one (Personal is free), then rerun."
    { grep -E 'error|Error|ERROR|Exception' "$LOGS/$name.log" || true; } | tail -30 >&2
    die "Unity step '$name' failed; full log: $LOGS/$name.log"
}

step "Importing and repairing the project in Unity $UNITY_VERSION (several minutes on first run)"
unity fixup -executeMethod PortFixup.Run

step "Building Slender.app"
unity build -executeMethod BuildMac.Build

rm -rf "$DIST"
mkdir -p "$DIST"
ditto "$PROJ/Build/Slender.app" "$DIST/Slender.app"

step "Done: $DIST/Slender.app"
echo "The Unity project is kept at $PROJ if you want to open it in the editor."
