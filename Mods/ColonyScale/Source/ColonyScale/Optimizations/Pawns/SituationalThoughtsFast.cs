using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale.Optimizations.Pawns
{
    /// <summary>
    /// SituationalThoughtHandler.UpdateAllMoodThoughts runs every mood interval for every pawn with a mood. For each
    /// of the ~290 non-social situational thought defs it scans all of the pawn's cached situational thoughts (without
    /// breaking) to learn whether one with that def exists: defs x cached thoughts reads per call. This keeps the
    /// question "does any cached thought have this def" in a stamp array indexed by Def.index.
    ///
    /// Exact: the body is vanilla's statement for statement (same RecalculateState loop, same def order, same
    /// TryCreateThought calls through the original method so its patches apply, same precept loop, thoughtsDirty
    /// cleared first). Only the membership scan changes, and it reads nothing but Thought.def, so skipping it is
    /// unobservable. The set is the set of defs of the list at the moment vanilla would scan it: defs of the list
    /// after the RecalculateState loop, plus each thought we add; any other change to the list (List._version moved,
    /// e.g. a reentrant call during TryCreateThought) rebuilds it from the list before the next check. ThoughtDef has
    /// no == operator and Def does not override Equals, so vanilla's test is reference equality, as here. Defs sharing
    /// an index (or not in the database, index 65535) and null defs fall back to vanilla's linear scan.
    /// </summary>
    public sealed class SituationalThoughtsFast : Optimization
    {
        private const string OptId = "situationalThoughts";

        public override string Id => OptId;
        public override string Label => "Faster situational thought refresh";
        public override string Description =>
            "Refreshing a pawn's situational thoughts no longer rescans all of its current thoughts once for every thought type in the game.";
        public override string Category => "Pawns";

        private static AccessTools.FieldRef<SituationalThoughtHandler, List<Thought_Situational>> cachedThoughtsRef;
        private static AccessTools.FieldRef<SituationalThoughtHandler, bool> thoughtsDirtyRef;
        private static AccessTools.FieldRef<List<Thought_Situational>, int> listVersion;
        private static Func<SituationalThoughtHandler, ThoughtDef, Thought_Situational> tryCreateThought;

        private static MethodInfo TryCreateThoughtMethod => AccessTools.Method(typeof(SituationalThoughtHandler), "TryCreateThought");

        public override IEnumerable<PatchSpec> GetPatches()
        {
            cachedThoughtsRef = AccessTools.FieldRefAccess<SituationalThoughtHandler, List<Thought_Situational>>("cachedThoughts");
            thoughtsDirtyRef = AccessTools.FieldRefAccess<SituationalThoughtHandler, bool>("thoughtsDirty");
            listVersion = AccessTools.FieldRefAccess<List<Thought_Situational>, int>("_version");
            var tryCreate = TryCreateThoughtMethod ?? throw new MissingMethodException(nameof(SituationalThoughtHandler), "TryCreateThought");
            // A delegate to the original method: calls go through Harmony's detour, so patches on it still apply.
            tryCreateThought = AccessTools.MethodDelegate<Func<SituationalThoughtHandler, ThoughtDef, Thought_Situational>>(tryCreate);

            yield return PatchSpec.For(AccessTools.Method(typeof(SituationalThoughtHandler), "UpdateAllMoodThoughts"))
                .WithPrefix(typeof(SituationalThoughtsFast), nameof(Prefix), replacesOriginal: true, priority: Priority.Last);
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            TryCreateThoughtMethod,
        };

        public override void ClearCaches()
        {
            stamps = new int[0];
            owners = new ThoughtDef[0];
            stamp = 0;
        }

        // Membership set: def d is in the set iff stamps[d.index] == stamp && owners[d.index] == d (unless collided).
        private static int[] stamps = new int[0];
        private static ThoughtDef[] owners = new ThoughtDef[0];
        private static int stamp;
        private static bool collided;
        private static bool busy;

        public static bool Prefix(SituationalThoughtHandler __instance)
        {
            // Static buffers: a reentrant call (another pawn's thoughts evaluated from inside TryCreateThought) or a
            // call off the main thread runs vanilla.
            if (busy || !UnityData.IsInMainThread) return true;
            busy = true;
            try
            {
                UpdateAllMoodThoughts(__instance);
            }
            finally
            {
                busy = false;
            }
            return false;
        }

        /// <summary>Mirrors SituationalThoughtHandler.UpdateAllMoodThoughts.</summary>
        private static void UpdateAllMoodThoughts(SituationalThoughtHandler handler)
        {
            thoughtsDirtyRef(handler) = false;
            List<ThoughtDef> situationalNonSocialThoughtDefs = ThoughtUtility.situationalNonSocialThoughtDefs;
            List<Thought_Situational> cachedThoughts = cachedThoughtsRef(handler); // readonly field
            foreach (Thought_Situational cachedThought in cachedThoughts)
            {
                cachedThought.RecalculateState();
            }

            MarkAll(cachedThoughts);
            int version = listVersion(cachedThoughts);
            bool verify = Verify.Enabled;
            foreach (ThoughtDef item in situationalNonSocialThoughtDefs)
            {
                if (listVersion(cachedThoughts) != version)
                {
                    // Changed behind our back (reentrancy): rebuild from the list exactly when vanilla would rescan it.
                    MarkAll(cachedThoughts);
                    version = listVersion(cachedThoughts);
                }
                bool flag = Has(cachedThoughts, item);
                if (verify) Verify.Record(OptId, flag == ScanHas(cachedThoughts, item));
                if (!flag)
                {
                    Thought_Situational thought_Situational = tryCreateThought(handler, item);
                    if (thought_Situational != null)
                    {
                        bool unchanged = listVersion(cachedThoughts) == version;
                        cachedThoughts.Add(thought_Situational);
                        if (unchanged)
                        {
                            Mark(thought_Situational.def);
                            version = listVersion(cachedThoughts);
                        }
                    }
                }
            }

            // handler.pawn is re-read at each use, like vanilla's ldfld.
            if (!ModsConfig.IdeologyActive || handler.pawn.Ideo == null)
            {
                return;
            }
            foreach (Precept item2 in handler.pawn.Ideo.PreceptsListForReading)
            {
                foreach (Thought_Situational item3 in item2.SituationThoughtsToAdd(handler.pawn, cachedThoughts))
                {
                    cachedThoughts.Add(item3);
                }
            }
        }

        /// <summary>Vanilla's membership test: a full scan without break.</summary>
        private static bool ScanHas(List<Thought_Situational> cachedThoughts, ThoughtDef item)
        {
            bool flag = false;
            foreach (Thought_Situational cachedThought in cachedThoughts)
            {
                if (cachedThought.def == item)
                {
                    flag = true;
                }
            }
            return flag;
        }

        private static void MarkAll(List<Thought_Situational> list)
        {
            if (stamp == int.MaxValue)
            {
                Array.Clear(stamps, 0, stamps.Length);
                stamp = 0;
            }
            stamp++;
            collided = false;
            // Same NullReferenceException as vanilla's scan if the list holds a null thought.
            for (int i = 0; i < list.Count; i++) Mark(list[i].def);
        }

        private static void Mark(ThoughtDef def)
        {
            if (def == null) return; // never equal to a def from situationalNonSocialThoughtDefs; Has(null) scans
            int idx = def.index;
            if (idx == ushort.MaxValue) { collided = true; return; } // not in the def database: no unique index
            if (idx >= stamps.Length)
            {
                int size = Math.Max(idx + 1, DefDatabase<ThoughtDef>.DefCount + 16);
                Array.Resize(ref stamps, size);
                Array.Resize(ref owners, size);
            }
            if (stamps[idx] == stamp && !ReferenceEquals(owners[idx], def)) { collided = true; return; }
            stamps[idx] = stamp;
            owners[idx] = def;
        }

        private static bool Has(List<Thought_Situational> list, ThoughtDef item)
        {
            if (collided || item == null) return ScanHas(list, item);
            int idx = item.index;
            return idx < stamps.Length && stamps[idx] == stamp && ReferenceEquals(owners[idx], item);
        }
    }
}
