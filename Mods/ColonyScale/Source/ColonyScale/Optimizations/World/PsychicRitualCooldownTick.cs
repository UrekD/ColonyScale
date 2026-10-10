using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale.Optimizations.World
{
    /// <summary>
    /// With Anomaly active, GameComponent_PsychicRitualManager.GameComponentTick builds a new list of expired
    /// ritual cooldowns every tick (Where + ToList: an iterator and a list, almost always empty). This refills
    /// the component's own list instead.
    ///
    /// Exact: the same dictionary is enumerated in the same order with the same predicate, the whole list is
    /// built before any entry is removed, and removals and messages happen in the same order. The list field
    /// is private and only used inside this method, so reusing the instance does not change what anyone sees.
    /// A null dictionary or list (broken save) falls back to vanilla.
    /// </summary>
    public sealed class PsychicRitualCooldownTick : Optimization
    {
        public override string Id => "psychicRitualCooldownTick";
        public override string Label => "Allocation-free psychic ritual cooldowns";
        public override string Description =>
            "The psychic ritual cooldown check (Anomaly) no longer creates garbage every tick.";
        public override string Category => "World";

        private static readonly AccessTools.FieldRef<GameComponent_PsychicRitualManager, Dictionary<PsychicRitualDef, int>> ritualCooldowns =
            AccessTools.FieldRefAccess<GameComponent_PsychicRitualManager, Dictionary<PsychicRitualDef, int>>("ritualCooldowns");
        private static readonly AccessTools.FieldRef<GameComponent_PsychicRitualManager, List<KeyValuePair<PsychicRitualDef, int>>> availableRitualDefs =
            AccessTools.FieldRefAccess<GameComponent_PsychicRitualManager, List<KeyValuePair<PsychicRitualDef, int>>>("availableRitualDefs");

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(GameComponent_PsychicRitualManager), nameof(GameComponent_PsychicRitualManager.GameComponentTick)))
                .WithPrefix(typeof(PsychicRitualCooldownTick), nameof(Prefix), replacesOriginal: true, priority: Priority.Last);
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            AccessTools.Method(typeof(GameComponent_PsychicRitualManager), nameof(GameComponent_PsychicRitualManager.ExposeData)),
        };

        public static bool Prefix(GameComponent_PsychicRitualManager __instance)
        {
            if (!ModsConfig.AnomalyActive) return false;
            var cooldowns = ritualCooldowns(__instance);
            var available = availableRitualDefs(__instance);
            if (cooldowns == null || available == null) return true;

            available.Clear();
            foreach (KeyValuePair<PsychicRitualDef, int> cd in cooldowns)
            {
                if (cd.Value <= Find.TickManager.TicksGame) available.Add(cd);
            }
            foreach (KeyValuePair<PsychicRitualDef, int> availableRitualDef in available)
            {
                cooldowns.Remove(availableRitualDef.Key);
                if (availableRitualDef.Key.researchPrerequisite != null && availableRitualDef.Key.researchPrerequisite.IsFinished)
                {
                    Messages.Message("PsychicRitualCanBeCastAgain".Translate(availableRitualDef.Key.label), MessageTypeDefOf.PositiveEvent);
                }
            }
            return false;
        }
    }
}
