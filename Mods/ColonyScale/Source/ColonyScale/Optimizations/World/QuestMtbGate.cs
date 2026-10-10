using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ColonyScale.Optimizations.World
{
    /// <summary>
    /// QuestPart_MTB.QuestPartTick evaluates MTBDays every tick but only rolls when TicksGame % 10 == 0.
    /// For the betrayal quest part (the only vanilla subclass) MTBDays checks the quest pawns and walks every
    /// player-faction pawn on the map, so this skips the body on the other 9 of 10 ticks.
    ///
    /// Exact: on skipped ticks vanilla's condition is false whatever MTBDays returns, so nothing completes
    /// and no Rand is drawn. QuestPart_BetrayMTB.MTBDays and everything it calls (CanBetray's pawn state
    /// getters, Suspended -> WorldPawns.GetSituation, IsBurning, MapHeld, PawnsInFaction -> AllPawns, which
    /// gathers unspawned pawns through GetChildHolders, SimpleCurve.Evaluate) draws no Rand and only writes
    /// scratch lists that are cleared and refilled on every use. Subclasses whose MTBDays is not vanilla's
    /// (e.g. from other mods, including ones whose lookup is ambiguous) always run the original body.
    /// </summary>
    public sealed class QuestMtbGate : Optimization
    {
        public override string Id => "questMtbGate";
        public override string Label => "Cheaper quest timers";
        public override string Description =>
            "Random-chance quest timers (such as betrayals) skip their colony scan on ticks where they cannot fire.";
        public override string Category => "World";

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(QuestPart_MTB), nameof(QuestPart_MTB.QuestPartTick)))
                .WithPrefix(typeof(QuestMtbGate), nameof(Prefix), replacesOriginal: true, priority: Priority.Last);
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            AccessTools.PropertyGetter(typeof(QuestPart_BetrayMTB), "MTBDays"),
            AccessTools.Method(typeof(QuestPart_BetrayMTB), "CanBetray"),
            AccessTools.Method(typeof(MapPawns), nameof(MapPawns.PawnsInFaction)),
            AccessTools.PropertyGetter(typeof(MapPawns), nameof(MapPawns.AllPawns)),
            AccessTools.PropertyGetter(typeof(MapPawns), nameof(MapPawns.AllPawnsUnspawned)),
            AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Suspended)),
            AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Suspended)),
            AccessTools.Method(typeof(WorldPawns), nameof(WorldPawns.GetSituation)),
            AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.IsColonist)),
            AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.IsFreeColonist)),
            AccessTools.Method(typeof(FireUtility), nameof(FireUtility.IsBurning), new[] { typeof(Thing) }),
            AccessTools.Method(typeof(SimpleCurve), nameof(SimpleCurve.Evaluate)),
        };

        public override void ClearCaches() => vanillaMtb.Clear();

        /// <summary>Per runtime type: whether its MTBDays is QuestPart_BetrayMTB's. Quest ticks run on the main thread.</summary>
        private static readonly Dictionary<Type, bool> vanillaMtb = new Dictionary<Type, bool>();

        public static bool Prefix(QuestPart_MTB __instance)
        {
            if (Find.TickManager.TicksGame % 10 == 0) return true;
            Type type = __instance.GetType();
            if (!vanillaMtb.TryGetValue(type, out bool gated))
            {
                try
                {
                    var getter = type.GetMethod("get_MTBDays", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    gated = getter?.DeclaringType == typeof(QuestPart_BetrayMTB);
                }
                catch (AmbiguousMatchException) // a mod subclass hides MTBDays: not vanilla's
                {
                    gated = false;
                }
                vanillaMtb[type] = gated;
            }
            return !gated;
        }
    }
}
