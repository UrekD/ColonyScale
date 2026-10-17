using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Unity.Collections;
using Verse;

namespace ColonyScale.Optimizations.AI
{
    /// <summary>
    /// Every pooled path job output (PathFinder.PathJobOutput, one per path request in flight, kept for reuse) allocates
    /// its result list with a capacity of the map's cell count: 62,500 cells × 12 bytes = 750 KB per output on a 250×250
    /// map, for paths that are usually a few dozen cells long. This starts the list at <see cref="InitialCapacity"/>
    /// cells; it grows like any NativeList when a longer path is written.
    /// Inspired by Performance Plus by axen-o7 (Steam Workshop 3821145631, "path output memory reduction").
    ///
    /// Exact: the capacity is not observable. PathFinderJob writes the path only through NativeList.Add, which grows the
    /// buffer (doubling, contents copied) when needed; in 1.7's NativeList every copy of the struct shares one header
    /// pointer, so growth inside the job's copy (job.path = output.path) is seen through output.path, and readers
    /// (EmitPath → PawnPath.Initialize, the Length checks) only use Length and the indexer. Nothing in the game reads
    /// Capacity or calls AddNoResize on it. The list's elements and length are the same as before, so paths, costs and
    /// Rand are unchanged. Allocation happens where vanilla already allocates (pool misses) and in Add on the job
    /// thread, which uses the same thread-safe Persistent allocator. Outputs created before this was turned on keep their
    /// big buffers until the map is gone.
    /// </summary>
    public sealed class PathOutputMemory : Optimization
    {
        private const string OptId = "pathOutputMemory";

        /// <summary>Cells reserved up front; longer paths grow the list (128 → 256 → ...).</summary>
        public const int InitialCapacity = 128;

        public override string Id => OptId;
        public override string Label => "Smaller path result buffers";
        public override string Description =>
            "Path finding no longer reserves a result buffer the size of the whole map for every path request in flight; " +
            "the buffer starts small and grows for long paths. Saves memory (about 750 KB per pooled buffer on a large map), not tick time.";
        public override string Category => "AI";

        // Performance Plus transpiles the same allocation (and expects to find it).
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "axeno7.performanceplus" };

        private static readonly Type OutputType = AccessTools.Inner(typeof(PathFinder), "PathJobOutput");
        private static readonly ConstructorInfo ListCtor =
            AccessTools.Constructor(typeof(NativeList<IntVec3>), new[] { typeof(int), typeof(AllocatorManager.AllocatorHandle) });

        public override IEnumerable<PatchSpec> GetPatches()
        {
            if (OutputType == null) throw new MissingMemberException("PathFinder", "PathJobOutput");
            yield return PatchSpec.For(AccessTools.Method(OutputType, "Initialize", new[] { typeof(int) }))
                .WithTranspiler(typeof(PathOutputMemory), nameof(Transpiler));
        }

        // PathFinderJob writes the list (Clear in Execute, Add per node in FinalizePath); the path readers use Length and the indexer only.
        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            AccessTools.Method(typeof(PathFinderJob), "Execute"),
            AccessTools.Method(typeof(PathFinderJob), "FinalizePath"),
            AccessTools.Method(typeof(PathFinder), "EmitPath"),
            AccessTools.Method(typeof(Verse.AI.PawnPath), "Initialize"),
            // The argument also rests on UnityStubs' NativeList: one shared header per list, Add grows, Length and the
            // indexer read through the header. A stubs update that changes these turns this off.
            ListCtor,
            AccessTools.Method(typeof(NativeList<IntVec3>), nameof(NativeList<IntVec3>.Add)),
            AccessTools.Method(typeof(NativeList<IntVec3>), nameof(NativeList<IntVec3>.Clear)),
            AccessTools.PropertyGetter(typeof(NativeList<IntVec3>), nameof(NativeList<IntVec3>.Length)),
            AccessTools.Method(typeof(NativeList<IntVec3>), "get_Item"),
            AccessTools.Method(typeof(NativeList<IntVec3>), "EnsureCapacityForLength"),
        };

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            int swapped = 0;
            foreach (var ins in list)
            {
                if (ins.opcode == OpCodes.Newobj && Equals(ins.operand, ListCtor))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = AccessTools.Method(typeof(PathOutputMemory), nameof(NewPathList));
                    swapped++;
                }
            }
            if (swapped != 1) throw new PatternNotFoundException(OptId, $"expected 1 NativeList<IntVec3> allocation, found {swapped}");
            return list;
        }

        /// <summary>Same stack shape as the constructor call it replaces: (capacity, allocator) → list.</summary>
        public static NativeList<IntVec3> NewPathList(int cells, AllocatorManager.AllocatorHandle allocator) =>
            new NativeList<IntVec3>(Math.Min(cells, InitialCapacity), allocator);
    }
}
