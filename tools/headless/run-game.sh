#!/usr/bin/env bash
# Run RimWorld 1.7 with -headless (a Windows install, RimWorld.exe, under portable Wine + Xvfb; a Linux build natively): no window and no Vulkan, while game logic and frames still
# run (so render-path optimizations are not exercised). Steam is not needed: without a Steam client its init only warns.
# The game runs in bubblewrap, without network access unless RW_NET=1.
#
# Usage: tools/headless/run-game.sh [game args...]
# Env:   RW_GAME_DIR  install to run (see game-dir.sh; default RIMWORLD_DIR, the Windows install)
#        RW_TIMEOUT   seconds before the game is killed (default 900)
#        RW_SAVEDATA  save-data folder: Config/, Saves/ (default .work/savedata)
#        RW_LOG       game log: the game's console output without colour codes (default .work/run/game.log);
#                     the game's own file log is off (-NoFileLog), it would go to ~/.config/unity3d
#        RW_WAIT_FOR  regex; prints seconds from launch until it appears in the log
#        RW_NET       1: allow network access (default: none)
#        RW_WINDOWED  1: render (no -headless) into a private Xvfb at RW_RES (default 1280x720) with Vulkan on lavapipe
#                     (Mesa software Vulkan; VK_DRIVER_FILES overrides). Screenshot while it runs:
#                     tools/headless/xwd2png.py "$(dirname RW_LOG)/Xvfb_screen0" shot.png
# Exit status: the game's, or 124 on timeout.
set -euo pipefail
RW_ROOT=${RW_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}
. "$(dirname "${BASH_SOURCE[0]}")/game-dir.sh"

timeout_s=${RW_TIMEOUT:-900}
savedata=${RW_SAVEDATA:-$RW_ROOT/.work/savedata}
log=${RW_LOG:-$RW_ROOT/.work/run/game.log}
mkdir -p "$savedata/Config" "$(dirname "$log")"

rm -f "$log"
start=$(date +%s) watch_pid=
if [[ -n ${RW_WAIT_FOR:-} ]]; then
  ( until grep -qE "$RW_WAIT_FOR" "$log" 2>/dev/null; do sleep 0.5; done
    echo "[run-game] '$RW_WAIT_FOR' after $(( $(date +%s) - start ))s" >&2 ) &
  watch_pid=$!
fi
trap '[[ -n $watch_pid ]] && kill $watch_pid 2>/dev/null; true' EXIT

echo "[run-game] game=$RW_GAME_DIR ($(<"$RW_GAME_DIR/Version.txt")) log=$log timeout=${timeout_s}s args: $*" >&2
game=(./RimWorld)
headless=(-headless)
xvfb=(xvfb-run -a -s "-screen 0 1024x768x24 -nolisten tcp")
if [[ ${RW_WINDOWED:-0} == 1 ]]; then
  res=${RW_RES:-1280x720}
  headless=()
  xvfb=(xvfb-run -a -s "-screen 0 ${res}x24 -nolisten tcp -fbdir $(dirname "$log")")
  lvp=/usr/share/vulkan/icd.d/lvp_icd.json
  [[ -n ${VK_DRIVER_FILES:-} || ! -f $lvp ]] || export VK_DRIVER_FILES=$lvp
  game=("${xvfb[@]}" ./RimWorld)
  echo "[run-game] windowed ${res}; screenshot: tools/headless/xwd2png.py $(dirname "$log")/Xvfb_screen0 shot.png" >&2
fi
command -v bwrap >/dev/null || { echo "bwrap not found: install bubblewrap (the game runs sandboxed)" >&2; exit 1; }
sandbox=(bwrap --dev-bind / / --die-with-parent)
[[ ${RW_NET:-0} == 1 ]] || sandbox+=(--unshare-net)
if [[ ! -x $RW_GAME_DIR/RimWorld ]]; then
  # Windows build: Wine from .work/ (setup-wine.sh) and a private X server. Absolute unix paths (/...) resolve on
  # the current drive (Z:, the unix root), so arguments need no Z: prefix.
  RIMWORLD_DIR=$RW_GAME_DIR . "$(dirname "${BASH_SOURCE[0]}")/setup-wine.sh"
  # Every caller shares this prefix and so one wineserver. A game that joins a wineserver started in another run's
  # sandbox (another user namespace) gets "Access denied" from Process.Modules, which Harmony's MonoMod needs: all
  # patching fails. So: one Wine run at a time (others wait), stray servers of crashed runs stopped first, and the
  # run gets its own PID namespace, so all of its Wine processes, the wineserver included, end with it.
  exec 9>"$WINEPREFIX/.run-game.lock"
  flock -n 9 || { echo "[run-game] waiting for another Wine run on $WINEPREFIX" >&2; flock 9; }
  "$WINE_BIN/wineserver" -k 2>/dev/null || true
  # wineserver -w returns early for a server in another PID namespace; under the lock any wineserver left is a stray of
  # this prefix, so wait for it to be gone (a game joining a dying server exits at once with an empty log).
  for _ in $(seq 100); do pgrep -u "$(id -u)" -x wineserver >/dev/null || break; sleep 0.1; done
  ! pgrep -u "$(id -u)" -x wineserver >/dev/null || { echo "[run-game] a wineserver is still running; stop it first" >&2; exit 1; }
  sandbox+=(--unshare-pid --proc /proc)
  game=("${xvfb[@]}" "$WINE_BIN/wine" ./RimWorld.exe)
fi
cd "$RW_GAME_DIR"
set +e
timeout -k 15 "$timeout_s" "${sandbox[@]}" -- "${game[@]}" "${headless[@]}" -NoCrashDialog -NoFileLog "-savedatafolder=$savedata" "$@" 2>&1 \
  | sed -u 's/\x1b\[[0-9;]*m//g' > "$log"
rc=${PIPESTATUS[0]}
set -e
echo "[run-game] exited rc=$rc after $(( $(date +%s) - start ))s" >&2
exit "$rc"
