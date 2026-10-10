using System;
using System.Collections.Generic;
using System.Reflection;
using ColonyScale.Optimizations.Pawns;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale.Optimizations.World
{
    /// <summary>
    /// Every world pawn asks Pawn.Suspended every tick, which goes through WorldPawns.GetSituation to
    /// QuestManager.IsReservedByAnyQuest (Quest.QuestReserves on every active quest, a virtual call on every part)
    /// and QuestUtility.IsBorrowedByAnyFaction (a type test on every part of every ongoing quest). Most parts answer
    /// neither question. Per quest we keep the indices of the parts that can, rebuilt when the parts list object or
    /// its List._version changes (every add, remove, insert, indexer set, clear; game load replaces the list).
    ///
    /// Exact, QuestReserves(Pawn): vanilla tests Historical, then calls QuestPartReserves(p) on parts[0..count) with
    /// count read once. A part whose runtime type has no QuestPartReserves below QuestPart runs the base method,
    /// which returns false and does nothing, so only the other parts are called: in the same order, read live from
    /// the list, null parts included (vanilla's NullReferenceException at the same point). If a call changes the
    /// list, the remaining indices are finished with vanilla's loop over the original count.
    /// IsBorrowedByAnyFaction: same loop over active quests and their State, but only parts that are
    /// QuestPart_LendColonistsToFaction are visited, in order; vanilla's `is` test is false for every other part
    /// (nulls included), and LentColonistsListForReading/Contains do not change anything.
    /// Verify mode computes the filtered answer and compares it with vanilla's.
    ///
    /// Threading: off-main-thread calls run vanilla, so the cache is only touched on the main thread. Rebuilds
    /// allocate new index arrays, so a reentrant call never changes an array a caller is iterating.
    /// </summary>
    public sealed class QuestReserveFilter : Optimization
    {
        private const string OptId = "questReserveFilter";

        public override string Id => OptId;
        public override string Label => "Faster quest reservation checks";
        public override string Description =>
            "Checks whether a world pawn is reserved or borrowed by a quest only look at the quest parts that can reserve or lend " +
            "pawns. Helps late games with many world pawns and active quests.";
        public override string Category => "World";

        // Free Performance's questreserve prefixes the same methods.
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "brage.paralleltick" };

        private static readonly AccessTools.FieldRef<Quest, List<QuestPart>> partsRef = AccessTools.FieldRefAccess<Quest, List<QuestPart>>("parts");

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(Quest), nameof(Quest.QuestReserves), new[] { typeof(Pawn) }))
                .WithPrefix(typeof(QuestReserveFilter), nameof(QuestReservesPrefix), replacesOriginal: true, priority: Priority.Last)
                .WithPostfix(typeof(QuestReserveFilter), nameof(VerifyPostfix), priority: Priority.First);
            yield return PatchSpec.For(AccessTools.Method(typeof(QuestUtility), nameof(QuestUtility.IsBorrowedByAnyFaction)))
                .WithPrefix(typeof(QuestReserveFilter), nameof(IsBorrowedPrefix), replacesOriginal: true, priority: Priority.Last)
                .WithPostfix(typeof(QuestReserveFilter), nameof(VerifyPostfix), priority: Priority.First);
            foreach (var p in CacheResets.Patches(typeof(QuestReserveFilter), nameof(ResetCaches))) yield return p;
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            AccessTools.Method(typeof(QuestPart), nameof(QuestPart.QuestPartReserves), new[] { typeof(Pawn) }),
            AccessTools.PropertyGetter(typeof(Quest), nameof(Quest.Historical)),
            AccessTools.PropertyGetter(typeof(Quest), nameof(Quest.State)),
            AccessTools.PropertyGetter(typeof(Quest), nameof(Quest.PartsListForReading)),
            AccessTools.PropertyGetter(typeof(QuestPart_LendColonistsToFaction), nameof(QuestPart_LendColonistsToFaction.LentColonistsListForReading)),
        };

        public override void ClearCaches() => ResetCaches();

        public static void ResetCaches()
        {
            entries.Clear();
            reservesByType.Clear();
        }

        private sealed class Entry
        {
            public List<QuestPart> parts;
            public int version;
            public int[] reserving;
            public int[] lending;
        }

        // Quest has reference equality; entries of removed quests are dropped with the game.
        private static readonly Dictionary<Quest, Entry> entries = new Dictionary<Quest, Entry>();
        private static readonly Dictionary<Type, bool> reservesByType = new Dictionary<Type, bool>();
        private static readonly List<int> scratch = new List<int>();

        public static bool QuestReservesPrefix(Quest __instance, Pawn p, ref bool __result, out int __state)
        {
            __state = -1;
            if (!UnityData.IsInMainThread) return true;
            if (Verify.Enabled)
            {
                __state = QuestReserves(__instance, p) ? 1 : 0;
                return true;
            }
            __result = QuestReserves(__instance, p);
            return false;
        }

        public static bool IsBorrowedPrefix(Pawn pawn, ref bool __result, out int __state)
        {
            __state = -1;
            if (!UnityData.IsInMainThread) return true;
            if (Verify.Enabled)
            {
                __state = IsBorrowed(pawn) ? 1 : 0;
                return true;
            }
            __result = IsBorrowed(pawn);
            return false;
        }

        public static void VerifyPostfix(bool __result, int __state)
        {
            if (__state >= 0) Verify.Record(OptId, __result == (__state == 1));
        }

        private static bool QuestReserves(Quest quest, Pawn p)
        {
            if (quest.Historical) return false;
            List<QuestPart> parts = partsRef(quest);
            Entry entry = EntryFor(quest, parts);
            int[] reserving = entry.reserving;
            int count = parts.Count;
            for (int k = 0; k < reserving.Length; k++)
            {
                int i = reserving[k];
                if (parts[i].QuestPartReserves(p)) return true;
                if (ListVersion<QuestPart>.Of(parts) != entry.version)
                {
                    // A part changed the list: continue exactly as vanilla's loop would.
                    for (i++; i < count; i++)
                        if (parts[i].QuestPartReserves(p)) return true;
                    return false;
                }
            }
            return false;
        }

        private static bool IsBorrowed(Pawn pawn)
        {
            List<Quest> quests = Find.QuestManager.ActiveQuestsListForReading;
            int count = quests.Count;
            for (int i = 0; i < count; i++)
            {
                Quest quest = quests[i];
                if (quest.State != QuestState.Ongoing) continue;
                List<QuestPart> parts = quest.PartsListForReading;
                int[] lending = EntryFor(quest, parts).lending;
                for (int k = 0; k < lending.Length; k++)
                    if (((QuestPart_LendColonistsToFaction)parts[lending[k]]).LentColonistsListForReading.Contains(pawn)) return true;
            }
            return false;
        }

        private static Entry EntryFor(Quest quest, List<QuestPart> parts)
        {
            int version = ListVersion<QuestPart>.Of(parts);
            if (entries.TryGetValue(quest, out Entry entry) && ReferenceEquals(entry.parts, parts) && entry.version == version) return entry;

            entry = new Entry { parts = parts, version = version };
            scratch.Clear();
            for (int i = 0; i < parts.Count; i++)
                if (parts[i] == null || Reserves(parts[i].GetType())) scratch.Add(i);
            entry.reserving = scratch.ToArray();
            scratch.Clear();
            for (int i = 0; i < parts.Count; i++)
                if (parts[i] is QuestPart_LendColonistsToFaction) scratch.Add(i);
            entry.lending = scratch.ToArray();
            scratch.Clear();
            entries[quest] = entry;
            return entry;
        }

        /// <summary>Any QuestPartReserves overload declared below QuestPart counts (conservative).</summary>
        private static bool Reserves(Type type)
        {
            if (!reservesByType.TryGetValue(type, out bool reserves))
                reservesByType[type] = reserves = OverrideScan.Overrides(type, typeof(QuestPart), nameof(QuestPart.QuestPartReserves));
            return reserves;
        }
    }
}
