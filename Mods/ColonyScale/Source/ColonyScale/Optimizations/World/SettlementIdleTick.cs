using System;
using System.Collections.Generic;
using System.Reflection;
using ColonyScale.Optimizations.Pawns;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ColonyScale.Optimizations.World
{
    /// <summary>
    /// Every world object ticks every tick. For a faction settlement (hundreds on a large world) WorldObject.DoTick
    /// runs the comp loop, the update-interval test, then the contents walk: GetChildHolders (which looks for the
    /// settlement's map) and ThingOwner.DoTick on the trader stock. On almost every tick none of that does anything.
    /// For exact type Settlement on such a tick only the tickDelta increment is kept.
    ///
    /// Exact: Settlement and MapParent do not override Tick, UpdateRateTicks or UpdateRateTickOffset (checked when
    /// patching), so Tick is WorldObject's loop of CompTick calls; we require every comp to be non-null and its runtime
    /// type to inherit WorldObjectComp's empty CompTick (no class below WorldObjectComp declares a CompTick). The
    /// answer is cached per comps list and rebuilt when its List._version changes.
    /// The interval test is vanilla's expression with tickDelta + 1 (the value it has after the increment) and the
    /// same getters, read before Tick instead of after it, which changes nothing when Tick does nothing; on a tick
    /// where it is true vanilla runs. Settlement is a holder but not IThingHolderTickable, so the walk always runs:
    /// its holder list is the settlement (MapParent.GetDirectlyHeldThings is null), the map if HasMap (we require
    /// !HasMap) and the trader tracker if any, whose container is the private stock; ThingOwner.DoTick on a null,
    /// dontTickContents or empty stock does nothing. The skipped profiler scopes, the lazy fill of the private
    /// cached* fields and the tmpHolders list (used nowhere else) are not observable; nothing skipped draws Rand.
    /// Verify mode runs vanilla and checks it left tickDelta at the predicted value (no interval tick).
    ///
    /// Threading: world objects tick on the main thread (WorldObjectsHolder.WorldObjectsHolderTick).
    /// </summary>
    public sealed class SettlementIdleTick : Optimization
    {
        private const string OptId = "settlementIdleTick";

        public override string Id => OptId;
        public override string Label => "Skip idle settlement ticks";
        public override string Description =>
            "Faction settlements on the world map skip their per-tick bookkeeping on ticks where they have nothing to do. " +
            "Grows with the number of settlements on the world.";
        public override string Category => "World";

        // Free Performance's settlementtick prefixes the same method; Performance Plus's world dispatch replaces the
        // world object tick loop and skips the same settlement ticks itself.
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "brage.paralleltick", "axeno7.performanceplus" };

        private static readonly AccessTools.FieldRef<WorldObject, int> tickDeltaRef = AccessTools.FieldRefAccess<WorldObject, int>("tickDelta");
        private static readonly AccessTools.FieldRef<Settlement_TraderTracker, ThingOwner<Thing>> stockRef =
            AccessTools.FieldRefAccess<Settlement_TraderTracker, ThingOwner<Thing>>("stock");
        private static readonly MethodInfo UpdateRateTicksGetter = AccessTools.PropertyGetter(typeof(WorldObject), "UpdateRateTicks");
        private static readonly MethodInfo UpdateRateTickOffsetGetter = AccessTools.PropertyGetter(typeof(WorldObject), "UpdateRateTickOffset");
        private static readonly Func<WorldObject, int> updateRateTicks = AccessTools.MethodDelegate<Func<WorldObject, int>>(UpdateRateTicksGetter);
        private static readonly Func<WorldObject, int> updateRateTickOffset = AccessTools.MethodDelegate<Func<WorldObject, int>>(UpdateRateTickOffsetGetter);

        public override IEnumerable<PatchSpec> GetPatches()
        {
            foreach (string name in new[] { "Tick", UpdateRateTicksGetter.Name, UpdateRateTickOffsetGetter.Name })
                if (OverrideScan.Overrides(typeof(Settlement), typeof(WorldObject), name))
                    throw new InvalidOperationException("Settlement overrides WorldObject." + name);
            if (typeof(IThingHolderTickable).IsAssignableFrom(typeof(Settlement)))
                throw new InvalidOperationException("Settlement is IThingHolderTickable");

            yield return PatchSpec.For(AccessTools.Method(typeof(WorldObject), nameof(WorldObject.DoTick)))
                .WithPrefix(typeof(SettlementIdleTick), nameof(Prefix), replacesOriginal: true, priority: Priority.Last)
                .WithPostfix(typeof(SettlementIdleTick), nameof(Postfix), priority: Priority.First);
            foreach (var p in CacheResets.Patches(typeof(SettlementIdleTick), nameof(ResetCaches))) yield return p;
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            AccessTools.Method(typeof(WorldObject), "Tick"),
            AccessTools.Method(typeof(WorldObjectComp), nameof(WorldObjectComp.CompTick)),
            UpdateRateTicksGetter,
            UpdateRateTickOffsetGetter,
            AccessTools.Method(typeof(GenTicks), nameof(GenTicks.IsTickInterval), new[] { typeof(int), typeof(int) }),
            AccessTools.Method(typeof(Settlement), nameof(Settlement.GetChildHolders)),
            AccessTools.Method(typeof(MapParent), nameof(MapParent.GetChildHolders)),
            AccessTools.Method(typeof(MapParent), nameof(MapParent.GetDirectlyHeldThings)),
            AccessTools.PropertyGetter(typeof(MapParent), nameof(MapParent.HasMap)),
            AccessTools.PropertyGetter(typeof(MapParent), nameof(MapParent.Map)),
            AccessTools.Method(typeof(Settlement_TraderTracker), nameof(Settlement_TraderTracker.GetDirectlyHeldThings)),
            AccessTools.Method(typeof(ThingOwner), nameof(ThingOwner.DoTick)),
        };

        public override void ClearCaches() => ResetCaches();

        public static void ResetCaches()
        {
            compsIdle.Clear();
            typeTicks.Clear();
        }

        private sealed class CompsEntry
        {
            public int version;
            public bool idle;
        }

        // Keyed by the settlement's comps list (reference equality); dropped with the game.
        private static readonly Dictionary<List<WorldObjectComp>, CompsEntry> compsIdle = new Dictionary<List<WorldObjectComp>, CompsEntry>();
        private static readonly Dictionary<Type, bool> typeTicks = new Dictionary<Type, bool>();

        public static bool Prefix(WorldObject __instance, out int __state)
        {
            __state = -1;
            if (__instance.GetType() != typeof(Settlement) || !Idle((Settlement)__instance)) return true;
            ref int tickDelta = ref tickDeltaRef(__instance);
            if (Verify.Enabled)
            {
                __state = tickDelta + 1;
                return true;
            }
            tickDelta++;
            return false;
        }

        public static void Postfix(WorldObject __instance, int __state)
        {
            if (__state >= 0) Verify.Record(OptId, tickDeltaRef(__instance) == __state);
        }

        private static bool Idle(Settlement settlement)
        {
            int rate = updateRateTicks(settlement);
            if (tickDeltaRef(settlement) + 1 > rate || GenTicks.IsTickInterval(updateRateTickOffset(settlement), rate)) return false;
            Settlement_TraderTracker trader = settlement.trader;
            if (trader != null)
            {
                ThingOwner<Thing> stock = stockRef(trader);
                if (stock != null && !stock.dontTickContents && stock.Count > 0) return false;
            }
            return CompsIdle(settlement.AllComps) && !settlement.HasMap;
        }

        private static bool CompsIdle(List<WorldObjectComp> comps)
        {
            int version = ListVersion<WorldObjectComp>.Of(comps);
            if (compsIdle.TryGetValue(comps, out CompsEntry entry) && entry.version == version) return entry.idle;
            bool idle = true;
            for (int i = 0; idle && i < comps.Count; i++)
                idle = comps[i] != null && !TypeTicks(comps[i].GetType());
            compsIdle[comps] = new CompsEntry { version = version, idle = idle };
            return idle;
        }

        private static bool TypeTicks(Type type)
        {
            if (!typeTicks.TryGetValue(type, out bool ticks))
                typeTicks[type] = ticks = OverrideScan.Overrides(type, typeof(WorldObjectComp), nameof(WorldObjectComp.CompTick));
            return ticks;
        }
    }
}
