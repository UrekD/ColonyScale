using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale.Optimizations.Pawns
{
    /// <summary>
    /// With Anomaly active, BloodRainUtility.BloodRainTick runs every tick for every spawned pawn and asks the pawn's map
    /// for an active blood rain through GameConditionManager.GetActiveCondition&lt;GameCondition_BloodRain&gt;(), a generic
    /// type-test scan of the map's conditions and then of the world's. This prefix answers the common "no blood rain on
    /// this map or the world" case with a direct scan of the same two lists and returns; otherwise the original runs.
    ///
    /// Exact: the prefix evaluates, in vanilla's order, ModsConfig.AnomalyActive and pawn.MapHeld (both pure), then
    /// performs the same "is GameCondition_BloodRain" test over the same lists and the same Parent chain that
    /// GetActiveCondition walks. It only skips when vanilla's ExposedToBloodRain would return false at that lookup
    /// (or earlier, at MapHeld == null), in which case BloodRainTick does nothing. The skipped "SpawnedParentOrMe is
    /// PawnFlyer" test sits between those two checks and can only turn the answer into false as well; it is a pure
    /// read (the pawn itself when spawned, which Pawn.Tick requires, else the same holder walk MapHeld just did).
    /// When it does not skip, the original runs from the start, so the only extra work is a repeated pure
    /// MapHeld/Parent read. A direct scan of the (0-3 entry) condition lists is cheaper than any version-keyed cache.
    /// Runs on the main thread only (Pawn.Tick).
    /// </summary>
    public sealed class BloodRainTick : Optimization
    {
        public override string Id => "bloodRain";
        public override string Label => "Cheaper blood rain check";
        public override string Description =>
            "Pawns check for blood rain with a direct look at the map's and world's active conditions. Anomaly only.";
        public override string Category => "Pawns";
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "brage.paralleltick" };

        private static readonly AccessTools.FieldRef<GameConditionManager, List<GameCondition>> activeConditions =
            AccessTools.FieldRefAccess<GameConditionManager, List<GameCondition>>("activeConditions");

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(BloodRainUtility), nameof(BloodRainUtility.BloodRainTick)))
                .WithPrefix(typeof(BloodRainTick), nameof(Prefix), replacesOriginal: true, priority: Priority.Last);
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            AccessTools.Method(typeof(BloodRainUtility), nameof(BloodRainUtility.ExposedToBloodRain)),
            AccessTools.Method(typeof(GameConditionManager), nameof(GameConditionManager.GetActiveCondition), new System.Type[0])
                .MakeGenericMethod(typeof(GameCondition_BloodRain)),
            AccessTools.PropertyGetter(typeof(GameConditionManager), nameof(GameConditionManager.Parent)),
            AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.MapHeld)),
            AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.SpawnedParentOrMe)),
        };

        /// <summary>False (skip the original) when vanilla's BloodRainTick provably does nothing.</summary>
        public static bool Prefix(Pawn pawn)
        {
            if (!ModsConfig.AnomalyActive) return false;
            Verse.Map map = pawn.MapHeld;
            if (map == null) return false;
            GameConditionManager manager = map.gameConditionManager;
            if (manager == null) return true; // let vanilla fail the same way
            bool any = AnyBloodRain(manager);
            if (Verify.Enabled)
            {
                Verify.Record("bloodRain", any == (manager.GetActiveCondition<GameCondition_BloodRain>() != null));
                return true;
            }
            return any;
        }

        /// <summary>GetActiveCondition&lt;GameCondition_BloodRain&gt;() != null, without the generic call.</summary>
        private static bool AnyBloodRain(GameConditionManager manager)
        {
            for (GameConditionManager m = manager; m != null; m = m.Parent)
            {
                List<GameCondition> list = activeConditions(m);
                for (int i = 0; i < list.Count; i++)
                    if (list[i] is GameCondition_BloodRain) return true;
            }
            return false;
        }
    }
}
