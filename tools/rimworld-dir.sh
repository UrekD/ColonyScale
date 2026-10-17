#!/usr/bin/env bash
# Print the RimWorld 1.7 install (Windows build) used by the build and the headless harness:
# $RIMWORLD_DIR if set, else the nearest folder at or above the repo that holds
# Assembly-CSharp.dll or has a RimWorld/ subfolder that does
# (repo overlaid on the install, or cloned next to it, e.g. ~/rim-mods/colony-scale with ~/RimWorld).
# Prints nothing and exits 1 if there is none. The csproj files apply the same rule.
set -euo pipefail
if [[ -n ${RIMWORLD_DIR:-} ]]; then echo "$RIMWORLD_DIR"; exit 0; fi
dll=Assembly-CSharp.dll
d=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
while :; do
  [[ -f $d/$dll ]] && { echo "$d"; exit 0; }
  [[ -f $d/RimWorld/$dll ]] && { echo "$d/RimWorld"; exit 0; }
  [[ $d == / ]] && exit 1
  d=$(dirname "$d")
done
