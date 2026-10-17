using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace ColonyScale.Optimizations.Map
{
    /// <summary>
    /// Once any gas has existed on a map (rot stink is common), GasGrid.Tick calls TryDissipateGases about area/64 times
    /// and TryDiffuseGases about area/32 times every tick, and on a gas-free cell both return almost immediately. The
    /// two calls are swapped for helpers that perform exactly vanilla's own early-return tests first and otherwise call
    /// the original method. The ProfilerBlocks, the cycle counters and the argument evaluation stay vanilla code.
    ///
    /// Exact: TryDissipateGases begins with "if (gasDensity[index] == 0) return;" (AnyGasAt), before any other read or
    /// write; the helper tests the same condition. TryDiffuseGases reads the tox, rot-stink and deadlife-dust bytes of
    /// the cell and returns when their sum is below 17, before cardinalDirections.Shuffle() (its only Rand use) and
    /// before any write; the helper computes the same sum from the same word with the same shifts (GasType values 8,
    /// 16, 24, checked when the patch is built). In both cases the skipped call would have returned without touching
    /// state or drawing Rand, so skipping it is identical. An out-of-range index throws the same IndexOutOfRange in
    /// either version. Verify mode runs the original anyway and checks that the density word and the order of
    /// cardinalDirections are unchanged. Main thread only (Map tick).
    /// </summary>
    public sealed class GasGridEarlyOut : Optimization
    {
        private const string OptId = "gasGridEarlyOut";

        public override string Id => OptId;
        public override string Label => "Skip gas updates on empty cells";
        public override string Description =>
            "Gas spreading and fading no longer does any work for map cells that contain no gas.";
        public override string Category => "Map";

        // Performance Plus's sparse gas replaces GasGrid.Tick with its own active-cell loop.
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "axeno7.performanceplus" };

        private static readonly MethodInfo DissipateMethod = AccessTools.Method(typeof(GasGrid), "TryDissipateGases");
        private static readonly MethodInfo DiffuseMethod = AccessTools.Method(typeof(GasGrid), "TryDiffuseGases");
        private static readonly MethodInfo DissipateHelper = AccessTools.Method(typeof(GasGridEarlyOut), nameof(Dissipate));
        private static readonly MethodInfo DiffuseHelper = AccessTools.Method(typeof(GasGridEarlyOut), nameof(Diffuse));

        private static readonly Action<GasGrid, int> originalDissipate =
            (Action<GasGrid, int>)Delegate.CreateDelegate(typeof(Action<GasGrid, int>), DissipateMethod);
        private static readonly Action<GasGrid, IntVec3> originalDiffuse =
            (Action<GasGrid, IntVec3>)Delegate.CreateDelegate(typeof(Action<GasGrid, IntVec3>), DiffuseMethod);

        private static readonly AccessTools.FieldRef<GasGrid, uint[]> gasDensity = AccessTools.FieldRefAccess<GasGrid, uint[]>("gasDensity");
        private static readonly AccessTools.FieldRef<GasGrid, Verse.Map> gridMap = AccessTools.FieldRefAccess<GasGrid, Verse.Map>("map");
        private static readonly AccessTools.FieldRef<GasGrid, List<IntVec3>> cardinalDirections =
            AccessTools.FieldRefAccess<GasGrid, List<IntVec3>>("cardinalDirections");

        public override IEnumerable<PatchSpec> GetPatches()
        {
            // The helpers hard-code the packed layout (DensityAt shifts by the GasType value).
            if (ShiftOf("ToxGas") != 8 || ShiftOf("RotStink") != 16 || ShiftOf("DeadlifeDust") != 24)
                throw new PatternNotFoundException(OptId, "GasType values changed");
            yield return PatchSpec.For(AccessTools.Method(typeof(GasGrid), nameof(GasGrid.Tick)))
                .WithTranspiler(typeof(GasGridEarlyOut), nameof(Transpiler));
        }

        private static int ShiftOf(string gasType) => Convert.ToInt32(Enum.Parse(typeof(GasType), gasType));

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            DissipateMethod,
            DiffuseMethod,
            AccessTools.Method(typeof(GasGrid), "AnyGasAt", new[] { typeof(int) }),
            AccessTools.Method(typeof(GasGrid), "DensityAt", new[] { typeof(int), typeof(GasType) }),
        };

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.SwapCalls(Transpilers.SwapCalls(instructions, OptId, DissipateMethod, DissipateHelper, 1),
                OptId, DiffuseMethod, DiffuseHelper, 1);

        public static void Dissipate(GasGrid grid, int index)
        {
            if (gasDensity(grid)[index] == 0)
            {
                if (Verify.Enabled) VerifyEarlyOut(grid, index, default, false);
                return;
            }
            originalDissipate(grid, index);
        }

        public static void Diffuse(GasGrid grid, IntVec3 cell)
        {
            int index = CellIndicesUtility.CellToIndex(cell, gridMap(grid).info.Size.x);
            uint word = gasDensity(grid)[index];
            if (((word >> 8) & 0xFF) + ((word >> 16) & 0xFF) + ((word >> 24) & 0xFF) < 17)
            {
                if (Verify.Enabled) VerifyEarlyOut(grid, index, cell, true);
                return;
            }
            originalDiffuse(grid, cell);
        }

        /// <summary>Verify mode: run the skipped call and check it changed neither the cell's density word nor the shuffle order.</summary>
        private static void VerifyEarlyOut(GasGrid grid, int index, IntVec3 cell, bool diffuse)
        {
            uint word = gasDensity(grid)[index];
            List<IntVec3> directions = cardinalDirections(grid);
            IntVec3[] order = directions.ToArray();
            if (diffuse) originalDiffuse(grid, cell);
            else originalDissipate(grid, index);
            bool same = gasDensity(grid)[index] == word && directions.Count == order.Length;
            for (int i = 0; same && i < order.Length; i++) same = directions[i] == order[i];
            Verify.Record(OptId, same);
        }
    }
}
