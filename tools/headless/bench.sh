#!/usr/bin/env bash
# Headless in-game benchmark (`make bench`): quick-test colony + added load, interleaved A/B of every
# active Colony Scale optimization, report written by the mod, game quits itself.
# Env: MODE=bench|verify (verify: optimizations also run vanilla and count mismatches; timings meaningless;
#      the mod also adds rot stink, a 2-cell stockpile with items and selected colonists, and recalculates the
#      world path costs once verify is on, so paths the quick-test colony never reaches get checked)
#      COLONISTS=40 ANIMALS=80 ITEMS=400 ROUNDS=4 BLOCK=500 TPF=15 RW_TIMEOUT=2400
set -euo pipefail
cd "$(dirname "$0")/../.."
root=$PWD
tools/headless/setup-harmony.sh

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
  <version>1.6.4871 rev591</version>
  <activeMods>
    <li>brrainz.harmony</li>
    <li>ludeon.rimworld</li>
    <li>ludeon.rimworld.royalty</li>
    <li>ludeon.rimworld.ideology</li>
    <li>ludeon.rimworld.biotech</li>
    <li>ludeon.rimworld.anomaly</li>
    <li>ludeon.rimworld.odyssey</li>
    <li>urekd.colonyscale</li>
  </activeMods>
  <knownExpansions>
    <li>ludeon.rimworld.royalty</li>
    <li>ludeon.rimworld.ideology</li>
    <li>ludeon.rimworld.biotech</li>
    <li>ludeon.rimworld.anomaly</li>
    <li>ludeon.rimworld.odyssey</li>
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
warmupTicks=600
settleTicks=${SETTLE:-120}
verify=$([ "$mode" = verify ] && echo true || echo false)
output=Z:$report
quitWhenDone=true
CFG

RW_SAVEDATA=$dir/savedata RW_LOG=$dir/game.log RW_TIMEOUT=${RW_TIMEOUT:-2400} \
  tools/headless/run-game.sh -quicktest "-colonyscale-bench=Z:$dir/bench.cfg" || true

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
