#!/usr/bin/env bash
# Run the Windows RimWorld build headless: portable Wine + private Xvfb + Wine virtual desktop.
# The virtual desktop is required: without it the game hangs right after the static
# constructors (main thread spins, the def-loading thread never starts).
#
# Usage: tools/headless/run-game.sh [game args...]     e.g.  run-game.sh -quicktest
# Env:   RW_TIMEOUT   seconds before the game is killed (default 900)
#        RW_SAVEDATA  save-data folder: Config/, Saves/ (default .work/savedata)
#        RW_LOG       game log file (default .work/run/game.log)
#        RW_RES       virtual desktop / window size (default 1024x768; below that the game
#                     rewrites Prefs.xml every 30 frames unless devMode is on)
#        RW_WAIT_FOR  regex; prints seconds from launch until it appears in the log
# Exit status: 124 on timeout, otherwise 0 once the game exits (explorer /desktop waits for the
# game but does not propagate its exit code). Xvfb and the Wine prefix are shut down on exit.
# Screenshot while running: tools/headless/xwd2png.py .work/run/Xvfb_screen0 shot.png
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/setup-wine.sh"

timeout_s=${RW_TIMEOUT:-900}
savedata=${RW_SAVEDATA:-$WORK/savedata}
log=${RW_LOG:-$WORK/run/game.log}
res=${RW_RES:-1024x768}
rundir=$WORK/run
mkdir -p "$savedata/Config" "$(dirname "$log")" "$rundir"

# Windowed at $res, silent, and keep simulating while unfocused (the game pauses otherwise).
# Without a Prefs.xml the game would switch to native-resolution fullscreen on first start.
prefs=$savedata/Config/Prefs.xml
if [[ ! -f $prefs ]]; then
  cat > "$prefs" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<PrefsData>
  <volumeMaster>0</volumeMaster>
  <screenWidth>0</screenWidth>
  <screenHeight>0</screenHeight>
  <fullscreen>False</fullscreen>
  <customCursorEnabled>False</customCursorEnabled>
  <runInBackground>True</runInBackground>
  <langFolderName>English</langFolderName>
  <textureCompression>False</textureCompression>
</PrefsData>
EOF
fi
sed -i -e "s#<screenWidth>[0-9]*<#<screenWidth>${res%x*}<#" -e "s#<screenHeight>[0-9]*<#<screenHeight>${res#*x}<#" \
  -e 's#<runInBackground>False<#<runInBackground>True<#' -e 's#<fullscreen>True<#<fullscreen>False<#' "$prefs"

# Private X server on a free display; framebuffer mmap'ed to $rundir/Xvfb_screen0 (see xwd2png.py).
rm -f "$rundir/display"
Xvfb -displayfd 3 -screen 0 "${res}x24" -nolisten tcp -fbdir "$rundir" \
  3>"$rundir/display" 2>"$rundir/xvfb.log" &
xvfb_pid=$! watch_pid=
cleanup() {
  "$WINE_BIN/wineserver" -k 2>/dev/null || true
  kill $watch_pid "$xvfb_pid" 2>/dev/null || true
}
trap cleanup EXIT
for _ in $(seq 100); do [[ -s $rundir/display ]] && break; sleep 0.1; done
export DISPLAY=:$(<"$rundir/display")

rm -f "$log"
start=$(date +%s)
if [[ -n ${RW_WAIT_FOR:-} ]]; then
  ( until grep -qE "$RW_WAIT_FOR" "$log" 2>/dev/null; do sleep 0.5; done
    echo "[run-game] '$RW_WAIT_FOR' after $(( $(date +%s) - start ))s" >&2 ) &
  watch_pid=$!
fi

echo "[run-game] DISPLAY=$DISPLAY log=$log timeout=${timeout_s}s args: $*" >&2
cd "$RW_ROOT"
rc=0
timeout -k 15 "$timeout_s" "$WINE_BIN/wine" explorer "/desktop=rimworld,$res" ./RimWorldWin64.exe \
  -savedatafolder="Z:$savedata" -logfile "Z:$log" \
  -screen-width "${res%x*}" -screen-height "${res#*x}" -screen-fullscreen 0 \
  "$@" >"$rundir/wine.out" 2>&1 || rc=$?
echo "[run-game] exited rc=$rc after $(( $(date +%s) - start ))s" >&2
exit "$rc"
