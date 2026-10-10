using System;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyScale
{
    /// <summary>
    /// Unattended benchmark for the offline pipeline: start the game with `-colonyscale-bench=&lt;config file&gt;`.
    /// With `start=quicktest` it starts a dev quick-test colony from the main menu (the `-quicktest`
    /// switch does not work in 1.6); otherwise it waits for a game loaded another way (e.g. a dev-mode
    /// `autostart` save). Once the map is up it adds load, runs the A/B benchmark, writes the report and quits.
    /// </summary>
    public sealed class HeadlessBenchmark : MonoBehaviour
    {
        private BenchConfig cfg;
        private int readyFrames;
        private bool quickTestQueued;

        public static void CheckCommandLine()
        {
            if (!GenCommandLine.TryGetCommandLineArg("colonyscale-bench", out string path)) return;
            if (!File.Exists(path)) { CSLog.Error("benchmark config not found: " + path); return; }
            var go = new GameObject("ColonyScaleHeadless");
            DontDestroyOnLoad(go);
            go.AddComponent<HeadlessBenchmark>().cfg = BenchConfig.Parse(File.ReadAllLines(path));
            CSLog.Message("headless benchmark armed: " + path);
        }

        private void Update()
        {
            if (cfg.quickTest && !quickTestQueued && Current.ProgramState == ProgramState.Entry && !LongEventHandler.AnyEventNowOrWaiting)
            {
                quickTestQueued = true;
                // Same as the main menu's dev "Quick test" button.
                LongEventHandler.QueueLongEvent(delegate
                {
                    Root_Play.SetupForQuickTestPlay();
                    PageUtility.InitGameStart();
                }, "GeneratingMap", doAsynchronously: true, GameAndMapInitExceptionHandlers.ErrorWhileGeneratingMap);
                return;
            }
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || LongEventHandler.AnyEventNowOrWaiting || Find.TickManager.ForcePaused)
            {
                readyFrames = 0;
                return;
            }
            Prefs.RunInBackground = true;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            if (++readyFrames < 60) return; // let the first frames settle (section meshes, UI)

            // One shot: a failure must neither add the load again next frame nor leave an unattended game running.
            Destroy(gameObject);
            var quit = cfg.quitWhenDone;
            try
            {
                Populate(Find.CurrentMap, cfg);
                if (cfg.verify) AddVerifyCoverage(Find.CurrentMap);
            }
            catch (Exception e)
            {
                CSLog.Error("benchmark aborted: adding load failed: " + e);
                if (quit) Root.Shutdown();
                return;
            }
            BenchmarkRunner.Start(cfg, _ => { if (quit) Root.Shutdown(); });
            if (cfg.verify) RecalculateWorldPathCosts();
        }

        /// <summary>
        /// Verify runs only: puts the map into states the quick-test colony never reaches, so the optimizations that
        /// act only there record checks. Rot stink (gasGridEarlyOut: GasGrid.Tick returns early until gas ever existed,
        /// and the cloud lasts roughly 4000 ticks outdoors), a separate 2-cell stockpile holding items
        /// (haulablesCellDedupe: only 1-3 cell slot groups revisit a cell within one CellsCheckTick), and selected
        /// colonists, whose paths are drawn (pathDrawCulling has no verify counter; this only runs its code, and the
        /// error count of the report covers it).
        /// </summary>
        private static void AddVerifyCoverage(Map map)
        {
            Rand.PushState(20261010);
            try
            {
                IntVec3 anchor = map.mapPawns.FreeColonistsSpawned.FirstOrDefault()?.Position ?? map.Center;

                IntVec3 gasAt = CellFinder.RandomClosewalkCellNear(anchor, map, 20);
                GasUtility.AddGas(gasAt, map, GasType.RotStink, 4f);

                // Outside the benchmark's 12-cell stockpile, on two free standable cells side by side.
                IntVec3 pileAt = GenRadial.RadialCellsAround(anchor, 30f, false).FirstOrDefault(c =>
                    c.DistanceTo(anchor) > 14f && FreeStorageCell(c, map) && FreeStorageCell(c + IntVec3.East, map));
                if (!pileAt.IsValid) throw new InvalidOperationException("no free cells for the 2-cell stockpile");
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                foreach (var c in new[] { pileAt, pileAt + IntVec3.East })
                {
                    zone.AddCell(c);
                    var steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    steel.stackCount = 20;
                    GenSpawn.Spawn(steel, c, map);
                }

                var selected = map.mapPawns.FreeColonistsSpawned.Take(5).ToList();
                foreach (var pawn in selected) Find.Selector.Select(pawn, playSound: false, forceDesignatorDeselect: false);

                CSLog.Message($"verify coverage added: rot stink at {gasAt}, 2-cell stockpile at {pileAt}, {selected.Count} colonists selected");
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static bool FreeStorageCell(IntVec3 c, Map map) =>
            c.InBounds(map) && c.Standable(map) && !c.Fogged(map) && map.zoneManager.ZoneAt(c) == null && c.GetFirstItem(map) == null;

        /// <summary>
        /// Verify runs only, after Verify is on: the daily world path cost recalculation otherwise runs only in
        /// World.FinalizeInit (before verify starts) and at midnight, which the run's ticks never reach.
        /// </summary>
        private static void RecalculateWorldPathCosts()
        {
            Rand.PushState(20261011);
            try
            {
                Find.WorldPathGrid.RecalculateAllLayersPathCosts();
                CSLog.Message("verify coverage: world path costs recalculated");
            }
            catch (Exception e)
            {
                CSLog.Error("verify coverage: recalculating world path costs failed: " + e);
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static void Populate(Map map, BenchConfig cfg)
        {
            Rand.PushState(20261009);
            try
            {
                IntVec3 anchor = map.mapPawns.FreeColonistsSpawned.FirstOrDefault()?.Position ?? map.Center;
                for (int i = 0; i < cfg.colonists; i++)
                {
                    var pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true));
                    GenSpawn.Spawn(pawn, CellFinder.RandomClosewalkCellNear(anchor, map, 25), map);
                }
                for (int i = 0; i < cfg.animals; i++)
                {
                    var animal = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Muffalo, Faction.OfPlayer));
                    GenSpawn.Spawn(animal, CellFinder.RandomClosewalkCellNear(anchor, map, 35), map);
                }
                if (cfg.itemStacks > 0)
                {
                    var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                    map.zoneManager.RegisterZone(zone);
                    foreach (var c in GenRadial.RadialCellsAround(anchor, 12f, true))
                        if (c.InBounds(map) && c.Standable(map) && map.zoneManager.ZoneAt(c) == null) zone.AddCell(c);

                    ThingDef[] defs = { ThingDefOf.Steel, ThingDefOf.WoodLog, ThingDefOf.MealSimple, ThingDefOf.ComponentIndustrial, ThingDefOf.Cloth };
                    for (int i = 0; i < cfg.itemStacks; i++)
                    {
                        var def = defs[i % defs.Length];
                        var thing = ThingMaker.MakeThing(def);
                        thing.stackCount = Mathf.Min(def.stackLimit, 10 + i % 40);
                        GenPlace.TryPlaceThing(thing, CellFinder.RandomClosewalkCellNear(anchor, map, 40), map, ThingPlaceMode.Near);
                    }
                }
                CSLog.Message($"benchmark load added: {cfg.colonists} colonists, {cfg.animals} animals, {cfg.itemStacks} item stacks");
            }
            finally
            {
                Rand.PopState();
            }
        }
    }
}
