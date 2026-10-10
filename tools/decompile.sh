#!/usr/bin/env bash
# Decompile the installed Assembly-CSharp.dll into .work/decompiled/<version>/ (gitignored; never commit it).
# Usage: tools/decompile.sh "<game version>"
set -euo pipefail
cd "$(dirname "$0")/.."
version="${1:?game version}"
dir=".work/decompiled/${version// /-}"
if [ -d "$dir" ]; then
  echo "already decompiled: $dir"
else
  # Decompile next to the target and move it in only when complete, so a failed run is not taken as done later.
  partial="$dir.partial"
  rm -rf "$partial" && mkdir -p "$partial"
  ilspycmd -p -o "$partial" "${RIMWORLD_DIR:?set RIMWORLD_DIR (make sets it)}/RimWorldWin64_Data/Managed/Assembly-CSharp.dll" > /dev/null
  mv "$partial" "$dir"
  echo "decompiled to $dir"
fi
ln -sfn "${version// /-}" .work/decompiled/current
