using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ColonyScale.Optimizations.Pawns
{
    /// <summary>
    /// The "listening to piano/harp/harpsichord" situational thoughts (ThoughtWorker_MusicalInstrumentListeningBase)
    /// run, for every pawn every recalculation, a region search from the pawn for an instrument of that def that is
    /// being played. When the map has such instruments at all, vanilla walks up to 30 regions around the pawn even if
    /// none is within the instrument's range. This replacing prefix answers "not listening" without the search when
    /// every instrument of the def on the map is out of range of the pawn; otherwise it runs vanilla's exact body.
    ///
    /// Exact:
    /// - Same body: CapableOf(Hearing) first (its capacity cache fill unchanged), then InstrumentDef once, then the same
    ///   ClosestThingReachable call with the same arguments and validator.
    /// - Same answer: both ClosestThingReachable paths only call the validator, and the global path only calls
    ///   CanReach, for things within maxDistance of the pawn (the region path tests num2 &lt; max², the global path
    ///   num &lt;= max², both on PositionHeld). Every thing of the def that either path can see is in
    ///   map.listerThings (region listers and the map lister are filled together at spawn), and all of those are
    ///   checked to be farther away, so vanilla returns null: "not listening".
    /// - Nothing observable is skipped: the shortcut needs a pawn on its map, in bounds, no regions or rooms to
    ///   rebuild (so GetValidRegionAt rebuilds nothing and logs nothing), and a pawn without an avoid grid (Region.Allows
    ///   would otherwise lazily regenerate it). With no thing in range, vanilla's search makes no CanReach call (no
    ///   reachability cache entry) and no validator call; what remains is region traversal: Region.Allows with
    ///   maxDanger Deadly (no per-frame danger cache), door passability (field reads; mod doors that override
    ///   PawnCanOpen or FreePassage make the shortcut stay off), ResolveClosestTouchPathMode and the within-region
    ///   reachability test (reads), and the traversal's own worker queue and visit counters, which no result
    ///   depends on. No Rand anywhere.
    /// Main thread only (thought recalculation).
    /// </summary>
    public sealed class MusicListeningShortcut : Optimization
    {
        private const string OptId = "musicListeningShortcut";

        public override string Id => OptId;
        public override string Label => "Skip music listening search away from instruments";
        public override string Description =>
            "Pawns that are out of range of every piano, harp or harpsichord no longer search the rooms around them for one " +
            "being played when their mood thoughts are updated.";
        public override string Category => "Pawns";

        // Free Performance shortcuts the same thought worker.
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "brage.paralleltick" };

        private static readonly MethodInfo Target = AccessTools.Method(typeof(ThoughtWorker_MusicalInstrumentListeningBase), "CurrentStateInternal");
        private static readonly Assembly GameAssembly = typeof(Pawn).Assembly;

        private static Func<ThoughtWorker_MusicalInstrumentListeningBase, ThingDef> instrumentDef;

        public override IEnumerable<PatchSpec> GetPatches()
        {
            instrumentDef = AccessTools.MethodDelegate<Func<ThoughtWorker_MusicalInstrumentListeningBase, ThingDef>>(
                AccessTools.PropertyGetter(typeof(ThoughtWorker_MusicalInstrumentListeningBase), "InstrumentDef")
                ?? throw new MissingMethodException(nameof(ThoughtWorker_MusicalInstrumentListeningBase), "InstrumentDef"));
            yield return PatchSpec.For(Target)
                .WithPrefix(typeof(MusicListeningShortcut), nameof(Prefix), replacesOriginal: true, priority: Priority.Last);
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            // The search and everything it can do when no thing of the def is in range.
            AccessTools.Method(typeof(GenClosest), nameof(GenClosest.ClosestThingReachable)),
            AccessTools.Method(typeof(GenClosest), "EarlyOutSearch"),
            AccessTools.Method(typeof(GenClosest), nameof(GenClosest.RegionwiseBFSWorker)),
            AccessTools.Method(typeof(GenClosest), nameof(GenClosest.ClosestThing_Global)),
            AccessTools.Method(typeof(RegionProcessorClosestThingReachable), "RegionEntryPredicate"),
            AccessTools.Method(typeof(RegionProcessorClosestThingReachable), "RegionProcessor"),
            AccessTools.Method(typeof(RegionProcessorClosestThingReachable), "ProcessThing"),
            AccessTools.Method(typeof(RegionTraverser), nameof(RegionTraverser.BreadthFirstTraverse),
                new[] { typeof(Region), typeof(RegionProcessorDelegateCache), typeof(int), typeof(RegionType) }),
            AccessTools.Method(typeof(RegionTraverser), nameof(RegionTraverser.BreadthFirstTraverse),
                new[] { typeof(Region), typeof(RegionEntryPredicate), typeof(RegionProcessor), typeof(int), typeof(RegionType) }),
            AccessTools.Method(AccessTools.Inner(typeof(RegionTraverser), "BFSWorker"), "BreadthFirstTraverseWork"),
            AccessTools.Method(typeof(Region), nameof(Region.Allows)),
            AccessTools.Method(typeof(Building_Door), nameof(Building_Door.CanPhysicallyPass)),
            AccessTools.Method(typeof(Building_Door), nameof(Building_Door.PawnCanOpen)),
            AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.TryGetAvoidGrid)),
            AccessTools.Method(typeof(ReachabilityWithinRegion), nameof(ReachabilityWithinRegion.ThingFromRegionListerReachable)),
            AccessTools.Method(typeof(GenPath), nameof(GenPath.ResolveClosestTouchPathMode)),
            AccessTools.Method(typeof(GenPath), "ShouldNotEnterCell"),
            AccessTools.Method(typeof(RegionGrid), nameof(RegionGrid.GetValidRegionAt)),
            AccessTools.Method(typeof(RegionAndRoomUpdater), nameof(RegionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms)),
            AccessTools.PropertyGetter(typeof(RegionAndRoomUpdater), nameof(RegionAndRoomUpdater.AnythingToRebuild)),
        };

        public override void ClearCaches()
        {
            modDoorsOverride = null;
        }

        public static bool Prefix(ThoughtWorker_MusicalInstrumentListeningBase __instance, Pawn p, ref ThoughtState __result)
        {
            __result = CurrentState(__instance, p);
            return false;
        }

        /// <summary>Vanilla's CurrentStateInternal, with the shortcut before the search.</summary>
        private static ThoughtState CurrentState(ThoughtWorker_MusicalInstrumentListeningBase worker, Pawn p)
        {
            if (!p.health.capacities.CapableOf(PawnCapacityDefOf.Hearing))
                return false;
            ThingDef def = instrumentDef(worker);
            if (NothingInRange(p, def))
            {
                if (Verify.Enabled) Verify.Record(OptId, Search(p, def) == null);
                return false;
            }
            return Search(p, def) != null;
        }

        private static Thing Search(Pawn p, ThingDef def) =>
            GenClosest.ClosestThingReachable(p.Position, p.Map, ThingRequest.ForDef(def), PathEndMode.ClosestTouch, TraverseParms.For(p),
                def.building.instrumentRange,
                thing => thing is Building_MusicalInstrument instrument && instrument.IsBeingPlayed
                    && Building_MusicalInstrument.IsAffectedByInstrument(def, instrument.Position, p.Position, p.Map));

        /// <summary>True when the search provably returns null and skipping it is unobservable.</summary>
        private static bool NothingInRange(Pawn p, ThingDef def)
        {
            Verse.Map map = p.Map;
            if (map == null || def?.building == null) return false;
            IntVec3 root = p.Position;
            if (!root.InBounds(map) || map.regionAndRoomUpdater.AnythingToRebuild || ModDoorsOverride()) return false;
            if (p.TryGetAvoidGrid(out _)) return false;
            float maxDistance = def.building.instrumentRange;
            float maxDistanceSquared = maxDistance * maxDistance;
            List<Thing> things = map.listerThings.ThingsMatching(ThingRequest.ForDef(def));
            for (int i = 0; i < things.Count; i++)
            {
                if (!((root - things[i].PositionHeld).LengthHorizontalSquared > maxDistanceSquared))
                    return false;
            }
            return true;
        }

        // Null until computed; types cannot be added once the game runs.
        private static bool? modDoorsOverride;

        /// <summary>True if a door class from a mod declares PawnCanOpen or FreePassage (unknown code in Region.Allows).</summary>
        private static bool ModDoorsOverride()
        {
            if (modDoorsOverride is bool known) return known;
            bool result;
            try
            {
                result = typeof(Building_Door).AllSubclasses().Any(type =>
                {
                    for (Type c = type; c != null && c != typeof(Building_Door); c = c.BaseType)
                    {
                        if (c.Assembly == GameAssembly) continue;
                        foreach (MethodInfo m in c.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                            if (m.Name == nameof(Building_Door.PawnCanOpen) || m.Name == "get_" + nameof(Building_Door.FreePassage)) return true;
                    }
                    return false;
                });
            }
            catch (Exception)
            {
                result = true; // a type that cannot be inspected: run vanilla
            }
            modDoorsOverride = result;
            return result;
        }
    }
}
