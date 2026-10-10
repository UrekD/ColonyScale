# How Colony Scale was made and tested

Colony Scale is a **proof of concept**. It was written by AI coding agents over about two days in October 2026, directed by one person. This page says who did what, how it was checked, and what was not checked.

## Who did what

**The maintainer (human)** set the goal (make large RimWorld 1.6 colonies tick faster) and the rules:

- every optimization must be exact: same behaviour and same `Verse.Rand` consumption as vanilla;
- stay out of the methods that RocketMan's 1.6 fork Missile Girl patches;
- decline anything that can't be shown exact.

The maintainer also made the design calls (a Harmony mod rather than binary patching, the self-disabling safeguards, what to publish) and supplied the game install. They did not write the code by hand. The code has not had a line-by-line human review. Apart from the automated and headless runs below, the maintainer has tried it once in the real game.

**AI agents** did the analysis, implementation, testing, reviews and documentation:

| Role | Tool and model |
|---|---|
| Orchestration, analysis of vanilla hot paths, merging, verification runs | [Claude Code](https://claude.com/claude-code) with Claude Opus 5.5 |
| Implementation of optimization batches and fixes | Claude Code subagents, then agents run by a local agent orchestrator in isolated git worktrees: Claude Opus 5.5 for complex work, Claude Sonnet 5.5 for simple work, Claude Haiku for mechanical checks |
| Independent reviews | GPT-6 Astra (OpenAI Codex CLI) reviewed the framework; Claude Fable 5.1 did the final review of every optimization |
| Research | Claude agents read the source of other 1.6 performance mods for compatibility and ideas (see the README's Sources section) |

The game's code was decompiled locally with ILSpy so the agents could read the vanilla methods. The decompiled code was used only as reference and is not in this repository.

## Process

1. **Analysis.** Parallel agents read the decompiled pawn tick, mood and thought, AI and job, map, rendering and world code. Each proposed candidate optimizations with an argument for why the result stays identical to vanilla.
2. **Implementation in batches.** Each optimization is one class with its correctness argument in the class comment. Every game method that argument relies on is listed in `DependsOn`.
3. **Review and fix rounds.** The framework review failed first time, with real defects:
   - tick-list removal order with duplicates;
   - a mood snapshot that differed from vanilla after situational thoughts were recalculated;
   - conflict checks that ran before other mods patch;
   - float formatting differing between Mono and .NET, which broke fingerprints.

   All of these were fixed. The final review passed with low-severity findings only, and those were fixed too.
4. **Candidates dropped as not provably exact (or not worth it).** For example:
   - think-node priority sorting: sibling priorities read state that failed children can change;
   - tooltip prefiltering: the pawn draw position has tween side effects;
   - an early-out in construction delivery: the skipped code consumes `Rand`;
   - a wealth memo, plus the temperature and stat caches other mods use: they would rely on methods Missile Girl patches, or they are approximate;
   - idle verb and stance skips: exact, but only about 0.1% gain.

## How it was tested

**Offline checker** (`make test`, no game process). It loads the real `Assembly-CSharp.dll` in .NET 10 and checks that:

- every patch target resolves;
- every transpiler finds its IL pattern;
- every patch applies with Harmony;
- every target matches its recorded IL fingerprint.

It also runs self-tests of the fingerprinting, list semantics and conflict detection. Result: 39/39 pass.

**In-game verify mode** (`MODE=verify tools/headless/bench.sh`). The real Windows build of the game runs headless under portable Wine and Xvfb on a Linux VM (software rendering, no GPU), with a dev quick-test colony plus added colonists, animals, items, gas, a 2-cell stockpile and selected pawns. Each instrumented optimization computes both its fast result and the vanilla result and counts disagreements. Result: all 36 active, 0 errors, **0 mismatches over about 6.2 million checks**. Not every optimization has a verify hook, and `mergeSameDef` was not exercised (no merge jobs occurred).

**A/B benchmark** (`make bench`, same headless setup). Off and on blocks run in ABBA order. Only `TickManager.DoSingleTick` is timed. Results: **18.4% and 19.9% less tick time** on two random quick-test colonies (about 150–170 map pawns, 35–40k things). The reports are in `docs/bench/`. This is one VM under Wine, so treat the numbers as indicative.

## What has not been tested

- Real play: beyond one try in the real game by the maintainer, it has not been played on a normal install or on real long-running saves.
- Large mod lists. Compatibility with other mods is designed in: foreign patches and known overlapping mods switch the affected optimizations off. It has not been exercised against a real modded save.
- Frame-side optimizations (category Frame): their effect on frame time was not measured. The benchmark times only ticks.
- Long-run determinism. Verify mode compares individual results call by call. It does not replay a whole game to compare the end state.
- These are covered by code but were not seen in game:
  - the "Harmony older than 2.4" refusal and the "Failed" optimization text in the settings;
  - memory release when a map is removed;
  - gravship capture together with `pathDrawCulling`.

Use it on a copy of your save. If something looks wrong, switch the suspect optimization off in the mod settings; each one can be toggled live.
