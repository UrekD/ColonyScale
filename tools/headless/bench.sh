#!/usr/bin/env bash
# Headless in-game benchmark (`make bench`): quick-test colony + added load, interleaved A/B of every
# active Colony Scale optimization, report written by the mod, game quits itself.
# Env: MODE=bench|verify|profile (profile: also times each optimization's patched targets off vs on, see
#      TargetProfiler; totals include the probe cost; verify: optimizations also run vanilla and count mismatches; timings meaningless;
#      the mod also adds rot stink, a 2-cell stockpile with items and selected colonists, and recalculates the
#      world path costs once verify is on, so paths the quick-test colony never reaches get checked)
#      COLONISTS=40 ANIMALS=80 ITEMS=400 ROUNDS=4 BLOCK=500 TPF=15 WARMUP=600 SETTLE=120 RW_TIMEOUT=2400
#      ONLY=id1,id2 (switch only these optimizations; the others stay on throughout)
set -euo pipefail
cd "$(dirname "$0")/../.."
root=$PWD
. tools/headless/game-dir.sh
# The run install needs the Harmony mod and a link to this checkout (no-ops when they are already there).
RIMWORLD_DIR=$RW_GAME_DIR tools/headless/setup-harmony.sh
RIMWORLD_DIR=$RW_GAME_DIR tools/link-mod.sh
version=$(tr -d '\r\n' < "$RW_GAME_DIR/Version.txt")
# Core plus whichever expansions this install has.
dlcs=()
for d in Royalty Ideology Biotech Anomaly Odyssey; do [[ -d $RW_GAME_DIR/Data/$d ]] && dlcs+=("ludeon.rimworld.${d,,}"); done
dlc_items() { local x; for x in "${dlcs[@]}"; do printf '    <li>%s</li>\n' "$x"; done; }

mode=${MODE:-bench}
dir=$root/.work/bench/$mode
rm -rf "$dir" && mkdir -p "$dir/savedata/Config"
cat > "$dir/savedata/Config/Prefs.xml" <<PREFS
<?xml version="1.0" encoding="utf-8"?>
<PrefsData>
  <volumeMaster>0</volumeMaster>
  <screenWidth>1024</screenWidth>
  <screenHeight>768</screenHeight>
  <fullscreen>False</fullscreen>
  <customCursorEnabled>False</customCursorEnabled>
  <runInBackground>True</runInBackground>
  <langFolderName>English</langFolderName>
  <automaticPauseMode>Never</automaticPauseMode>
  <autosaveIntervalDays>100</autosaveIntervalDays>
  <devMode>False</devMode>
  <logVerbose>False</logVerbose>
</PrefsData>
PREFS
cat > "$dir/savedata/Config/ModsConfig.xml" <<MODS
<?xml version="1.0" encoding="utf-8"?>
<ModsConfigData>
  <version>$version</version>
  <activeMods>
    <li>brrainz.harmony</li>
    <li>ludeon.rimworld</li>
$(dlc_items)
    <li>urekd.colonyscale</li>
  </activeMods>
  <knownExpansions>
$(dlc_items)
  </knownExpansions>
</ModsConfigData>
MODS
report=$dir/report.txt
cat > "$dir/bench.cfg" <<CFG
colonists=${COLONISTS:-40}
animals=${ANIMALS:-80}
itemStacks=${ITEMS:-400}
rounds=${ROUNDS:-4}
blockTicks=${BLOCK:-500}
ticksPerFrame=${TPF:-15}
warmupTicks=${WARMUP:-600}
settleTicks=${SETTLE:-120}
verify=$([ "$mode" = verify ] && echo true || echo false)
profile=$([ "$mode" = profile ] && echo true || echo false)
only=${ONLY:-}
output=$report
quitWhenDone=true
start=quicktest
CFG

RW_SAVEDATA=$dir/savedata RW_LOG=$dir/game.log RW_TIMEOUT=${RW_TIMEOUT:-2400} \
  tools/headless/run-game.sh "-colonyscale-bench=$dir/bench.cfg" || true

echo "== game log: $(grep -c -E 'Exception|^ERROR|\[ColonyScale\].*(off|mismatch)' "$dir/game.log" 2>/dev/null || echo 0) error/warning lines (see $dir/game.log)"
grep -E '\[ColonyScale\]' "$dir/game.log" 2>/dev/null | head -40 || true
if [[ -f $report ]]; then
  echo "== report ($report)"; cat "$report"
else
  echo "no report written — see $dir/game.log" >&2; exit 1
fi
if grep -q -E '^benchmark aborted|could not be restored' "$report"; then
  echo "benchmark did not complete a valid comparison — see the report above" >&2; exit 1
fi
