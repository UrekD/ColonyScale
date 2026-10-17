#!/usr/bin/env bash
# Idempotently install the pinned Harmony mod (brrainz.harmony, MIT) into $RIMWORLD_DIR/Mods/Harmony (the headless
# harness passes its Linux run install as RIMWORLD_DIR). On the gaming PC the Steam Workshop copy is used instead.
set -euo pipefail
RW_ROOT=${RW_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}
VERSION=v3.0.1.0   # Harmony 3 for RimWorld 1.7 (1.7/Assemblies, net10)
SHA256=983d762258d0b6b9ff05b0fd9c4a92753bf08e13cdb4eed4e86f81f66f878583
URL=https://github.com/pardeike/HarmonyRimWorld/releases/download/$VERSION/HarmonyMod.zip
game=${RIMWORLD_DIR:-$("$(dirname "${BASH_SOURCE[0]}")/../rimworld-dir.sh")} || { echo "no RimWorld install found; set RIMWORLD_DIR" >&2; exit 1; }
dest=$game/Mods/Harmony
# Installed and the pinned version: nothing to do. Another version (an older pin): replace it.
grep -qF "<version>${VERSION#v}</version>" "$dest/About/Manifest.xml" 2>/dev/null && exit 0
dl=$RW_ROOT/.work/dl/harmony-$VERSION
mkdir -p "$dl"
[[ -f $dl/HarmonyMod.zip ]] || curl -fL --retry 3 -o "$dl/HarmonyMod.zip" "$URL"
echo "$SHA256  $dl/HarmonyMod.zip" | sha256sum -c - >/dev/null
rm -rf "$dl/x" && mkdir -p "$dl/x"
python3 -I -m zipfile -e "$dl/HarmonyMod.zip" "$dl/x"
src=$(dirname "$(find "$dl/x" -path '*/About/About.xml' | head -1)")/..
rm -rf "${dest:?}" && mkdir -p "$dest" && cp -r "$src"/. "$dest"/
echo "[setup-harmony] installed Harmony $VERSION into $dest" >&2
