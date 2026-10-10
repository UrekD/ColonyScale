using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale.Optimizations.Pawns
{
    /// <summary>
    /// QuestUtility.GetExtraFactionsFromQuestParts backs IsQuestLodger (called twice per check), extra home/host/mini
    /// faction lookups and ~110 other sites, including ThoughtUtility.CanGetThought every mood interval. It scans
    /// QuestManager's cached extra-faction parts, which keeps the parts of every quest ever added, finished ones too.
    /// This scans an order-preserving copy without the parts that can never match.
    ///
    /// Exact: a part is dropped only if its quest has ended and its root is not Hospitality_Refugee. For such a part
    /// vanilla's test (State == Ongoing || quest == forQuest || (root == Hospitality_Refugee &amp;&amp; !pawn.IsColonist))
    /// is false: Quest.State is never Ongoing once `ended` is set, forQuest is either null or (checked per call) a
    /// quest that is not droppable, and the root test fails, so pawn.IsColonist, AlreadyAdded and Contains are never
    /// evaluated for it. What vanilla does read for it (extraFaction?.faction, quest, quest.State) has no side effects.
    /// Kept parts run vanilla's full test, in vanilla's order, so the output list (order and duplicate filtering) is
    /// identical. Droppability is stable: `ended` is private and only ever set true (Quest.End; ExposeData on load),
    /// root is only assigned at generation and load, and part.quest only changes in Quest.AddPart/RemovePart, which
    /// invalidate the copy. The copy is also rebuilt when the source list object or its List._version changes (every
    /// add/remove/clear, game load, new game) and after Quest.End (only to drop more; a stale copy is a superset).
    /// </summary>
    public sealed class QuestLodgerScan : Optimization
    {
        private const string OptId = "questLodgerScan";

        public override string Id => OptId;
        public override string Label => "Faster quest lodger checks";
        public override string Description =>
            "Checks for whether a pawn is a quest lodger, guest or helper skip quest parts of quests that have already ended. " +
            "Grows with the number of quests completed over a long game.";
        public override string Category => "Pawns";

        private static AccessTools.FieldRef<List<QuestPart_ExtraFaction>, int> listVersion;
        private static AccessTools.FieldRef<Quest, bool> endedRef;

        public override IEnumerable<PatchSpec> GetPatches()
        {
            listVersion = AccessTools.FieldRefAccess<List<QuestPart_ExtraFaction>, int>("_version");
            endedRef = AccessTools.FieldRefAccess<Quest, bool>("ended");

            yield return PatchSpec.For(AccessTools.Method(typeof(QuestUtility), nameof(QuestUtility.GetExtraFactionsFromQuestParts)))
                .WithPrefix(typeof(QuestLodgerScan), nameof(Prefix), replacesOriginal: true, priority: Priority.Last)
                .WithPostfix(typeof(QuestLodgerScan), nameof(Postfix), priority: Priority.First);
            yield return PatchSpec.For(AccessTools.Method(typeof(Quest), nameof(Quest.End)))
                .WithPostfix(typeof(QuestLodgerScan), nameof(Invalidate));
            yield return PatchSpec.For(AccessTools.Method(typeof(Quest), nameof(Quest.AddPart), new[] { typeof(QuestPart) }))
                .WithPostfix(typeof(QuestLodgerScan), nameof(Invalidate));
            yield return PatchSpec.For(AccessTools.Method(typeof(Quest), nameof(Quest.RemovePart)))
                .WithPostfix(typeof(QuestLodgerScan), nameof(Invalidate));
        }

        // The "ended => not Ongoing" argument and the claim that State has no side effects.
        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            AccessTools.PropertyGetter(typeof(Quest), nameof(Quest.State)),
        };

        public override void ClearCaches()
        {
            source = null;
            filtered.Clear();
            epoch++;
        }

        private static List<QuestPart_ExtraFaction> source;
        private static int sourceVersion;
        private static int builtEpoch = -1;
        private static int epoch;
        private static readonly List<QuestPart_ExtraFaction> filtered = new List<QuestPart_ExtraFaction>();
        private static bool busy;

        public static void Invalidate() => epoch++;

        public static bool Prefix(Pawn pawn, List<ExtraFaction> outExtraFactions, Quest forQuest, out List<ExtraFaction> __state)
        {
            __state = null;
            // Reentrant or off-main-thread calls, and calls for a quest whose parts we dropped, run vanilla.
            if (busy || !UnityData.IsInMainThread || (forQuest != null && Droppable(forQuest))) return true;
            busy = true;
            try
            {
                if (Verify.Enabled)
                {
                    var fast = new List<ExtraFaction>();
                    Collect(pawn, Filtered(Find.QuestManager.ExtraFactionQuestParts), fast, forQuest);
                    __state = fast;
                    return true;
                }
                outExtraFactions.Clear();
                Collect(pawn, Filtered(Find.QuestManager.ExtraFactionQuestParts), outExtraFactions, forQuest);
                return false;
            }
            finally
            {
                busy = false;
            }
        }

        public static void Postfix(List<ExtraFaction> outExtraFactions, List<ExtraFaction> __state)
        {
            if (__state == null) return;
            bool same = __state.Count == outExtraFactions.Count;
            for (int i = 0; same && i < __state.Count; i++) same = ReferenceEquals(__state[i], outExtraFactions[i]);
            Verify.Record(OptId, same);
        }

        private static bool Droppable(Quest quest) => endedRef(quest) && quest.root != QuestScriptDefOf.Hospitality_Refugee;

        private static List<QuestPart_ExtraFaction> Filtered(List<QuestPart_ExtraFaction> src)
        {
            if (!ReferenceEquals(src, source) || listVersion(src) != sourceVersion || builtEpoch != epoch)
            {
                filtered.Clear();
                for (int i = 0; i < src.Count; i++)
                {
                    var part = src[i];
                    // Null parts and parts without a quest are kept so vanilla's NullReferenceException is reproduced.
                    if (part == null || part.quest == null || !Droppable(part.quest)) filtered.Add(part);
                }
                source = src;
                sourceVersion = listVersion(src);
                builtEpoch = epoch;
            }
            return filtered;
        }

        /// <summary>Vanilla's loop body over the given parts.</summary>
        private static void Collect(Pawn pawn, List<QuestPart_ExtraFaction> parts, List<ExtraFaction> outExtraFactions, Quest forQuest)
        {
            for (int i = 0; i < parts.Count; i++)
            {
                QuestPart_ExtraFaction part = parts[i];
                if (part.extraFaction?.faction != null)
                {
                    Quest quest = part.quest;
                    if ((quest.State == QuestState.Ongoing || quest == forQuest || (quest.root == QuestScriptDefOf.Hospitality_Refugee && !pawn.IsColonist))
                        && !AlreadyAdded(outExtraFactions, part.extraFaction) && part.affectedPawns.Contains(pawn))
                    {
                        outExtraFactions.Add(part.extraFaction);
                    }
                }
            }
        }

        private static bool AlreadyAdded(List<ExtraFaction> outExtraFactions, ExtraFaction extraFaction)
        {
            for (int j = 0; j < outExtraFactions.Count; j++)
            {
                if (outExtraFactions[j].faction == extraFaction.faction && outExtraFactions[j].factionType == extraFaction.factionType)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
