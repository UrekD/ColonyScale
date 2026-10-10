using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale.Optimizations.Map
{
    /// <summary>
    /// Room.UpdateRoomStatsAndRole scores every RoomStatDef and every RoomRoleDef. Almost every worker starts with
    /// room.ContainedAndAdjacentThings, which rebuilds the room's thing list through a HashSet over all its regions,
    /// so one update rebuilds the same list ~25 times; the stat defs are also re-sorted with LINQ on every update.
    /// Here the body is replaced by an equivalent one that sorts the stat defs once (re-sorting whenever the def list
    /// or any updatePriority changes) and, while the update runs, lets ContainedAndAdjacentThings reuse its last result
    /// when provably nothing it reads has changed.
    ///
    /// Exact:
    /// - The replacement executes vanilla's statements in vanilla's order (dirty flag, ProperRoom/RegionCount, DefMap
    ///   creation, stats field loaded before each GetScore, Worker getters, GenCollection.MaxBy over AllDefs).
    ///   OrderByDescending is a stable sort over the same list, so the cached array (built with the same LINQ call)
    ///   has the same order; it is rebuilt if the list instance, its elements or any updatePriority differ.
    /// - ContainedAndAdjacentThings returns the room's own uniqueContainedThings list, rebuilt from scratch; its
    ///   contents are a pure function of the region sequence from Room.Regions and, per region, the contents of
    ///   ListerThings.AllThings. A reuse first calls the real Room.Regions (vanilla's own side effect on tmpRegions),
    ///   then requires the same region objects in the same order, the same AllThings list objects with unchanged
    ///   List._version (every List mutation bumps it), and the result list itself untouched since we produced it.
    ///   Then a rebuild would produce the identical list, so returning it unchanged is identical. The result list's
    ///   _version is advanced as Clear + Count Adds would, so enumerators held across the call fail as in vanilla.
    ///   Any mismatch, a nested update or another room falls through to vanilla.
    /// - No worker needs to be trusted: a worker that changes things, regions or the list only causes a rebuild.
    /// Main thread only.
    /// </summary>
    public sealed class RoomStatsMemo : Optimization
    {
        public override string Id => "roomStatsMemo";
        public override string Label => "Faster room stat updates";
        public override string Description =>
            "When a room's impressiveness, beauty, wealth and role are recalculated, its list of contained things is built " +
            "once instead of once per stat and role.";
        public override string Category => "Map";

        private static readonly AccessTools.FieldRef<Room, bool> statsAndRoleDirty = AccessTools.FieldRefAccess<Room, bool>("statsAndRoleDirty");
        private static readonly AccessTools.FieldRef<Room, DefMap<RoomStatDef, float>> statsField =
            AccessTools.FieldRefAccess<Room, DefMap<RoomStatDef, float>>("stats");
        private static readonly AccessTools.FieldRef<Room, RoomRoleDef> roleField = AccessTools.FieldRefAccess<Room, RoomRoleDef>("role");
        private static readonly AccessTools.FieldRef<Room, List<Thing>> uniqueContainedThings =
            AccessTools.FieldRefAccess<Room, List<Thing>>("uniqueContainedThings");
        private static readonly AccessTools.FieldRef<Room, List<Region>> tmpRegions = AccessTools.FieldRefAccess<Room, List<Region>>("tmpRegions");
        private static readonly AccessTools.FieldRef<List<Thing>, int> listVersion = AccessTools.FieldRefAccess<List<Thing>, int>("_version");

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(Room), "UpdateRoomStatsAndRole"))
                .WithPrefix(typeof(RoomStatsMemo), nameof(UpdatePrefix), replacesOriginal: true, priority: Priority.Last);
            yield return PatchSpec.For(AccessTools.PropertyGetter(typeof(Room), nameof(Room.ContainedAndAdjacentThings)))
                .WithPrefix(typeof(RoomStatsMemo), nameof(ThingsPrefix), replacesOriginal: true, priority: Priority.Last)
                .WithPostfix(typeof(RoomStatsMemo), nameof(ThingsPostfix), priority: Priority.First);
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            // Listed so that any foreign patch on it (e.g. a postfix editing the shared list) keeps this off.
            AccessTools.PropertyGetter(typeof(Room), nameof(Room.ContainedAndAdjacentThings)),
            AccessTools.PropertyGetter(typeof(Room), nameof(Room.Regions)),
            AccessTools.PropertyGetter(typeof(Region), nameof(Region.ListerThings)),
            AccessTools.PropertyGetter(typeof(ListerThings), nameof(ListerThings.AllThings)),
            // The HashSet dedup in the rebuild uses these; the list is a pure function of its inputs only while they are.
            AccessTools.Method(typeof(Thing), nameof(Thing.Equals), new[] { typeof(Thing) }),
            AccessTools.Method(typeof(Thing), nameof(Thing.GetHashCode)),
        };

        public override void ClearCaches()
        {
            scopeRoom = null;
            memoValid = false;
            snapRegions = new Region[0];
            snapLists = new List<Thing>[0];
            snapVersions = new int[0];
            snapResult = null;
            orderSource = null;
            orderDefs = null;
            orderPriorities = null;
            orderSorted = null;
        }

        // ── Sorted stat defs ────────────────────────────────

        private static List<RoomStatDef> orderSource;
        private static RoomStatDef[] orderDefs;
        private static float[] orderPriorities;
        private static RoomStatDef[] orderSorted;

        private static RoomStatDef[] SortedStatDefs()
        {
            List<RoomStatDef> source = DefDatabase<RoomStatDef>.AllDefsListForReading;
            if (orderSorted != null && ReferenceEquals(source, orderSource) && source.Count == orderDefs.Length)
            {
                bool same = true;
                for (int i = 0; i < orderDefs.Length; i++)
                {
                    RoomStatDef def = source[i];
                    if (!ReferenceEquals(def, orderDefs[i]) || def.updatePriority != orderPriorities[i]) { same = false; break; }
                }
                if (same)
                {
                    if (Verify.Enabled)
                        Verify.Record("roomStatsMemo.order", orderSorted.SequenceEqual(DefDatabase<RoomStatDef>.AllDefs.OrderByDescending(x => x.updatePriority)));
                    return orderSorted;
                }
            }
            orderSource = source;
            orderDefs = source.ToArray();
            orderPriorities = new float[orderDefs.Length];
            for (int i = 0; i < orderDefs.Length; i++) orderPriorities[i] = orderDefs[i].updatePriority;
            orderSorted = DefDatabase<RoomStatDef>.AllDefs.OrderByDescending(x => x.updatePriority).ToArray();
            return orderSorted;
        }

        // ── Update scope ────────────────────────────────────

        /// <summary>Room whose UpdateRoomStatsAndRole is running (innermost); only its list is reused.</summary>
        private static Room scopeRoom;

        public static bool UpdatePrefix(Room __instance)
        {
            Room room = __instance;
            statsAndRoleDirty(room) = false;
            if (room.ProperRoom && room.RegionCount <= 60)
            {
                if (statsField(room) == null) statsField(room) = new DefMap<RoomStatDef, float>();
                Room outer = scopeRoom;
                scopeRoom = room;
                memoValid = false;
                try
                {
                    RoomStatDef[] order = SortedStatDefs();
                    for (int i = 0; i < order.Length; i++)
                    {
                        RoomStatDef item = order[i];
                        DefMap<RoomStatDef, float> stats = statsField(room); // vanilla loads the field before scoring
                        stats[item] = item.Worker.GetScore(room);
                    }
                    roleField(room) = BestRole(room);
                }
                finally
                {
                    scopeRoom = outer;
                    memoValid = false; // the outer room (if any) rebuilds on its next call
                }
            }
            else
            {
                statsField(room) = null;
                roleField(room) = RoomRoleDefOf.None;
            }
            return false;
        }

        /// <summary>
        /// Vanilla's role pick. The capturing lambda lives here so its closure is only allocated when roles are scored,
        /// not at entry of <see cref="UpdatePrefix"/> (which also runs for rooms that take the else branch).
        /// </summary>
        private static RoomRoleDef BestRole(Room room) =>
            GenCollection.MaxBy(DefDatabase<RoomRoleDef>.AllDefs, (RoomRoleDef x) => x.Worker.GetScore(room));

        // ── ContainedAndAdjacentThings memo ─────────────────

        private static bool memoValid;
        private static int snapCount;
        private static Region[] snapRegions = new Region[0];
        private static List<Thing>[] snapLists = new List<Thing>[0];
        private static int[] snapVersions = new int[0];
        private static List<Thing> snapResult;
        private static int snapResultVersion;
        private static readonly List<Thing> verifyCopy = new List<Thing>();

        /// <summary>__state: 0 = not ours, 1 = vanilla builds and we snapshot, 2 = verify: vanilla rebuilds a reusable list.</summary>
        public static bool ThingsPrefix(Room __instance, ref List<Thing> __result, out int __state)
        {
            __state = 0;
            if (!ReferenceEquals(__instance, scopeRoom)) return true;
            __state = 1;
            if (!memoValid || !Unchanged(__instance)) return true;

            List<Thing> list = uniqueContainedThings(__instance);
            if (Verify.Enabled)
            {
                verifyCopy.Clear();
                verifyCopy.AddRange(list);
                __state = 2;
                return true;
            }
            listVersion(list) += 1 + list.Count; // as vanilla's Clear() and Count Add() calls would
            snapResultVersion = listVersion(list);
            __result = list;
            __state = 0;
            return false;
        }

        public static void ThingsPostfix(Room __instance, List<Thing> __result, int __state)
        {
            if (__state == 0) return;
            if (__state == 2)
            {
                bool same = verifyCopy.Count == __result.Count;
                for (int i = 0; same && i < verifyCopy.Count; i++) same = ReferenceEquals(verifyCopy[i], __result[i]);
                Verify.Record("roomStatsMemo", same);
                verifyCopy.Clear();
            }
            Snapshot(__instance, __result);
        }

        /// <summary>Records what vanilla's build just read: tmpRegions is exactly the region list it iterated.</summary>
        private static void Snapshot(Room room, List<Thing> result)
        {
            List<Region> regions = tmpRegions(room);
            int n = regions.Count;
            if (snapRegions.Length < n)
            {
                snapRegions = new Region[n * 2];
                snapLists = new List<Thing>[n * 2];
                snapVersions = new int[n * 2];
            }
            for (int i = 0; i < n; i++)
            {
                Region region = regions[i];
                List<Thing> things = region.ListerThings.AllThings;
                snapRegions[i] = region;
                snapLists[i] = things;
                snapVersions[i] = things == null ? 0 : listVersion(things);
            }
            snapCount = n;
            snapResult = result;
            snapResultVersion = listVersion(result);
            memoValid = true;
        }

        private static bool Unchanged(Room room)
        {
            List<Region> regions = room.Regions; // the real getter: same tmpRegions rebuild vanilla's build starts with
            if (regions.Count != snapCount) return false;
            for (int i = 0; i < snapCount; i++)
            {
                Region region = regions[i];
                if (!ReferenceEquals(region, snapRegions[i])) return false;
                List<Thing> things = region.ListerThings.AllThings;
                if (!ReferenceEquals(things, snapLists[i])) return false;
                if (things != null && listVersion(things) != snapVersions[i]) return false;
            }
            List<Thing> result = uniqueContainedThings(room);
            return ReferenceEquals(result, snapResult) && listVersion(result) == snapResultVersion;
        }
    }
}
