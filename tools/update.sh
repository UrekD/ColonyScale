#!/usr/bin/env bash
# Game update pipeline (`make update`). Run after a new RimWorld version lands in $RIMWORLD_DIR.
# Decompiles the new build, diffs the IL of every patched method against the recorded baseline,
# re-runs the offline checks, rebuilds the mod, optionally benchmarks, and writes a report.
# Exit code: 0 = nothing needs attention, 1 = see the report, 2 = a pipeline step failed (no report is claimed).
set -uo pipefail
cd "$(dirname "$0")/.."

CHECK_PROJ=Mods/ColonyScale/Source/ColonyScale.Tests/ColonyScale.Tests.csproj
FINGERPRINTS=Mods/ColonyScale/Fingerprints.tsv
check() { dotnet run --project "$CHECK_PROJ" -c Release --no-build -- "$@"; }
# A step the review depends on failed: stop, so no report claims a completed review.
die() { echo "update pipeline failed: $*" >&2; echo "no update report written" >&2; exit 2; }

mkdir -p reports .work

dotnet build "$CHECK_PROJ" -c Release -v q -nologo > .work/update-build-check.log 2>&1 \
  || { echo "checker does not compile against the new game — see .work/update-build-check.log"; grep -E "error" .work/update-build-check.log | head -20; exit 1; }

new=$(check gameversion) || die "could not read the installed game version"
[ -n "$new" ] || die "installed game version is empty"
base=$(sed -n 's/^# game\t//p' "$FINGERPRINTS") || die "could not read $FINGERPRINTS"
[ -n "$base" ] || die "no recorded game version in $FINGERPRINTS"
# The baseline is disassembled game code and is not published with the source: record it with
# `make fingerprints` while the previous game version is still installed.
[ -d tools/baseline/il ] || die "no IL baseline in tools/baseline/il (run \`make fingerprints\` on the previous game version first)"
slug=${new// /-}
report="reports/update-$slug.md"
problems=0

tools/decompile.sh "$new" > /dev/null || die "decompiling $new failed (tools/decompile.sh)"

il_dir=".work/il-$slug"
rm -rf "$il_dir" || die "could not clear $il_dir"
check dump-il --out "$il_dir" > /dev/null || die "dumping the IL of the patched methods failed"
il_diff=$(diff -ruN tools/baseline/il "$il_dir")
case $? in
  0) ;;
  1) problems=1 ;;
  *) die "diffing the IL against tools/baseline/il failed" ;;
esac

check_out=$(check check --fingerprints "$FINGERPRINTS" 2>&1) || problems=1
build_out=$(dotnet build Mods/ColonyScale/Source/ColonyScale/ColonyScale.csproj -c Release -nologo 2>&1) || problems=1

bench_out="skipped (set BENCH=1 and set up tools/headless first)"
if [ "${BENCH:-0}" = 1 ]; then
  bench_out=$(tools/headless/bench.sh 2>&1) || problems=1
fi

{
  echo "# Game update report: $base → $new"
  echo
  echo "Generated $(date -Is) by \`make update\`. Decompiled source: \`.work/decompiled/$slug/\`."
  echo
  if [ "$problems" = 0 ]; then
    echo "**Result: nothing to do.** Every patched method is unchanged, all checks pass and the mod builds."
    [ "$base" != "$new" ] && echo "Run \`make fingerprints\` to stamp the new game version, then \`make dist\`."
  else
    echo "**Result: needs attention.** In game, optimizations whose target changed stay off by themselves until this is resolved."
    echo
    echo "Next steps: for each changed method below, read the new code in the decompiled source, adapt the"
    echo "optimization under \`Mods/ColonyScale/Source/ColonyScale/Optimizations/\` (or drop it if the game now"
    echo "does it itself), then \`make fingerprints && make test && make dist\`."
  fi
  echo
  echo "## Patched methods whose IL changed"
  echo
  if [ -z "$il_diff" ]; then echo "None."; else echo '```diff'; echo "$il_diff"; echo '```'; fi
  echo
  echo "## Offline checks"
  echo; echo '```'; echo "$check_out"; echo '```'
  echo
  echo "## Build"
  echo; echo '```'; echo "$build_out" | tail -5; echo '```'
  echo
  echo "## Headless benchmark"
  echo; echo '```'; echo "$bench_out" | tail -25; echo '```'
} > "$report" || die "could not write $report"

echo "report: $report"
[ "$problems" = 0 ] && echo "nothing needs attention" || echo "needs attention — see report"
exit "$problems"
