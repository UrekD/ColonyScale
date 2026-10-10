using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace ColonyScale.Optimizations.Map
{
    /// <summary>
    /// ConnectivitySource.UpdateIncrementally recomputes the 3x3 neighbourhood of every changed cell for the
    /// pathfinder. Before each neighbourhood it clears a static HashSet created with capacity 160000, and
    /// HashSet.Clear wipes the whole bucket array (~640 KB) every time, once per changed cell.
    ///
    /// Exact: the set is cleared right before each rect and only used for that rect's cells. CellRect.Cells
    /// yields each (x, 0, z) of the rect once, so Add always returns true and every cell is computed exactly
    /// as before. The set is private to this method, so nothing else observes it. Clear and Add are swapped
    /// for calls that do nothing and return true; the patched method then touches no shared static state,
    /// which matters because it runs inside PathFinderMapData.GatherData's Parallel.ForEach on worker threads.
    /// </summary>
    public sealed class ConnectivityIncremental : Optimization
    {
        public override string Id => "connectivityIncremental";
        public override string Label => "Faster pathfinding connectivity updates";
        public override string Description =>
            "When walls, doors or terrain change, the pathfinder updates its connectivity data without wiping a large lookup table " +
            "for every changed cell. Biggest effect while building, mining or fighting.";
        public override string Category => "Map";

        private static readonly MethodInfo SetClear = AccessTools.Method(typeof(HashSet<IntVec3>), nameof(HashSet<IntVec3>.Clear));
        private static readonly MethodInfo SetAdd = AccessTools.Method(typeof(HashSet<IntVec3>), nameof(HashSet<IntVec3>.Add));

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(ConnectivitySource), nameof(ConnectivitySource.UpdateIncrementally)))
                .WithTranspiler(typeof(ConnectivityIncremental), nameof(Transpiler));
        }

        // The argument rests on CellRect.Cells yielding distinct cells.
        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            AccessTools.EnumeratorMoveNext(AccessTools.PropertyGetter(typeof(CellRect), nameof(CellRect.Cells))),
        };

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var swapped = Transpilers.SwapCalls(instructions, "connectivityIncremental", SetClear,
                AccessTools.Method(typeof(ConnectivityIncremental), nameof(SkipClear)), 1);
            return Transpilers.SwapCalls(swapped, "connectivityIncremental", SetAdd,
                AccessTools.Method(typeof(ConnectivityIncremental), nameof(AlwaysAdded)), 1);
        }

        public static void SkipClear(HashSet<IntVec3> set) { }

        public static bool AlwaysAdded(HashSet<IntVec3> set, IntVec3 cell) => true;
    }
}
