using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale.Optimizations.Map
{
    /// <summary>
    /// ListerHaulables.CellsCheckTick picks one slot group per tick and recalculates 4 consecutive cells of it, wrapping
    /// modulo the group's cell count. For a group of 1-3 cells the same cell is therefore recalculated several times in
    /// one call, and every recalculation runs ShouldBeHaulable for each thing in the cell (a best-storage search).
    /// Its single RecalcAllInCell call is swapped for a helper that skips a cell already recalculated earlier in the
    /// same CellsCheckTick call. The cycle counters are vanilla's own and are untouched.
    ///
    /// Exact: RecalcAllInCell(c) runs Check(t) for each thing in c; Check writes only the haulables set (add or remove,
    /// both idempotent) from ShouldBeHaulable(t). ShouldBeHaulable never reads the haulables set and writes no game
    /// state: IsForbidden, the designation lookup, IsInAnyStorage, ParentHolder and IsInValidBestStorage are reads, and
    /// IsInValidBestStorage passes needAccurateResult: false, so the Rand.Range in TryFindBestBetterStoreCellForWorker
    /// is not evaluated (its start count is 0), TryFindBestBetterNonSlotGroupStorageFor has no randomness, and the
    /// carrier is null so no reachability or reservation call (CanReserveNew, CanReach) happens. Within one
    /// CellsCheckTick call nothing else runs between the recalculations except other Recalc calls, which only touch the
    /// haulables set. So a second recalculation of the same cell computes the same answers and leaves the set as it
    /// is: skipping it changes no state and consumes no Rand. The seen-cell buffer is reset by a prefix on
    /// CellsCheckTick; a cell is recorded only after its recalculation returned, so an exception leaves it unrecorded.
    /// Verify mode runs the skipped recalculation anyway and checks that the haulable membership of the cell's things
    /// did not change. Main thread only (Map.MapPostTick).
    /// </summary>
    public sealed class HaulablesCellDedupe : Optimization
    {
        private const string OptId = "haulablesCellDedupe";
        private const int CellsPerTick = 4;

        public override string Id => OptId;
        public override string Label => "Skip repeated haulable cell checks";
        public override string Description =>
            "Small storage areas no longer have the same cell re-checked for haulable items several times in a single tick.";
        public override string Category => "Map";
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "brage.paralleltick" };

        private static readonly MethodInfo RecalcAllInCellMethod = AccessTools.Method(typeof(ListerHaulables), nameof(ListerHaulables.RecalcAllInCell));
        private static readonly MethodInfo RecalcOnceMethod = AccessTools.Method(typeof(HaulablesCellDedupe), nameof(RecalcOnce));

        private static readonly AccessTools.FieldRef<ListerHaulables, Verse.Map> listerMap =
            AccessTools.FieldRefAccess<ListerHaulables, Verse.Map>("map");
        private static readonly AccessTools.FieldRef<ListerHaulables, HashSet<Thing>> listerHaulables =
            AccessTools.FieldRefAccess<ListerHaulables, HashSet<Thing>>("haulables");

        private static readonly IntVec3[] seen = new IntVec3[CellsPerTick];
        private static int seenCount;

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(ListerHaulables), "CellsCheckTick"))
                .WithPrefix(typeof(HaulablesCellDedupe), nameof(ResetSeen))
                .WithTranspiler(typeof(HaulablesCellDedupe), nameof(Transpiler));
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            RecalcAllInCellMethod,
            AccessTools.Method(typeof(ListerHaulables), "Check"),
            AccessTools.Method(typeof(ListerHaulables), "ShouldBeHaulable"),
            AccessTools.Method(typeof(StoreUtility), nameof(StoreUtility.IsInValidBestStorage)),
            AccessTools.Method(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStorageFor)),
            AccessTools.Method(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor)),
            AccessTools.Method(typeof(StoreUtility), "TryFindBestBetterStoreCellForWorker"),
            AccessTools.Method(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterNonSlotGroupStorageFor)),
            AccessTools.Method(typeof(StoreUtility), nameof(StoreUtility.IsGoodStoreCell)),
        };

        public override void ClearCaches() => seenCount = 0;

        public static void ResetSeen() => seenCount = 0;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.SwapCalls(instructions, OptId, RecalcAllInCellMethod, RecalcOnceMethod, 1);

        public static void RecalcOnce(ListerHaulables lister, IntVec3 c)
        {
            for (int i = 0; i < seenCount; i++)
            {
                if (seen[i] != c) continue;
                if (Verify.Enabled) VerifyRepeat(lister, c);
                return;
            }
            lister.RecalcAllInCell(c);
            if (seenCount < seen.Length) seen[seenCount++] = c;
        }

        /// <summary>Verify mode: run the skipped recalculation and check it left every haulable flag in the cell as it was.</summary>
        private static void VerifyRepeat(ListerHaulables lister, IntVec3 c)
        {
            List<Thing> things = c.GetThingList(listerMap(lister));
            HashSet<Thing> haulables = listerHaulables(lister);
            var before = new bool[things.Count];
            for (int i = 0; i < before.Length; i++) before[i] = haulables.Contains(things[i]);
            lister.RecalcAllInCell(c);
            bool same = things.Count == before.Length;
            for (int i = 0; same && i < before.Length; i++) same = haulables.Contains(things[i]) == before[i];
            Verify.Record(OptId, same);
        }
    }
}
