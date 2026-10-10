using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace ColonyScale.Optimizations.Map
{
    /// <summary>
    /// When a thing despawns it is also removed from its tick bucket and from the dynamic draw list. Both use
    /// List&lt;Thing&gt;.Remove: a front-to-back scan with a Thing.Equals call per element. Newly spawned things
    /// (projectiles, dropped items) sit at the end of those lists.
    ///
    /// Exact: every Add and Remove on these lists is swapped for <see cref="TrackedLists"/>, which counts how many
    /// elements of each Thing.Equals class each list holds. A removal searches from both ends by reference only when
    /// that count is 1 (then the element found is the first and only match); otherwise it runs the exact front-to-back
    /// scan (see <see cref="FastList"/>). Duplicates are reachable in vanilla: TickList.Tick appends registrations
    /// before processing deregistrations, so a thing despawned and respawned before its next tick (SkipUtility.SkipTo)
    /// sits in its bucket twice and vanilla removes the earlier copy; the count is then 2 and the exact scan does the
    /// same. The tick buckets are private to TickList and also mutated by Reset and RemoveWhere, which drop the counts;
    /// drawThings is only mutated by Register/DeRegisterDrawable. Assumes no mod mutates these lists by reflection.
    /// </summary>
    public sealed class ThingListRemovals : Optimization
    {
        public override string Id => "thingListRemovals";
        public override string Label => "Faster tick and draw list removal";
        public override string Description =>
            "Despawning a thing (projectiles, picked-up items, eaten meals) no longer scans the whole tick and draw lists.";
        public override string Category => "Map";

        private static readonly MethodInfo ListAdd = AccessTools.Method(typeof(List<Thing>), nameof(List<Thing>.Add));
        private static readonly MethodInfo ListRemove = AccessTools.Method(typeof(List<Thing>), nameof(List<Thing>.Remove));
        private static readonly MethodInfo TrackedAdd = AccessTools.Method(typeof(ThingListRemovals), nameof(Add));
        private static readonly MethodInfo TrackedRemove = AccessTools.Method(typeof(ThingListRemovals), nameof(Remove));

        private static readonly AccessTools.FieldRef<TickList, List<List<Thing>>> thingLists =
            AccessTools.FieldRefAccess<TickList, List<List<Thing>>>("thingLists");

        private static readonly TrackedLists tracked = new TrackedLists();

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(TickList), nameof(TickList.Tick)))
                .WithTranspiler(typeof(ThingListRemovals), nameof(TickListTranspiler));
            yield return PatchSpec.For(AccessTools.Method(typeof(TickList), nameof(TickList.Reset)))
                .WithPrefix(typeof(ThingListRemovals), nameof(ForgetBuckets));
            yield return PatchSpec.For(AccessTools.Method(typeof(TickList), nameof(TickList.RemoveWhere)))
                .WithPrefix(typeof(ThingListRemovals), nameof(ForgetBuckets));
            yield return PatchSpec.For(AccessTools.Method(typeof(DynamicDrawManager), nameof(DynamicDrawManager.RegisterDrawable)))
                .WithTranspiler(typeof(ThingListRemovals), nameof(RegisterDrawableTranspiler));
            yield return PatchSpec.For(AccessTools.Method(typeof(DynamicDrawManager), nameof(DynamicDrawManager.DeRegisterDrawable)))
                .WithTranspiler(typeof(ThingListRemovals), nameof(DeRegisterDrawableTranspiler));
            foreach (var p in CacheResets.Patches(typeof(ThingListRemovals), nameof(ResetCaches))) yield return p;
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            AccessTools.Method(typeof(Thing), nameof(Thing.Equals), new[] { typeof(Thing) }),
        };

        // The counts hash things by def.category, which a def hot reload can change.
        public override void ClearCaches() => ResetCaches();

        public static void ResetCaches() => tracked.Clear();

        public static void Add(List<Thing> list, Thing t) => tracked.Add(list, t);

        public static bool Remove(List<Thing> list, Thing t) => tracked.Remove(list, t);

        public static void ForgetBuckets(TickList __instance)
        {
            var buckets = thingLists(__instance);
            for (int i = 0; i < buckets.Count; i++) tracked.Forget(buckets[i]);
        }

        public static IEnumerable<CodeInstruction> TickListTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.SwapCalls(Transpilers.SwapCalls(instructions, "thingListRemovals", ListAdd, TrackedAdd, 1),
                "thingListRemovals", ListRemove, TrackedRemove, 1);

        public static IEnumerable<CodeInstruction> RegisterDrawableTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.SwapCalls(instructions, "thingListRemovals", ListAdd, TrackedAdd, 1);

        public static IEnumerable<CodeInstruction> DeRegisterDrawableTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.SwapCalls(instructions, "thingListRemovals", ListRemove, TrackedRemove, 1);
    }
}
