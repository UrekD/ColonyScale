#!/usr/bin/env bash
# Idempotently install the pinned Harmony mod (brrainz.harmony, MIT) into Mods/Harmony for headless runs.
# Mods/Harmony is gitignored; on the gaming PC the Steam Workshop copy is used instead.
set -euo pipefail
RW_ROOT=${RW_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}
VERSION=v2.4.2.0
SHA256=305d014d3ef893909e28ec48615b886c7ebfa3c71b40a45d45bc20bdd4713af1
URL=https://github.com/pardeike/HarmonyRimWorld/releases/download/$VERSION/HarmonyMod.zip
dest=$RW_ROOT/Mods/Harmony
[[ -f $dest/About/About.xml ]] && exit 0
dl=$RW_ROOT/.work/dl/harmony-$VERSION
mkdir -p "$dl"
[[ -f $dl/HarmonyMod.zip ]] || curl -fL --retry 3 -o "$dl/HarmonyMod.zip" "$URL"
echo "$SHA256  $dl/HarmonyMod.zip" | sha256sum -c - >/dev/null
rm -rf "$dl/x" && mkdir -p "$dl/x"
python3 -I -m zipfile -e "$dl/HarmonyMod.zip" "$dl/x"
src=$(dirname "$(find "$dl/x" -path '*/About/About.xml' | head -1)")/..
mkdir -p "$dest" && cp -r "$src"/. "$dest"/
echo "[setup-harmony] installed Harmony $VERSION into $dest" >&2
