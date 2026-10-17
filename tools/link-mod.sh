#!/usr/bin/env bash
# Idempotently make the game load this checkout: $RIMWORLD_DIR/Mods/ColonyScale -> <repo>/Mods/ColonyScale.
# Nothing to do when the repo is overlaid on the install (both are the same folder). Refuses to replace a
# real folder or a link to somewhere else (e.g. a copied release build); move that away first.
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
game=${RIMWORLD_DIR:-$("$root/tools/rimworld-dir.sh")} || { echo "no RimWorld install found; set RIMWORLD_DIR" >&2; exit 1; }
src=$root/Mods/ColonyScale
dest=$game/Mods/ColonyScale
[[ -e $dest || -L $dest ]] && {
  [[ $(realpath -m "$dest") == "$(realpath "$src")" ]] && exit 0
  echo "$dest exists and is not this checkout; move it away first" >&2; exit 1
}
mkdir -p "$game/Mods"
ln -s "$src" "$dest"
echo "[link-mod] $dest -> $src" >&2
