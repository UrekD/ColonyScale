# Colony Scale

> **Proof of concept. AI-generated.** The code, tests and most of these docs were written by AI coding agents (Claude via Claude Code, with reviews by Claude and GPT models), directed by one person. It passes automated and headless in-game checks and has been tried once in the real game, but it has not been played on real saves, has not had a line-by-line human review, and is not on the Steam Workshop. Use it on a copy of your save. How it was made and tested, and what was not tested: [docs/HOW-IT-WAS-MADE.md](docs/HOW-IT-WAS-MADE.md).
>
> Community-made mod. Not affiliated with or endorsed by Ludeon Studios. RimWorld is © Ludeon Studios.

A [Harmony](https://github.com/pardeike/HarmonyRimWorld) mod for RimWorld 1.6 that makes large colonies run faster. It ships 36 optimizations of vanilla code. Every one is **exact**: same game behaviour and the same `Verse.Rand` consumption as vanilla, so the game makes the same decisions with the mod on or off.

Each optimization turns itself off when it can't be sure it is safe:

- the game code it patches (or relies on) differs from what the mod was built against, for example after a game update;
- another mod has patched the same code;
- a mod that already does the same job is active.

The mod settings show which optimizations are on and why any are off.

- Mod name: Colony Scale (`UrekD.ColonyScale`, author UrekD)
- Supported version: RimWorld 1.6 (`About/About.xml`)
- Built and fingerprinted against game 1.6.4871 rev591
- Requires: Harmony 2.4 or newer (`brrainz.harmony`); with an older Harmony every optimization stays off and the mod settings say why

## Install

1. Install the Harmony mod (`brrainz.harmony`), from the Steam Workshop or [GitHub](https://github.com/pardeike/HarmonyRimWorld/releases/latest).
2. Build it (there is no prebuilt release; see [Building](#building)). `make dist` writes `dist/ColonyScale/` and `dist/ColonyScale.zip` (override with `DIST_DIR=`). Copy the `ColonyScale` folder into `RimWorld/Mods/`. It contains `About/`, `Assemblies/` and `Fingerprints.tsv`.
3. Enable Colony Scale in the mod list, **below Harmony** (`loadAfter` is set to `brrainz.harmony`).

Keep `Fingerprints.tsv` in the mod folder: an optimization with no recorded fingerprint stays off (unless the trust option below is on).

## Settings

Options → Mod settings → Colony Scale.

- **Apply optimizations even if the game code they patch changed (unsafe, needs restart).** Off by default. After a game update, optimizations whose patched methods changed stay off until Colony Scale is rebuilt for the new version. This option applies them anyway. Leave it off unless you know why you need it.
- **One checkbox per optimization**, grouped by category, with the description as a tooltip. Toggling takes effect immediately. Only your deviations from the default are saved.
- **A state line under each checkbox.** Green means `active`. Grey means `off` (you switched it off). Yellow shows the reason it is off, such as a changed target, another mod patching the same code, or a missing patch target. Red means removing its patches failed.
- **Failed optimizations stay listed.** If an optimization cannot be created at startup (for example a game member it looks up was renamed), it still appears under its normal name with the state `Failed` and the reason, and cannot be switched on. The other optimizations are not affected.
- **Too old Harmony.** With Harmony older than 2.4 the settings page shows only a red message saying so.

The mod settings have no verify mode. That exists only in the headless harness (see [Measuring](#measuring)).

## Optimizations

| Category | Id | Label | What it does | Steps aside for |
|---|---|---|---|---|
| AI | `reachabilityClearHostile` | Faster reachability reset on doors | Opening or closing a door checks each pawn's hostility to the door once instead of once per cached path check. Biggest effect with many pawns walking through doors. | — |
| AI | `reservationIndex` | Indexed reservation checks | Checks like "can this pawn reserve that item" look only at the reservations on that item instead of every reservation on the map. Biggest effect with many pawns hauling, cooking and building at once. | `bs.performancefish`, `brage.paralleltick` |
| AI | `mergeSameDef` | Faster stack merge search | When looking for a stack to merge a partial stack into, haulers only look at stacks of the same item in that stockpile or shelf instead of everything stored there. Helps with large stockpiles. | `brage.paralleltick` |
| Frame | `textSizeCache` | Cache text measurements | Labels (pawn names, item labels, UI text) are measured once and reused instead of being measured again every frame. | `Taranchuk.PerformanceOptimizer` |
| Frame | `silhouetteCache` | Faster silhouette lookups | Pawn silhouettes (shown when zoomed out) find their material without allocating memory for every pawn every frame. | — |
| Frame | `windSwayPerFrame` | Plant sway once per frame | The wind sway of plants is sent to the graphics card once per frame instead of once per tick. Helps most at high game speeds. | `brage.paralleltick`, `Taranchuk.PerformanceOptimizer` |
| Frame | `overlayCulling` | Skip hidden overlay icons | Forbidden markers and other overlay icons are no longer checked every frame for things that have no icon, and forbidden markers of off-screen items are not drawn. Helps on large maps with many items. | — |
| Frame | `pathDrawCulling` | Skip off-screen path lines | The movement path of selected pawns is only drawn where it is on screen. Helps when many pawns with long paths are selected. | — |
| Frame | `pawnPreDrawSkip` | Skip redundant pawn colour writes | Pawn drawing no longer sets each body part's and apparel layer's colour twice per frame; the second write already sets the same colour. Helps with many pawns on screen. | `brage.paralleltick` |
| Map | `wastepackDissolution` | Cheaper wastepack ticking | Wastepacks only work out their dissolution interval (indoor/outdoor and rain checks) once their dissolve timer is close enough to it to matter. | — |
| Map | `connectivityIncremental` | Faster pathfinding connectivity updates | When walls, doors or terrain change, the pathfinder updates its connectivity data without wiping a large lookup table for every changed cell. Biggest effect while building, mining or fighting. | — |
| Map | `resourceCounter` | Faster resource counting | The resource readout recount, which runs whenever a pawn picks up, drops or loads a counted item, walks stockpile cells directly and adds stacks per item type instead of updating a lookup table per stack. | — |
| Map | `roomStatsMemo` | Faster room stat updates | When a room's impressiveness, beauty, wealth and role are recalculated, its list of contained things is built once instead of once per stat and role. | — |
| Map | `nonSlotStorage` | Faster special storage search | Looking for a grave, bookcase or other container-type storage for an item no longer steps over every stockpile and shelf on the map. | `bs.performancefish`, `brage.paralleltick` |
| Map | `listerThings` | Faster thing listers | Spawning, despawning and pawn movement update the map and region thing lists without testing every thing group or scanning whole lists. Biggest effect in fights (projectiles) and on large maps. | — |
| Map | `thingListRemovals` | Faster tick and draw list removal | Despawning a thing (projectiles, picked-up items, eaten meals) no longer scans the whole tick and draw lists. | — |
| Map | `gasGridEarlyOut` | Skip gas updates on empty cells | Gas spreading and fading no longer does any work for map cells that contain no gas. | — |
| Map | `contentsTickSkip` | Skip idle inventory ticks | Pawns, construction frames and bookcases no longer walk their inventory, equipment, apparel or stored items every tick when nothing in there needs to tick. Helps with every pawn on the map. | `brage.paralleltick` |
| Map | `haulablesCellDedupe` | Skip repeated haulable cell checks | Small storage areas no longer have the same cell re-checked for haulable items several times in a single tick. | `brage.paralleltick` |
| Pawns | `bloodRain` | Cheaper blood rain check | Pawns check for blood rain with a direct look at the map's and world's active conditions. Anomaly only. | `brage.paralleltick` |
| Pawns | `hiddenDeselect` | Cheaper hidden-pawn deselect check | The per-tick check that deselects pawns which turned invisible only looks at pawns that could be selected. | `brage.paralleltick` |
| Pawns | `statusEffecters` | Cheaper status effecter scan | Each tick, pawns look for hediff visual effects with a cheaper type check. | `brage.paralleltick` |
| Pawns | `overseerSubject` | Faster mech control checks | Colony mechs check whether their mechanitor controls them without scanning the mechanitor's whole mech list, and only once per tick when nothing changed in between. Helps colonies with many mechs. | `brage.paralleltick` |
| Pawns | `questLodgerScan` | Faster quest lodger checks | Checks for whether a pawn is a quest lodger, guest or helper skip quest parts of quests that have already ended. Grows with the number of quests completed over a long game. | — |
| Pawns | `situationalThoughts` | Faster situational thought refresh | Refreshing a pawn's situational thoughts no longer rescans all of its current thoughts once for every thought type in the game. | — |
| Pawns | `moodTotal` | One-pass mood total | Each pawn's mood total is computed from one pass over its thoughts instead of one pass per thought group. | — |
| Pawns | `musicListeningShortcut` | Skip music listening search away from instruments | Pawns that are out of range of every piano, harp or harpsichord no longer search the rooms around them for one being played when their mood thoughts are updated. | `brage.paralleltick` |
| Pawns | `idleAbilities` | Skip idle ability ticks | Abilities and psycasts that are not being cast, have no running effects and are not finishing a cooldown are not ticked. Helps most with many psycasters. | — |
| Pawns | `geneTicks` | Skip idle gene ticks | Genes that have nothing to do on a tick (nearly all of them) are no longer checked every tick for every pawn. | `brage.paralleltick` |
| Pawns | `hediffTickPlan` | Skip idle hediff ticks | Health conditions with nothing to do on a tick (scars, implants, missing parts, most illnesses) are no longer ticked every tick for every pawn. Their periodic updates are unaffected. | `brage.paralleltick` |
| Pawns | `spawnedPawnWorldChecks` | Skip world-pawn checks for pawns on a map | Pawns on a map no longer look themselves up in the world pawn lists every tick; only pawns off the map can be world pawns. | `brage.paralleltick` |
| World | `psychicRitualCooldownTick` | Allocation-free psychic ritual cooldowns | The psychic ritual cooldown check (Anomaly) no longer creates garbage every tick. | — |
| World | `questMtbGate` | Cheaper quest timers | Random-chance quest timers (such as betrayals) skip their colony scan on ticks where they cannot fire. | — |
| World | `worldPathGridDaily` | Faster daily world path costs | The once-a-day recalculation of world travel costs (winter slowdown) reuses each tile's position on the planet instead of recomputing it, removing a stutter at midnight on large worlds. | — |
| World | `questReserveFilter` | Faster quest reservation checks | Checks whether a world pawn is reserved or borrowed by a quest only look at the quest parts that can reserve or lend pawns. Helps late games with many world pawns and active quests. | `brage.paralleltick` |
| World | `settlementIdleTick` | Skip idle settlement ticks | Faction settlements on the world map skip their per-tick bookkeeping on ticks where they have nothing to do. Grows with the number of settlements on the world. | `brage.paralleltick` |

The list above is generated from the `Optimization` classes under `Mods/ColonyScale/Source/ColonyScale/Optimizations/`. "Steps aside for" lists the package ids from each class's `IncompatiblePackageIds`. If one of those mods is active, that optimization stays off. Each class comment holds the correctness argument for that optimization.

## Measuring

**In game.** Enable dev mode, then use Debug actions → Colony Scale:

- *A/B benchmark (about 1 min)*: 300 warm-up ticks, 300 ticks per block, 3 rounds, 100 discarded ticks after each switch.
- *A/B benchmark (long, more precise)*: 600 warm-up ticks, 1000 ticks per block, 5 rounds, 200 discarded ticks after each switch.

It alternates blocks of ticks with the optimizations off and on in ABBA order, runs about 15 ticks per rendered frame, and times only `TickManager.DoSingleTick`. After each switch it discards some ticks so blocks measure steady state. It uses the optimizations you currently have on. The report shows, for off and on, mean and median ms/tick and error counts, plus the speedup, a tick-only TPS ceiling and per-block timings. It excludes rendering and other frame work.

**Headless (maintainer).** `make bench` runs `tools/headless/bench.sh`. It starts the Windows game build under portable Wine and Xvfb with a quick-test colony, adds load, runs the A/B benchmark, writes a report and quits. Requirements: a RimWorld Windows install in `RIMWORLD_DIR`, `Xvfb`, `curl`, `tar` with xz, `sha256sum` and `python3`. The first run downloads and checksum-checks Wine 11.0 (Kron4ek build) and the Harmony mod v2.4.2.0 into `.work/` and `Mods/Harmony/`, both gitignored. Output goes to `.work/bench/<mode>/` (`report.txt`, `game.log`).

| Env var | Default | Meaning |
|---|---|---|
| `MODE` | `bench` | `bench`, or `verify`: optimizations also run vanilla and count mismatches; timings are meaningless |
| `COLONISTS` | 40 | colonists added to the quick-test map |
| `ANIMALS` | 80 | animals added |
| `ITEMS` | 400 | item stacks added in a stockpile |
| `ROUNDS` | 4 | A/B rounds |
| `BLOCK` | 500 | ticks per block |
| `TPF` | 15 | ticks per rendered frame |
| `SETTLE` | 120 | ticks discarded after each switch |
| `RW_TIMEOUT` | 2400 | seconds before the game is killed |
| `RIMWORLD_DIR` | nearest folder at or above the repo with the game DLLs | game install |

`tools/headless/run-game.sh` also reads `RW_SAVEDATA`, `RW_LOG`, `RW_RES` (default 1024x768) and `RW_WAIT_FOR`. They are documented in the script header.

Correctness check against vanilla: `MODE=verify tools/headless/bench.sh`. The report ends with checks and mismatches per optimization. Not every optimization supports verify mode.

**Results.** Both runs: game 1.6.4871 rev591, 43 colonists, 15 ticks per frame, 600 warm-up ticks, 120 discarded ticks after each switch, 6 rounds of off/on blocks of 1000 ticks, all optimizations on. Each run builds its own random quick-test colony, so the two numbers bracket the gain on this setup rather than repeat one measurement.

| Report | Optimizations | Map pawns | Things | off ms/tick | on ms/tick | Less tick time | TPS ceiling |
|---|---|---|---|---|---|---|---|
| `docs/bench/2026-10-09-33opts.txt` | 33 | 167 | 34,601 | 6.126 | 4.909 | **19.9%** | 163 → 204 |
| `docs/bench/2026-10-09-36opts.txt` | 36 | 153 | 40,297 | 5.042 | 4.115 | **18.4%** | 198 → 243 |

Both had 0 errors. The lower figure for 36 optimizations is not a regression: the colonies differ. It is one machine under Wine, so treat the numbers as indicative; they depend on colony size, other mods and hardware. Only `DoSingleTick` is timed; rendering and other frame work are not.

**Verify.** `docs/bench/2026-10-09-36opts-verify.txt` is a `MODE=verify` run (2 rounds of 300 ticks) of all 36 optimizations: **0 mismatches**. Its timings are meaningless (verify also runs vanilla).

## Compatibility

**Missile Girl** (`vr.missilegirl`, the RocketMan 1.6 fork). Colony Scale never patches its targets: `StatWorker.GetValue`, `Pawn_NeedsTracker.NeedsTrackerTickInterval`, `BeautyUtility.FillBeautyRelevantCells`, `GenTemperature.ComfortableTemperatureRange`, `ListerBuildingsRepairable.UpdateBuilding`, `Lord.Notify_PawnDamaged`, `CompDeepDrill.CanDrillNow`, `PlayLog.ReduceToCapacity`, `WorldPawns.DefPreventingMothball` and `AlertsReadout`. Missile Girl is not listed in any `IncompatiblePackageIds`. Anything else it patches is handled by the general rule below.

**Mods that step aside.** These optimizations stay off when the mod is active:

- **Free Performance** (`brage.paralleltick`): `reservationIndex`, `mergeSameDef`, `windSwayPerFrame`, `pawnPreDrawSkip`, `nonSlotStorage`, `contentsTickSkip`, `haulablesCellDedupe`, `bloodRain`, `hiddenDeselect`, `statusEffecters`, `overseerSubject`, `geneTicks`, `hediffTickPlan`, `musicListeningShortcut`, `spawnedPawnWorldChecks`, `questReserveFilter`, `settlementIdleTick`.
- **Performance Fish** (`bs.performancefish`): `reservationIndex`, `nonSlotStorage`.
- **Performance Optimizer** (`Taranchuk.PerformanceOptimizer`): `textSizeCache`, `windSwayPerFrame`.

**General rule.** Any foreign Harmony patch on a method an optimization patches or depends on turns that one optimization off, and the settings show the reason (for example "another mod patches X: owner"). For a method we replace outright, foreign transpilers, inner patches and prefixes that could run after ours count. Prefixes that run before ours and postfixes do not. Optimizations that don't touch the same code keep working.

Revalidation happens after all mods' startup patches and again when a game starts or loads, so patches installed late are caught too.

## Building

The build compiles against the game's own DLLs, so you need a RimWorld 1.6 install (the Windows build's `RimWorldWin64_Data/Managed`). The game files are not, and may not be, part of this repository.

**Layout.** The repo is meant to be laid over a RimWorld install: clone it, then copy a RimWorld 1.6 Windows install into the clone. The game files are gitignored. The headless harness runs the game from the repo root, and the build finds the game by looking for `RimWorldWin64_Data/Managed/Assembly-CSharp.dll` in the repo folder or any folder above it. If you only want to build, the game can live anywhere: set `RIMWORLD_DIR=/path/to/RimWorld`.

Requirements: .NET SDK 10 (the mod targets `net472`, the offline checker `net10.0`), NuGet access for `Lib.Harmony` (2.4.1 for the mod, 2.4.2 for the checker), `make`, and `ilspycmd` for `make decompile`/`make update`. The build treats warnings as errors. Developed on Linux; the Makefile and scripts assume a POSIX shell.

| Target | What it does |
|---|---|
| `make build` | builds the mod into `Mods/ColonyScale/Assemblies/` |
| `make build-check` | builds the offline checker |
| `make test` | offline, no game needed: every patch target resolves, every transpiler finds its IL pattern, every patch applies, and every target still matches its recorded fingerprint |
| `make fingerprints` | re-records `Mods/ColonyScale/Fingerprints.tsv` and the local IL baseline `tools/baseline/il` for the installed game |
| `make update` | game update pipeline (below) |
| `make decompile` | decompiles `Assembly-CSharp.dll` into `.work/decompiled/<version>/` (gitignored) |
| `make bench` | headless A/B benchmark ([Measuring](#measuring)) |
| `make dist` | runs `make test`, then writes the distributable folder and zip |
| `make clean` | removes build output |

## Game updates

`make update` diffs every patched method's IL against a local baseline in `tools/baseline/il`. The baseline is disassembled game code, so it is not published. Record it with `make fingerprints` **before** you install the new game version.

1. Copy the new game into `RIMWORLD_DIR`.
2. Run `make update` (add `BENCH=1` to also run the headless benchmark). It builds the checker, decompiles the new version, dumps the IL of every patched method and diffs it against `tools/baseline/il`, runs the offline checks, rebuilds the mod and writes `reports/update-<version>.md`. Exit code 0 means nothing needs attention, 1 means read the report, 2 means a pipeline step failed and no report was written.
3. If methods changed, read the new code in the decompiled source, adapt or drop the affected optimization, then `make fingerprints && make test && make dist`.
4. If nothing changed, run `make fingerprints` to stamp the new game version, then `make dist`.

Fingerprints are hashes of each patched or depended-on method's IL, taken with the Harmony version recorded in the file's header. Targets marked hook-only (`PatchSpec.AsHook()`: cache-reset and flush points whose body no correctness argument depends on) are not fingerprinted. Until they are re-recorded, optimizations whose target changed stay off in game. `Fingerprints.tsv` ships with the mod. `tools/baseline/` and `Fingerprints.tsv` should only change when you deliberately re-record them.

**Adding an optimization.** Add one class per optimization under `Mods/ColonyScale/Source/ColonyScale/Optimizations/<Area>/`, deriving `Optimization`. Give it a stable `Id`, plain Label and Description, `DependsOn` for every game method the correctness argument relies on, and the argument itself in the class comment. Optimizations must be exact. Then run `make build build-check && make fingerprints && make test`. More rules:

- A replacing prefix uses `replacesOriginal: true` and `Priority.Last`.
- A transpiler throws `PatternNotFoundException` when the IL it expects is missing.
- Patches that only mark a moment (cache resets, flush points) use `PatchSpec.AsHook()`.
- Guard static buffers against reentrancy.
- Never patch Missile Girl's targets (see [Compatibility](#compatibility)).
- Support verify mode (`Verify.Enabled`, `Verify.Record`) where practical.

## Sources and credits

Used to build, run or test the mod:

- **[RimWorld](https://rimworldgame.com/)** by Ludeon Studios. The mod compiles against the game's DLLs, and the vanilla methods were read in decompiled form for reference. That is allowed by the [RimWorld EULA](https://rimworldgame.com/eula/), which says mods must be free and must be presented as community-made. Optimizations that replace a vanilla method re-implement it, so they restate small pieces of vanilla logic. No game files, decompiled sources or IL dumps are in this repository.
- **[Harmony](https://github.com/pardeike/Harmony)** by Andreas Pardeike (MIT): the patching library (`Lib.Harmony` NuGet, compile-time only). At runtime it comes from the **[Harmony mod](https://github.com/pardeike/HarmonyRimWorld)** (`brrainz.harmony`, MIT), which the headless harness downloads.
- **[ILSpy / ilspycmd](https://github.com/icsharpcode/ILSpy)** (MIT): decompiling for reference and the update pipeline.
- **[Wine](https://www.winehq.org/)** (LGPL-2.1+), the portable builds by **[Kron4ek/Wine-Builds](https://github.com/Kron4ek/Wine-Builds)**, and **Xvfb** (X.Org): running the Windows game headless on Linux. Wine is downloaded and checksum-verified by `tools/headless/setup-wine.sh`, not vendored.
- **.NET SDK** and the .NET Framework 4.7.2 reference assemblies (Microsoft, MIT).

Other mods studied (read only; no code copied):

- **[Free Performance](https://github.com/BrageEvjen/FreePerformance)** by BrageEvjen (MIT). The source was analysed for compatibility and ideas. These optimizations re-implement ideas found there, with our own code and exactness arguments: `contentsTickSkip`, `settlementIdleTick`, `questReserveFilter`, `windSwayPerFrame`, `haulablesCellDedupe`, `gasGridEarlyOut`, `pawnPreDrawSkip`, `mergeSameDef`, `musicListeningShortcut`, and the idle tend/link comp handling in `hediffTickPlan`. Earlier optimizations (`hediffTickPlan`, `geneTicks`, `bloodRain`, `spawnedPawnWorldChecks`, `statusEffecters`, `overseerSubject`, `reservationIndex`, `nonSlotStorage`) turned out to overlap with Free Performance. They were developed independently, before it was studied.
- **[Missile Girl](https://github.com/ViralReaction/MissileGirl)** (ViralReaction, EPL-2.0), the 1.6 fork of **[RocketMan](https://github.com/kbatbouta/RimWorld-RocketMan)** (EPL-2.0). Its patch targets were listed so Colony Scale stays out of them.
- **[Performance Fish](https://github.com/bbradson/Performance-Fish)** (bbradson, MPL-2.0) and **[Performance Optimizer](https://github.com/Taranchuk/PerformanceOptimizer)** (Taranchuk, MIT): checked for overlapping patches, which led to the step-aside entries in [Compatibility](#compatibility).

AI tools used to write the code are listed in [docs/HOW-IT-WAS-MADE.md](docs/HOW-IT-WAS-MADE.md).

## License

[The Unlicense](LICENSE): public domain, so anyone may do anything with it. No warranty. This covers the code in this repository only, not RimWorld or the third-party projects above.
