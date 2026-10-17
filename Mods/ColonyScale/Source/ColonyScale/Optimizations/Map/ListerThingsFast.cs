using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale.Optimizations.Map
{
    /// <summary>
    /// ListerThings.Add/Remove/Contains, used for the map lister (30-80k things late game) and for every
    /// region lister a pawn steps through. Vanilla tests all ~80 thing groups per call through
    /// ThingRequestGroup.Includes (reflection-heavy) and removes with RemoveSwap (1.7: List.IndexOf, a linear scan
    /// calling Thing.Equals per element, then the last element moves into the hole). Here the groups a def belongs to are computed once per (def, use) by calling
    /// vanilla Includes, and removal from the map lister searches from both ends where that is provably exact.
    ///
    /// Exact: lists, hashes and callbacks are updated in vanilla's order. Includes reads only the def's fields and
    /// ModsConfig; the cache is dropped when def fields can change (hot reload, new game/load: <see cref="CacheResets"/>),
    /// so it assumes no mod mutates thing defs in the middle of a game. Def indices are reused, so entries also
    /// check the def reference.
    /// Removal: region lists are small and use the exact scan (<see cref="FastList.IndexOf"/>). For global listers an
    /// <see cref="Occurrences"/> per lister counts each Thing.Equals class added minus removed, raised to the per-list
    /// maximum when (re)built from the lists. A Remove touches the def list plus the cached groups' lists, the same
    /// set every Add of that class touched since the cache was built, so no touched list holds more equal elements
    /// than the count: a count of 1 makes the two-ended reference search exact, anything else scans front to back.
    /// The found index is then removed the way vanilla's RemoveSwap does (<see cref="FastList.RemoveSwap"/>); swapping
    /// changes order but not which elements each list holds, so the counts stay valid.
    /// ListerThings.Clear drops the counts. Assumes no mod mutates the lists ThingsMatching returns.
    ///
    /// Known difference, out of scope: if Includes throws (a malformed def, e.g. a Plant def without a plant block),
    /// the groups are all evaluated before any group list is touched, so the thing ends up only in the def list and
    /// haul sources, where vanilla would also have added it to the group lists before the throwing group. The
    /// exception itself and its repetition on every later Add/Remove of that def are the same, and vanilla already
    /// logs it from GenSpawn.Spawn.
    /// </summary>
    public sealed class ListerThingsFast : Optimization
    {
        public override string Id => "listerThings";
        public override string Label => "Faster thing listers";
        public override string Description =>
            "Spawning, despawning and pawn movement update the map and region thing lists without testing every thing group " +
            "or scanning whole lists. Biggest effect in fights (projectiles) and on large maps.";
        public override string Category => "Map";

        private static readonly AccessTools.FieldRef<ListerThings, Dictionary<ThingDef, List<Thing>>> listsByDef =
            AccessTools.FieldRefAccess<ListerThings, Dictionary<ThingDef, List<Thing>>>("listsByDef");
        private static readonly AccessTools.FieldRef<ListerThings, List<Thing>[]> listsByGroup =
            AccessTools.FieldRefAccess<ListerThings, List<Thing>[]>("listsByGroup");
        private static readonly AccessTools.FieldRef<ListerThings, List<IHaulSource>> haulSources =
            AccessTools.FieldRefAccess<ListerThings, List<IHaulSource>>("haulSources");
        private static readonly AccessTools.FieldRef<ListerThings, int[]> stateHashByGroup =
            AccessTools.FieldRefAccess<ListerThings, int[]>("stateHashByGroup");

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(ListerThings), nameof(ListerThings.Add)))
                .WithPrefix(typeof(ListerThingsFast), nameof(AddPrefix), replacesOriginal: true, priority: Priority.Last);
            yield return PatchSpec.For(AccessTools.Method(typeof(ListerThings), nameof(ListerThings.Remove)))
                .WithPrefix(typeof(ListerThingsFast), nameof(RemovePrefix), replacesOriginal: true, priority: Priority.Last);
            yield return PatchSpec.For(AccessTools.Method(typeof(ListerThings), nameof(ListerThings.Contains)))
                .WithPrefix(typeof(ListerThingsFast), nameof(ContainsPrefix), replacesOriginal: true, priority: Priority.Last);
            yield return PatchSpec.For(AccessTools.Method(typeof(ListerThings), nameof(ListerThings.Clear)))
                .WithPrefix(typeof(ListerThingsFast), nameof(ClearPrefix));
            foreach (var p in CacheResets.Patches(typeof(ListerThingsFast), nameof(ResetCaches))) yield return p;
        }

        public override IEnumerable<System.Reflection.MethodBase> DependsOn => new System.Reflection.MethodBase[]
        {
            AccessTools.Method(typeof(ListerThings), "GroupIncludes"),
            AccessTools.Method(typeof(ListerThings), nameof(ListerThings.EverListable)),
            AccessTools.Method(typeof(ThingListGroupHelper), nameof(ThingListGroupHelper.Includes)),
            AccessTools.Method(typeof(ThingRequestGroupUtility), nameof(ThingRequestGroupUtility.StoreInRegion)),
            AccessTools.PropertyGetter(typeof(ListerThings), nameof(ListerThings.AllThings)),
            AccessTools.Method(typeof(Thing), nameof(Thing.Equals), new[] { typeof(Thing) }),
        };

        public override void ClearCaches() => ResetCaches();

        public static void ResetCaches()
        {
            GroupCache.Clear();
            counts = new ConditionalWeakTable<ListerThings, Occurrences>();
        }

        private static ConditionalWeakTable<ListerThings, Occurrences> counts = new ConditionalWeakTable<ListerThings, Occurrences>();
        private static readonly Dictionary<Thing, int> scratch = Occurrences.NewScratch();

        /// <summary>Counts for a global lister, built from its lists on first use; null where the exact scan is used.</summary>
        private static Occurrences CountsFor(ListerThings lister)
        {
            if (lister.use != ListerThingsUse.Global || !FastList.VanillaEquality) return null;
            if (counts.TryGetValue(lister, out var occ)) return occ;
            occ = new Occurrences();
            foreach (var list in listsByDef(lister).Values) occ.CoverList(list, scratch);
            foreach (var list in listsByGroup(lister))
                if (list != null) occ.CoverList(list, scratch);
            counts.Add(lister, occ);
            return occ;
        }

        /// <summary>Per (def, use): the groups the def is listed in, as vanilla's Add (enum value) and Remove (array position) index them.</summary>
        private sealed class Groups
        {
            public ThingDef def;
            public int[] values;
            public int[] positions;
        }

        private static class GroupCache
        {
            private static Groups[][] byUse = new Groups[2][];

            public static void Clear() => byUse = new Groups[2][];

            public static Groups For(ThingDef def, ListerThingsUse use)
            {
                int u = use == ListerThingsUse.Region ? 1 : 0;
                var table = byUse[u];
                int idx = def.index;
                if (table != null && idx < table.Length && table[idx]?.def == def) return table[idx];

                var values = new List<int>();
                var positions = new List<int>();
                var all = ThingListGroupHelper.AllGroups;
                for (int i = 0; i < all.Length; i++)
                {
                    var group = all[i];
                    if ((use != ListerThingsUse.Region || group.StoreInRegion()) && group.Includes(def))
                    {
                        values.Add((int)group);
                        positions.Add(i);
                    }
                }
                var entry = new Groups { def = def, values = values.ToArray(), positions = positions.ToArray() };
                if (table == null || idx >= table.Length)
                {
                    var grown = new Groups[System.Math.Max(idx + 1, DefDatabase<ThingDef>.DefCount + 16)];
                    table?.CopyTo(grown, 0);
                    byUse[u] = table = grown;
                }
                table[idx] = entry;
                return entry;
            }
        }

        public static bool AddPrefix(ListerThings __instance, Thing t)
        {
            if (!ListerThings.EverListable(t.def, __instance.use)) return false;
            var byDef = listsByDef(__instance);
            if (!byDef.TryGetValue(t.def, out var value))
            {
                value = new List<Thing>();
                byDef.Add(t.def, value);
            }
            value.Add(t);
            if (t is IHaulSource item) haulSources(__instance).Add(item);

            var groups = GroupCache.For(t.def, __instance.use);
            var lists = listsByGroup(__instance);
            var hashes = stateHashByGroup(__instance);
            int[] values = groups.values;
            for (int k = 0; k < values.Length; k++)
            {
                int g = values[k];
                var list = lists[g];
                if (list == null)
                {
                    list = new List<Thing>();
                    lists[g] = list;
                    hashes[g] = 0;
                }
                list.Add(t);
                hashes[g]++;
            }
            if (__instance.use == ListerThingsUse.Global && counts.TryGetValue(__instance, out var occ)) occ.Added(t);
            __instance.thingListChangedCallbacks?.onThingAdded?.Invoke(t);
            return false;
        }

        public static bool RemovePrefix(ListerThings __instance, Thing t)
        {
            if (!ListerThings.EverListable(t.def, __instance.use)) return false;
            var occ = CountsFor(__instance);
            int atMost = occ?.Count(t) ?? int.MaxValue;
            if (listsByDef(__instance).TryGetValue(t.def, out var value)) FastList.RemoveSwap(value, t, atMost);
            if (t is IHaulSource item) haulSources(__instance).Remove(item);

            var groups = GroupCache.For(t.def, __instance.use);
            var lists = listsByGroup(__instance);
            var hashes = stateHashByGroup(__instance);
            for (int k = 0; k < groups.positions.Length; k++)
            {
                // Same NullReferenceException as vanilla if the group list was never created.
                FastList.RemoveSwap(lists[groups.positions[k]], t, atMost);
                hashes[groups.values[k]]++;
            }
            occ?.Removed(t);
            __instance.thingListChangedCallbacks?.onThingRemoved?.Invoke(t);
            return false;
        }

        public static void ClearPrefix(ListerThings __instance) => counts.Remove(__instance);

        public static bool ContainsPrefix(ListerThings __instance, Thing t, ref bool __result)
        {
            __result = FastList.Contains(__instance.AllThings, t);
            return false;
        }
    }
}
