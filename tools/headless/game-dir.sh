#!/usr/bin/env bash
# Sourced by the headless scripts: sets RW_GAME_DIR, the RimWorld 1.7 install the harness runs. Default: the build
# install (RIMWORLD_DIR, the Windows install that matches the gaming PC), run under portable Wine. RW_GAME_DIR may
# point at a Linux build instead (executable RimWorld, e.g. ~/RimWorld-linux), which runs natively.
RW_GAME_DIR=${RW_GAME_DIR:-${RIMWORLD_DIR:-$("$(dirname "${BASH_SOURCE[0]}")/../rimworld-dir.sh" || true)}}
[[ -n $RW_GAME_DIR && -f $RW_GAME_DIR/Assembly-CSharp.dll && ( -x $RW_GAME_DIR/RimWorld || -f $RW_GAME_DIR/RimWorld.exe ) ]] \
  || { echo "no RimWorld 1.7 install to run (Windows: RimWorld.exe, Linux: executable RimWorld, plus Assembly-CSharp.dll); set RW_GAME_DIR" >&2; exit 1; }
export RW_GAME_DIR
