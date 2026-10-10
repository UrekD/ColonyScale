using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Profile;
using Reservation = Verse.AI.ReservationManager.Reservation;

namespace ColonyScale.Optimizations.AI
{
    using Map = Verse.Map;

    /// <summary>
    /// ReservationManager's queries (CanReserve, TryGetReserver, FirstRespectedReserver, ReservedBy, ...) scan the
    /// map's whole reservation list, about two entries per busy pawn, and run inside nearly every work, haul and food
    /// search validator, several times per candidate. This keeps a per-map side index that buckets the same
    /// Reservation objects by target and runs vanilla's loop over the target's bucket instead of the whole list.
    ///
    /// Exact:
    /// - Buckets are the equivalence classes of LocalTargetInfo ==: a target with a thing equals exactly the targets
    ///   holding the same thing reference (thing vs. cell is never equal); without a thing, valid cells compare by
    ///   x/y/z and all invalid cells are equal to each other. Keys: thing reference / valid cell / one invalid bucket.
    /// - Every replaced loop first tests reservation.Target against the query target and does nothing else for
    ///   non-matching entries (no early return, no other call). A bucket is the list's subsequence of matching entries
    ///   in list order, so the same entries are visited in the same order: same first match, same per-claimant
    ///   de-duplication through the manager's own tmpReservers list, same calls into RespectsReservationsOf and
    ///   other code, same exceptions. The loops still test Target, so they also run unchanged over the full list.
    /// - The index never trusts a hook: before each use it compares the list reference and List._version with the
    ///   state it was built from. On a change it diffs a mirror copy of the old list against the live list (kept
    ///   entries in order, removed entries, appended suffix), which yields the exact buckets for any mutation by
    ///   anyone; if the diff does not apply it rebuilds. A list containing null or the same Reservation twice is not
    ///   indexed (vanilla would throw on a null wherever it sits), so such a list is scanned as vanilla does.
    /// - Reservation targets never change after construction except while loading, when cross-references resolve
    ///   inside ReservationManager.ExposeData; a postfix there drops the index.
    /// - CanReserve calls the private IsAlreadyReserved twice with identical arguments and only pure checks in
    ///   between (:188, :209); the second call always returns false, so it is dropped.
    ///
    /// - The diff on version change replaces per-mutator postfixes: it is incremental for vanilla's mutations (append,
    ///   Remove, RemoveAt loops) and also covers mutators nobody hooked (other mods, ExposeData, ToList+reassign).
    /// - RespectsReservationsOf is the only game code called while a bucket is iterated; it is read-only, and is in
    ///   DependsOn so a foreign patch on it (which could mutate the list mid-loop) turns this off.
    ///
    /// Threading: IsReservedAndRespected(LocalTargetInfo, Faction) is called from Parallel.ForEach workers
    /// (FactionSource via PathFinderMapData) and is left unpatched. Every prefix runs the untouched original when not
    /// on the main thread, so the index (and the verify counters) are never built, read or written off it.
    /// </summary>
    public sealed class ReservationIndex : Optimization
    {
        public override string Id => "reservationIndex";
        public override string Label => "Indexed reservation checks";
        public override string Description =>
            "Checks like \"can this pawn reserve that item\" look only at the reservations on that item instead of every " +
            "reservation on the map. Biggest effect with many pawns hauling, cooking and building at once.";
        public override string Category => "AI";

        // Performance Fish has its own reservation patch; Free Performance's "reservedup" rewrites CanReserve.
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "bs.performancefish", "brage.paralleltick" };

        private const string IdCanReserve = "reservationIndex.CanReserve";
        private const string IdCanReserveStack = "reservationIndex.CanReserveStack";
        private const string IdIsReserved = "reservationIndex.IsReserved";
        private const string IdReserversOf = "reservationIndex.ReserversOf";
        private const string IdTryGetReserver = "reservationIndex.TryGetReserver";
        private const string IdOnlyForJobDef = "reservationIndex.OnlyReservationsForJobDef";
        private const string IdFirstRespected = "reservationIndex.FirstRespectedReserver";
        private const string IdReservedBy = "reservationIndex.ReservedBy";
        private const string IdIndex = "reservationIndex.index";

        private static readonly Type RM = typeof(ReservationManager);

        private static readonly AccessTools.FieldRef<ReservationManager, List<Reservation>> reservationsRef =
            AccessTools.FieldRefAccess<ReservationManager, List<Reservation>>("reservations");
        private static readonly AccessTools.FieldRef<ReservationManager, Map> mapRef =
            AccessTools.FieldRefAccess<ReservationManager, Map>("map");
        private static readonly AccessTools.FieldRef<ReservationManager, List<Pawn>> tmpReserversRef =
            AccessTools.FieldRefAccess<ReservationManager, List<Pawn>>("tmpReservers");
        private static readonly AccessTools.FieldRef<List<Reservation>, int> versionRef =
            AccessTools.FieldRefAccess<List<Reservation>, int>("_version");

        // The original (and any patches on it): called on exactly the entries vanilla calls it on.
        private static readonly Func<Pawn, Pawn, bool> Respects = AccessTools.MethodDelegate<Func<Pawn, Pawn, bool>>(
            AccessTools.Method(RM, "RespectsReservationsOf", new[] { typeof(Pawn), typeof(Pawn) }));

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return Replace(nameof(ReservationManager.CanReserve),
                new[] { typeof(Pawn), typeof(LocalTargetInfo), typeof(int), typeof(int), typeof(ReservationLayerDef), typeof(bool) },
                nameof(CanReservePrefix), nameof(CanReservePostfix));
            yield return Replace(nameof(ReservationManager.CanReserveStack),
                new[] { typeof(Pawn), typeof(LocalTargetInfo), typeof(int), typeof(ReservationLayerDef), typeof(bool) },
                nameof(CanReserveStackPrefix), nameof(CanReserveStackPostfix));
            yield return Replace(nameof(ReservationManager.IsReserved),
                new[] { typeof(LocalTargetInfo) },
                nameof(IsReservedPrefix), nameof(IsReservedPostfix));
            yield return Replace(nameof(ReservationManager.ReserversOf),
                new[] { typeof(LocalTargetInfo), typeof(HashSet<Pawn>) },
                nameof(ReserversOfPrefix), nameof(ReserversOfPostfix));
            yield return Replace(nameof(ReservationManager.TryGetReserver),
                new[] { typeof(LocalTargetInfo), typeof(Faction), typeof(Pawn).MakeByRefType() },
                nameof(TryGetReserverPrefix), nameof(TryGetReserverPostfix));
            yield return Replace(nameof(ReservationManager.OnlyReservationsForJobDef),
                new[] { typeof(LocalTargetInfo), typeof(JobDef), typeof(bool) },
                nameof(OnlyReservationsForJobDefPrefix), nameof(OnlyReservationsForJobDefPostfix));
            yield return Replace(nameof(ReservationManager.FirstRespectedReserver),
                new[] { typeof(LocalTargetInfo), typeof(Pawn), typeof(ReservationLayerDef) },
                nameof(FirstRespectedReserverPrefix), nameof(FirstRespectedReserverPostfix));
            yield return Replace(nameof(ReservationManager.ReservedBy),
                new[] { typeof(LocalTargetInfo), typeof(Pawn), typeof(Job) },
                nameof(ReservedByPrefix), nameof(ReservedByPostfix));

            yield return PatchSpec.For(AccessTools.Method(RM, nameof(ReservationManager.ExposeData)))
                .WithPostfix(typeof(ReservationIndex), nameof(ExposeDataPostfix));
            yield return PatchSpec.For(AccessTools.Method(typeof(MemoryUtility), nameof(MemoryUtility.ClearAllMapsAndWorld)))
                .WithPostfix(typeof(ReservationIndex), nameof(ForgetLast)).AsHook();
            yield return PatchSpec.For(AccessTools.Method(typeof(MapDeiniter), nameof(MapDeiniter.Deinit)))
                .WithPostfix(typeof(ReservationIndex), nameof(ForgetLast)).AsHook();
        }

        private static PatchSpec Replace(string method, Type[] args, string prefix, string postfix) =>
            PatchSpec.For(AccessTools.Method(RM, method, args))
                .WithPrefix(typeof(ReservationIndex), prefix, replacesOriginal: true, priority: Priority.Last)
                .WithPostfix(typeof(ReservationIndex), postfix, priority: Priority.First);

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            // Bucketing reproduces LocalTargetInfo equality.
            AccessTools.Method(typeof(LocalTargetInfo), "op_Equality"),
            AccessTools.Method(typeof(LocalTargetInfo), "op_Inequality"),
            AccessTools.PropertyGetter(typeof(LocalTargetInfo), nameof(LocalTargetInfo.Thing)),
            AccessTools.PropertyGetter(typeof(LocalTargetInfo), nameof(LocalTargetInfo.Cell)),
            AccessTools.PropertyGetter(typeof(IntVec3), nameof(IntVec3.IsValid)),
            AccessTools.Method(typeof(IntVec3), "op_Equality"),
            AccessTools.PropertyGetter(typeof(Reservation), nameof(Reservation.Target)),
            // Reproduced inside CanReserve (and its second, redundant call dropped).
            AccessTools.Method(RM, "IsAlreadyReserved"),
            // Called mid-iteration over a bucket; must not mutate the list.
            AccessTools.Method(RM, "RespectsReservationsOf", new[] { typeof(Pawn), typeof(Pawn) }),
        };

        public override void ClearCaches()
        {
            indexes = new ConditionalWeakTable<ReservationManager, Index>();
            ForgetLast();
        }

        // ── Index ──────────────────────────────────────────────────────────────────

        private static ConditionalWeakTable<ReservationManager, Index> indexes = new ConditionalWeakTable<ReservationManager, Index>();
        private static readonly ConditionalWeakTable<ReservationManager, Index>.CreateValueCallback NewIndex = _ => new Index();

        // One-entry cache in front of the table (main thread only). It holds the last manager strongly, so it is
        // dropped whenever a map is removed (MapDeiniter.Deinit) or the game is cleared (ClearAllMapsAndWorld);
        // otherwise a removed map would stay alive until another map is queried.
        private static ReservationManager lastManager;
        private static Index lastIndex;

        public static void ForgetLast()
        {
            lastManager = null;
            lastIndex = null;
        }

        public static void ExposeDataPostfix(ReservationManager __instance)
        {
            // May run on the loading thread; only drops state (the main thread does not query while a long event runs).
            if (indexes.TryGetValue(__instance, out var index)) index.synced = false;
            ForgetLast();
        }

        private static readonly List<Reservation> Empty = new List<Reservation>();

        /// <summary>
        /// The list the replaced loops iterate: the target's bucket when the index is usable, else the live list.
        /// Either way the entries whose Target == target appear in the same order as in the live list.
        /// </summary>
        private static List<Reservation> Source(ReservationManager rm, LocalTargetInfo target)
        {
            List<Reservation> list = reservationsRef(rm);
            if (list == null) return list;
            Index index;
            if (ReferenceEquals(rm, lastManager)) index = lastIndex;
            else
            {
                index = indexes.GetValue(rm, NewIndex);
                lastManager = rm;
                lastIndex = index;
            }
            index.Sync(list);
            return index.usable ? index.Bucket(target) : list;
        }

        private sealed class ThingRefComparer : IEqualityComparer<Thing>
        {
            public static readonly ThingRefComparer Instance = new ThingRefComparer();
            public bool Equals(Thing a, Thing b) => ReferenceEquals(a, b);
            public int GetHashCode(Thing t) => RuntimeHelpers.GetHashCode(t);
        }

        private sealed class CellComparer : IEqualityComparer<IntVec3>
        {
            public static readonly CellComparer Instance = new CellComparer();
            public bool Equals(IntVec3 a, IntVec3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
            public int GetHashCode(IntVec3 c) => unchecked((c.x * 397) ^ (c.z * 7919) ^ c.y);
        }

        private sealed class Index
        {
            public bool synced;
            public bool usable;
            private List<Reservation> source;
            private int version;
            private readonly List<Reservation> mirror = new List<Reservation>();
            private readonly Dictionary<Thing, List<Reservation>> byThing = new Dictionary<Thing, List<Reservation>>(ThingRefComparer.Instance);
            private readonly Dictionary<IntVec3, List<Reservation>> byCell = new Dictionary<IntVec3, List<Reservation>>(CellComparer.Instance);
            private List<Reservation> invalid = new List<Reservation>();

            public void Sync(List<Reservation> list)
            {
                if (synced && ReferenceEquals(source, list) && version == versionRef(list)) return;
                bool diffed = synced && usable && ReferenceEquals(source, list) && TryDiff(list);
                if (!diffed) Rebuild(list);
                source = list;
                version = versionRef(list);
                synced = true;
                if (Verify.Enabled && usable) Verify.Record(IdIndex, MatchesFreshBuild(list));
            }

            /// <summary>Target's bucket; never null. Same key rules as LocalTargetInfo.operator==.</summary>
            public List<Reservation> Bucket(LocalTargetInfo target)
            {
                Thing thing = target.Thing;
                if (thing != null) return byThing.TryGetValue(thing, out var b) ? b : Empty;
                IntVec3 cell = target.Cell;
                if (cell.IsValid) return byCell.TryGetValue(cell, out var c) ? c : Empty;
                return invalid;
            }

            private List<Reservation> BucketForAdd(LocalTargetInfo target)
            {
                Thing thing = target.Thing;
                if (thing != null)
                {
                    if (!byThing.TryGetValue(thing, out var b)) byThing.Add(thing, b = new List<Reservation>(2));
                    return b;
                }
                IntVec3 cell = target.Cell;
                if (cell.IsValid)
                {
                    if (!byCell.TryGetValue(cell, out var c)) byCell.Add(cell, c = new List<Reservation>(2));
                    return c;
                }
                return invalid;
            }

            /// <summary>Appends r to its bucket; false if r is already there (same object twice in the list).</summary>
            private bool Append(Reservation r)
            {
                var bucket = BucketForAdd(r.Target);
                for (int k = 0; k < bucket.Count; k++)
                    if (ReferenceEquals(bucket[k], r)) return false;
                bucket.Add(r);
                return true;
            }

            private bool Remove(Reservation r)
            {
                LocalTargetInfo target = r.Target;
                List<Reservation> bucket = Bucket(target);
                int k = -1;
                for (int i = 0; i < bucket.Count; i++)
                    if (ReferenceEquals(bucket[i], r)) { k = i; break; }
                if (k < 0) return false;
                bucket.RemoveAt(k);
                if (bucket.Count == 0)
                {
                    // Drop empty buckets so destroyed things and old cells are not kept alive.
                    Thing thing = target.Thing;
                    if (thing != null) byThing.Remove(thing);
                    else if (target.Cell.IsValid) byCell.Remove(target.Cell);
                }
                return true;
            }

            private void Rebuild(List<Reservation> list)
            {
                // Fresh bucket lists: a caller still iterating an old bucket keeps an unchanged list.
                byThing.Clear();
                byCell.Clear();
                invalid = new List<Reservation>();
                mirror.Clear();
                usable = true;
                for (int i = 0; i < list.Count; i++)
                {
                    Reservation r = list[i];
                    if (r == null || !Append(r))
                    {
                        usable = false;
                        return;
                    }
                }
                mirror.AddRange(list);
            }

            /// <summary>
            /// Brings the buckets from the mirror (old list) to the live list: old entries that still appear in order
            /// are kept, the others are removed, and live entries after the last kept one are appended. Kept entries
            /// keep their relative order in both lists and appended ones follow them, so each bucket ends up as the
            /// live list's subsequence for its key, whatever mutation happened. False → caller rebuilds.
            /// </summary>
            private bool TryDiff(List<Reservation> list)
            {
                int n = mirror.Count, m = list.Count, j = 0;
                for (int i = 0; i < n; i++)
                {
                    Reservation r = mirror[i];
                    if (j < m && ReferenceEquals(list[j], r)) { j++; continue; }
                    if (!Remove(r)) return false;
                }
                for (; j < m; j++)
                {
                    Reservation r = list[j];
                    if (r == null || !Append(r)) return false;
                }
                mirror.Clear();
                mirror.AddRange(list);
                return true;
            }

            private bool MatchesFreshBuild(List<Reservation> list)
            {
                var fresh = new Index();
                fresh.Rebuild(list);
                if (!fresh.usable) return false;
                int total = invalid.Count;
                foreach (var b in byThing.Values) total += b.Count;
                foreach (var b in byCell.Values) total += b.Count;
                if (total != list.Count) return false;
                for (int i = 0; i < list.Count; i++)
                {
                    var target = list[i].Target;
                    List<Reservation> mine = Bucket(target), theirs = fresh.Bucket(target);
                    if (mine.Count != theirs.Count) return false;
                    for (int k = 0; k < mine.Count; k++)
                        if (!ReferenceEquals(mine[k], theirs[k])) return false;
                }
                return true;
            }
        }

        // ── Replaced queries: vanilla's bodies with `reservations` replaced by Source(...) ───────────────────────────

        private static bool CanReserveFast(ReservationManager rm, Pawn claimant, LocalTargetInfo target, int maxPawns, int stackCount,
            ReservationLayerDef layer, bool ignoreOtherReservations)
        {
            if (claimant == null)
            {
                Log.Error("CanReserve with null claimant");
                return false;
            }
            Map map = mapRef(rm);
            if (!claimant.Spawned || claimant.Map != map) return false;
            if (!target.IsValid || target.ThingDestroyed) return false;
            if (target.HasThing && target.Thing.SpawnedOrAnyParentSpawned && target.Thing.MapHeld != map) return false;
            int num = ((!target.HasThing) ? 1 : target.Thing.stackCount);
            int num2 = ((stackCount == -1) ? num : stackCount);
            if (num2 > num) return false;
            if (!ignoreOtherReservations)
            {
                if (map.physicalInteractionReservationManager.IsReserved(target) && !map.physicalInteractionReservationManager.IsReservedBy(claimant, target))
                    return false;
                if (IsAlreadyReserved(Source(rm, target), claimant, target, layer, num2)) return true;
                if (target.HasThing && target.Thing is Building building && building.def.hasInteractionCell)
                {
                    IntVec3 interactionCell = building.InteractionCell;
                    Building edifice = interactionCell.GetEdifice(claimant.Map);
                    if (edifice != null)
                    {
                        if (claimant.Map.reservationManager.TryGetReserver(edifice, claimant.Faction, out var reserver) && reserver.Spawned && reserver != claimant)
                            return false;
                    }
                    else if (claimant.Map.reservationManager.TryGetReserver(interactionCell, claimant.Faction, out var reserver2) && reserver2.Spawned && reserver2 != claimant)
                    {
                        return false;
                    }
                }
                // Vanilla's else-branch: IsAlreadyReserved again with the same arguments, after only pure checks → false.
                List<Pawn> tmpReservers = tmpReserversRef(rm);
                tmpReservers.Clear();
                int num3 = 0;
                int num4 = 0;
                List<Reservation> src = Source(rm, target);
                for (int i = 0; i < src.Count; i++)
                {
                    Reservation reservation = src[i];
                    if (!(reservation.Target != target) && reservation.Layer == layer && reservation.Claimant != claimant
                        && !tmpReservers.Contains(reservation.Claimant) && Respects(claimant, reservation.Claimant))
                    {
                        if (reservation.MaxPawns != maxPawns) return false;
                        tmpReservers.Add(reservation.Claimant);
                        num3++;
                        num4 = ((reservation.StackCount != -1) ? (num4 + reservation.StackCount) : (num4 + num));
                        if (num3 >= maxPawns || num2 + num4 > num) return false;
                    }
                }
            }
            return true;
        }

        private static bool IsAlreadyReserved(List<Reservation> src, Pawn claimant, LocalTargetInfo target, ReservationLayerDef layer, int toReserveResolved)
        {
            for (int i = 0; i < src.Count; i++)
            {
                Reservation reservation = src[i];
                if (reservation.Target == target && reservation.Layer == layer && reservation.Claimant == claimant
                    && (reservation.StackCount == -1 || reservation.StackCount >= toReserveResolved))
                    return true;
            }
            return false;
        }

        private static int CanReserveStackFast(ReservationManager rm, Pawn claimant, LocalTargetInfo target, int maxPawns, ReservationLayerDef layer,
            bool ignoreOtherReservations)
        {
            if (claimant == null)
            {
                Log.Error("CanReserve with null claimant");
                return 0;
            }
            Map map = mapRef(rm);
            if (!claimant.Spawned || claimant.Map != map) return 0;
            if (!target.IsValid || target.ThingDestroyed) return 0;
            if (target.HasThing && target.Thing.SpawnedOrAnyParentSpawned && target.Thing.MapHeld != map) return 0;
            int num = ((!target.HasThing) ? 1 : target.Thing.stackCount);
            int num2 = 0;
            if (!ignoreOtherReservations)
            {
                if (map.physicalInteractionReservationManager.IsReserved(target) && !map.physicalInteractionReservationManager.IsReservedBy(claimant, target))
                    return 0;
                int num3 = 0;
                List<Reservation> src = Source(rm, target);
                for (int i = 0; i < src.Count; i++)
                {
                    Reservation reservation = src[i];
                    if (!(reservation.Target != target) && reservation.Layer == layer && reservation.Claimant != claimant && Respects(claimant, reservation.Claimant))
                    {
                        if (reservation.MaxPawns != maxPawns) return 0;
                        num3++;
                        num2 = ((reservation.StackCount != -1) ? (num2 + reservation.StackCount) : (num2 + num));
                        if (num3 >= maxPawns || num2 >= num) return 0;
                    }
                }
            }
            return Mathf.Max(num - num2, 0);
        }

        private static bool IsReservedFast(ReservationManager rm, LocalTargetInfo target)
        {
            List<Reservation> src = Source(rm, target);
            for (int i = 0; i < src.Count; i++)
                if (src[i].Target == target) return true;
            return false;
        }

        private static void ReserversOfFast(ReservationManager rm, LocalTargetInfo target, HashSet<Pawn> reserversOut)
        {
            mapRef(rm).physicalInteractionReservationManager.ReserversOf(target, reserversOut);
            List<Reservation> src = Source(rm, target);
            for (int i = 0; i < src.Count; i++)
            {
                Reservation reservation = src[i];
                if (reservation.Target == target) reserversOut.Add(reservation.Claimant);
            }
        }

        private static bool TryGetReserverFast(ReservationManager rm, LocalTargetInfo target, Faction faction, out Pawn reserver)
        {
            reserver = null;
            if (!target.IsValid) return false;
            List<Reservation> src = Source(rm, target);
            for (int i = 0; i < src.Count; i++)
            {
                Reservation reservation = src[i];
                if (reservation.Target == target && reservation.Claimant.Faction == faction)
                {
                    reserver = reservation.Claimant;
                    return true;
                }
            }
            return false;
        }

        private static bool OnlyReservationsForJobDefFast(ReservationManager rm, LocalTargetInfo target, JobDef requiredJobDef, bool requireAtLeastOne)
        {
            if (!target.IsValid) return false;
            bool flag = false;
            List<Reservation> src = Source(rm, target);
            for (int i = 0; i < src.Count; i++)
            {
                Reservation reservation = src[i];
                if (!(reservation.Target != target))
                {
                    if (reservation.Job.def != requiredJobDef) return false;
                    flag = true;
                }
            }
            return !requireAtLeastOne || flag;
        }

        private static Pawn FirstRespectedReserverFast(ReservationManager rm, LocalTargetInfo target, Pawn claimant, ReservationLayerDef layer)
        {
            if (!target.IsValid) return null;
            List<Reservation> src = Source(rm, target);
            for (int i = 0; i < src.Count; i++)
            {
                Reservation reservation = src[i];
                if (reservation.Target == target && (layer == null || reservation.Layer == layer) && Respects(claimant, reservation.Claimant))
                    return reservation.Claimant;
            }
            return null;
        }

        private static bool ReservedByFast(ReservationManager rm, LocalTargetInfo target, Pawn claimant, Job job)
        {
            if (!target.IsValid) return false;
            List<Reservation> src = Source(rm, target);
            for (int i = 0; i < src.Count; i++)
            {
                Reservation reservation = src[i];
                if (reservation.Target == target && reservation.Claimant == claimant && (job == null || reservation.Job == job))
                    return true;
            }
            return false;
        }

        // ── Harmony glue. Off the main thread the original runs untouched. Verify mode: compute the fast result, let
        //    vanilla run, compare in a postfix. ─────────────────────────────────────────────────────────────────────

        public static bool CanReservePrefix(ReservationManager __instance, Pawn claimant, LocalTargetInfo target, int maxPawns, int stackCount,
            ReservationLayerDef layer, bool ignoreOtherReservations, ref bool __result, out bool? __state)
        {
            __state = null;
            if (!UnityData.IsInMainThread) return true;
            bool fast = CanReserveFast(__instance, claimant, target, maxPawns, stackCount, layer, ignoreOtherReservations);
            return Finish(fast, ref __result, out __state);
        }

        public static bool CanReserveStackPrefix(ReservationManager __instance, Pawn claimant, LocalTargetInfo target, int maxPawns,
            ReservationLayerDef layer, bool ignoreOtherReservations, ref int __result, out int? __state)
        {
            __state = null;
            if (!UnityData.IsInMainThread) return true;
            int fast = CanReserveStackFast(__instance, claimant, target, maxPawns, layer, ignoreOtherReservations);
            if (Verify.Enabled) { __state = fast; return true; }
            __result = fast;
            return false;
        }

        public static bool IsReservedPrefix(ReservationManager __instance, LocalTargetInfo target, ref bool __result, out bool? __state)
        {
            __state = null;
            return !UnityData.IsInMainThread || Finish(IsReservedFast(__instance, target), ref __result, out __state);
        }

        public static bool ReserversOfPrefix(ReservationManager __instance, LocalTargetInfo target, HashSet<Pawn> reserversOut, out HashSet<Pawn> __state)
        {
            __state = null;
            if (!UnityData.IsInMainThread) return true;
            if (Verify.Enabled)
            {
                // Fast result on a copy; vanilla then fills the caller's set and the postfix compares.
                __state = new HashSet<Pawn>(reserversOut, reserversOut.Comparer);
                ReserversOfFast(__instance, target, __state);
                return true;
            }
            ReserversOfFast(__instance, target, reserversOut);
            return false;
        }

        public static bool TryGetReserverPrefix(ReservationManager __instance, LocalTargetInfo target, Faction faction, ref Pawn reserver,
            ref bool __result, out object __state)
        {
            __state = null;
            if (!UnityData.IsInMainThread) return true;
            bool found = TryGetReserverFast(__instance, target, faction, out Pawn fastReserver);
            if (Verify.Enabled)
            {
                __state = new KeyValuePair<bool, Pawn>(found, fastReserver);
                return true;
            }
            reserver = fastReserver;
            __result = found;
            return false;
        }

        public static bool OnlyReservationsForJobDefPrefix(ReservationManager __instance, LocalTargetInfo target, JobDef requiredJobDef,
            bool requireAtLeastOne, ref bool __result, out bool? __state)
        {
            __state = null;
            return !UnityData.IsInMainThread
                || Finish(OnlyReservationsForJobDefFast(__instance, target, requiredJobDef, requireAtLeastOne), ref __result, out __state);
        }

        public static bool FirstRespectedReserverPrefix(ReservationManager __instance, LocalTargetInfo target, Pawn claimant, ReservationLayerDef layer,
            ref Pawn __result, out object __state)
        {
            __state = null;
            if (!UnityData.IsInMainThread) return true;
            Pawn fast = FirstRespectedReserverFast(__instance, target, claimant, layer);
            if (Verify.Enabled)
            {
                __state = new StrongBox<Pawn>(fast);
                return true;
            }
            __result = fast;
            return false;
        }

        public static bool ReservedByPrefix(ReservationManager __instance, LocalTargetInfo target, Pawn claimant, Job job, ref bool __result, out bool? __state)
        {
            __state = null;
            return !UnityData.IsInMainThread || Finish(ReservedByFast(__instance, target, claimant, job), ref __result, out __state);
        }

        private static bool Finish(bool fast, ref bool result, out bool? state)
        {
            state = null;
            if (Verify.Enabled)
            {
                state = fast;
                return true;
            }
            result = fast;
            return false;
        }

        public static void CanReservePostfix(bool __result, bool? __state) { if (__state.HasValue) Verify.Record(IdCanReserve, __result == __state.Value); }
        public static void IsReservedPostfix(bool __result, bool? __state) { if (__state.HasValue) Verify.Record(IdIsReserved, __result == __state.Value); }
        public static void OnlyReservationsForJobDefPostfix(bool __result, bool? __state) { if (__state.HasValue) Verify.Record(IdOnlyForJobDef, __result == __state.Value); }
        public static void ReservedByPostfix(bool __result, bool? __state) { if (__state.HasValue) Verify.Record(IdReservedBy, __result == __state.Value); }

        public static void CanReserveStackPostfix(int __result, int? __state)
        {
            if (__state.HasValue) Verify.Record(IdCanReserveStack, __result == __state.Value);
        }

        public static void ReserversOfPostfix(HashSet<Pawn> reserversOut, HashSet<Pawn> __state)
        {
            if (__state != null) Verify.Record(IdReserversOf, __state.SetEquals(reserversOut));
        }

        public static void TryGetReserverPostfix(ref Pawn reserver, bool __result, object __state)
        {
            if (__state is KeyValuePair<bool, Pawn> fast) Verify.Record(IdTryGetReserver, fast.Key == __result && fast.Value == reserver);
        }

        public static void FirstRespectedReserverPostfix(Pawn __result, object __state)
        {
            if (__state is StrongBox<Pawn> fast) Verify.Record(IdFirstRespected, fast.Value == __result);
        }
    }
}
