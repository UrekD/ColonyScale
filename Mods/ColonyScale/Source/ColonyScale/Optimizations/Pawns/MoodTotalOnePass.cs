using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale.Optimizations.Pawns
{
    /// <summary>
    /// ThoughtHandler.TotalMoodOffset runs every mood interval for every pawn with a mood. Vanilla rebuilds the
    /// full mood-thought list (calling MoodOffset() on every memory, each doing two nullification checks) once
    /// to find the distinct groups and then once more per group: (groups+1) x memories MoodOffset calls, O(n^2)
    /// over a colony because social memories grow with colony size. This builds the list once, evaluates each
    /// MoodOffset once, and reproduces vanilla's grouping and arithmetic exactly (same members, same order,
    /// same float operations, same rounding points).
    ///
    /// Exact because every one of vanilla's repeated GetAllMoodThoughts snapshots equals the first, and repeated
    /// MoodOffset/GroupsWith calls return the same value without observable effects. Vanilla is run instead when:
    /// - the situational cache is dirty: the first GetAllMoodThoughts would rebuild it, and a thought becoming
    ///   (in)active adds or removes its producesMemoryThought memory after the memories were copied;
    /// - a memory or active situational thought is of a class, or has a worker, whose MoodOffset, BaseMoodOffset,
    ///   CurStageIndex, GroupsWith, LabelCap (memory grouping), DurationTicks, ShouldDiscard (Thought_Memory),
    ///   OpinionOffset (Thought_MemorySocial), MoodMultiplier or PostProcessLabel comes from outside the game assembly. The game's own implementations (all in DependsOn, so a patch on any of them turns this
    ///   off) only read state, apart from:
    /// - Thought_OpinionOfMyLover and Thought_SharedBed, whose offset calls OpinionOf, which recalculates the pawn's
    ///   situational social thoughts at most once per 100 ticks; a social thought becoming (in)active could change
    ///   memories. No vanilla social situational def produces a memory, so this is allowed only while every such def
    ///   has no producesMemoryThought and a game-assembly thought class and worker.
    /// Idempotent effects (that social recalculation, stat caches) happen on a thought's first evaluation, which runs
    /// in vanilla's order: groups in list order, members in list order. Assumption, not checked: Pawn.GetStatValue
    /// inside MoodOffset (mod StatWorkers and StatParts) is free of effects other than caching (the UI evaluates it
    /// at arbitrary times), as are PawnRelationWorkers reached through OpinionOf. A null CurStage logs an
    /// error per MoodOffset call, so it is logged fewer times here.
    /// </summary>
    public sealed class MoodTotalOnePass : Optimization
    {
        public override string Id => "moodTotal";
        public override string Label => "One-pass mood total";
        public override string Description =>
            "Each pawn's mood total is computed from one pass over its thoughts instead of one pass per thought group.";
        public override string Category => "Pawns";

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(ThoughtHandler), nameof(ThoughtHandler.TotalMoodOffset)))
                .WithPrefix(typeof(MoodTotalOnePass), nameof(Prefix), replacesOriginal: true, priority: Priority.Last)
                .WithPostfix(typeof(MoodTotalOnePass), nameof(Postfix), priority: Priority.First);
            foreach (var p in CacheResets.Patches(typeof(MoodTotalOnePass), nameof(ResetCaches))) yield return p;
        }

        private static readonly Assembly GameAssembly = typeof(Thought).Assembly;

        private static readonly (Type root, string name, Type[] args)[] ThoughtMembers =
        {
            (typeof(Thought), nameof(Thought.MoodOffset), Type.EmptyTypes),
            (typeof(Thought), "get_BaseMoodOffset", Type.EmptyTypes),
            (typeof(Thought), "get_" + nameof(Thought.CurStageIndex), Type.EmptyTypes),
            (typeof(Thought), nameof(Thought.GroupsWith), new[] { typeof(Thought) }),
            (typeof(Thought), "get_" + nameof(Thought.LabelCap), Type.EmptyTypes),
            (typeof(Thought), "get_" + nameof(Thought.DurationTicks), Type.EmptyTypes),
            (typeof(Thought_Memory), "get_" + nameof(Thought_Memory.ShouldDiscard), Type.EmptyTypes),
            (typeof(Thought_MemorySocial), nameof(Thought_MemorySocial.OpinionOffset), Type.EmptyTypes),
        };

        private static readonly (Type root, string name, Type[] args)[] WorkerMembers =
        {
            (typeof(ThoughtWorker), nameof(ThoughtWorker.MoodMultiplier), new[] { typeof(Pawn) }),
            (typeof(ThoughtWorker), nameof(ThoughtWorker.PostProcessLabel), new[] { typeof(Pawn), typeof(string) }),
        };

        // We reproduce the first five; the rest are what the argument above relies on.
        public override IEnumerable<MethodBase> DependsOn
        {
            get
            {
                var methods = new List<MethodBase>
                {
                    AccessTools.Method(typeof(ThoughtHandler), nameof(ThoughtHandler.GetDistinctMoodThoughtGroups)),
                    AccessTools.Method(typeof(ThoughtHandler), nameof(ThoughtHandler.MoodOffsetOfGroup)),
                    AccessTools.Method(typeof(ThoughtHandler), nameof(ThoughtHandler.GetMoodThoughts)),
                    AccessTools.Method(typeof(ThoughtHandler), nameof(ThoughtHandler.GetAllMoodThoughts)),
                    AccessTools.Method(typeof(SituationalThoughtHandler), nameof(SituationalThoughtHandler.AppendMoodThoughts), new[] { typeof(List<Thought>) }),
                    AccessTools.Method(typeof(SituationalThoughtHandler), "CheckRecalculateSocialThoughts"),
                    AccessTools.Method(typeof(Thought_Situational), nameof(Thought_Situational.RecalculateState)),
                    AccessTools.Method(typeof(Thought_Situational), "Notify_BecameActive"),
                    AccessTools.Method(typeof(Thought_Situational), "Notify_BecameInactive"),
                    AccessTools.Method(typeof(ThoughtUtility), nameof(ThoughtUtility.ThoughtNullified)),
                    AccessTools.Method(typeof(Pawn_RelationsTracker), nameof(Pawn_RelationsTracker.OpinionOf)),
                };
                methods.AddRange(GameImplementations(ThoughtMembers));
                methods.AddRange(GameImplementations(WorkerMembers));
                return methods;
            }
        }

        /// <summary>Every body the game assembly declares for these members, in a stable order.</summary>
        private static IEnumerable<MethodBase> GameImplementations((Type root, string name, Type[] args)[] members)
        {
            Type[] types;
            try { types = GameAssembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            return types
                .Where(t => t.IsClass)
                .SelectMany(t => members
                    .Where(m => m.root.IsAssignableFrom(t))
                    .Select(m => t.GetMethod(m.name, declared, null, m.args, null)))
                .Where(m => m != null && !m.IsAbstract && m.GetMethodBody() != null)
                .OrderBy(m => m.DeclaringType.FullName, StringComparer.Ordinal)
                .ThenBy(m => m.Name, StringComparer.Ordinal);
        }

        private static readonly AccessTools.FieldRef<SituationalThoughtHandler, bool> thoughtsDirty =
            AccessTools.FieldRefAccess<SituationalThoughtHandler, bool>("thoughtsDirty");
        private static readonly AccessTools.FieldRef<SituationalThoughtHandler, List<Thought_Situational>> cachedThoughts =
            AccessTools.FieldRefAccess<SituationalThoughtHandler, List<Thought_Situational>>("cachedThoughts");

        public override void ClearCaches() => ResetCaches();

        public static void ResetCaches()
        {
            gameCode.Clear();
            socialRecalcKeepsMemories = null;
        }

        private static readonly List<Thought> thoughts = new List<Thought>();
        private static float[] offsets = new float[64];
        private static bool[] evaluated = new bool[64];
        private static bool computing;

        public static bool Prefix(ThoughtHandler __instance, ref float __result, out float? __state)
        {
            __state = null;
            if (computing) return true; // re-entered from inside a thought evaluation: let vanilla handle it
            if (!SnapshotsStable(__instance)) return true;
            float fast = Compute(__instance);
            if (Verify.Enabled)
            {
                __state = fast;
                return true;
            }
            __result = fast;
            return false;
        }

        public static void Postfix(float __result, float? __state)
        {
            if (__state.HasValue) Verify.Record("moodTotal", __result.Equals(__state.Value));
        }

        /// <summary>Reads only fields and types: deciding must not evaluate anything vanilla would not.</summary>
        private static bool SnapshotsStable(ThoughtHandler handler)
        {
            var situational = handler.situational;
            if (situational == null || thoughtsDirty(situational)) return false;
            var memories = handler.memories.Memories;
            for (int i = 0; i < memories.Count; i++)
                if (!GameCode(memories[i])) return false;
            var cached = cachedThoughts(situational);
            for (int i = 0; i < cached.Count; i++)
            {
                var s = cached[i];
                if (!s.Active) continue; // not in the list, never evaluated
                if (!GameCode(s)) return false;
                if ((s is Thought_OpinionOfMyLover || s is Thought_SharedBed) && !SocialRecalcKeepsMemories) return false;
            }
            return true;
        }

        private static readonly Dictionary<Type, bool> gameCode = new Dictionary<Type, bool>();

        private static bool GameCode(Thought t) =>
            GameCode(t.GetType(), ThoughtMembers) && (t.def.workerClass == null || GameCode(t.def.workerClass, WorkerMembers));

        private static bool GameCode(Type type, (Type root, string name, Type[] args)[] members)
        {
            if (gameCode.TryGetValue(type, out bool ok)) return ok;
            ok = true;
            try
            {
                foreach (var m in members)
                {
                    if (!m.root.IsAssignableFrom(type)) continue; // member only exists on a subtree of thoughts
                    var method = type.GetMethod(m.name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, m.args, null);
                    if (method == null || method.DeclaringType.Assembly != GameAssembly) ok = false;
                }
            }
            catch (Exception)
            {
                ok = false;
            }
            gameCode[type] = ok;
            return ok;
        }

        private static bool? socialRecalcKeepsMemories;

        /// <summary>Recalculating situational social thoughts cannot add or remove memories (true for the game's defs).</summary>
        private static bool SocialRecalcKeepsMemories => socialRecalcKeepsMemories ??= ComputeSocialRecalcKeepsMemories();

        private static bool ComputeSocialRecalcKeepsMemories()
        {
            var defs = ThoughtUtility.situationalSocialThoughtDefs;
            if (defs == null) return false;
            foreach (var def in defs)
            {
                if (def.producesMemoryThought != null) return false;
                if (def.ThoughtClass.Assembly != GameAssembly) return false;
                if (def.workerClass != null && def.workerClass.Assembly != GameAssembly) return false;
            }
            return true;
        }

        private static float Compute(ThoughtHandler handler)
        {
            computing = true;
            try
            {
                handler.GetAllMoodThoughts(thoughts);
                int n = thoughts.Count;
                if (offsets.Length < n)
                {
                    offsets = new float[n * 2];
                    evaluated = new bool[n * 2];
                }
                for (int i = 0; i < n; i++) evaluated[i] = false;

                float num = 0f;
                for (int j = 0; j < n; j++)
                {
                    // Vanilla keeps thought j as a group representative iff no earlier thought groups with it.
                    Thought rep = thoughts[j];
                    bool distinct = true;
                    for (int i = 0; i < j; i++)
                    {
                        if (thoughts[i].GroupsWith(rep)) { distinct = false; break; }
                    }
                    if (distinct) num += OffsetOfGroup(rep, n);
                }
                return num;
            }
            finally
            {
                thoughts.Clear();
                computing = false;
            }
        }

        /// <summary>Mirrors ThoughtHandler.MoodOffsetOfGroup statement for statement; kept a separate call so the
        /// float result is rounded at the same point as vanilla's return value. Each MoodOffset is evaluated where
        /// vanilla first sums it (groups in order, members in order) and reused for later groups.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float OffsetOfGroup(Thought group, int n)
        {
            int count = 0;
            float num = 0f;
            float num2 = 1f;
            float num3 = 0f;
            for (int k = 0; k < n; k++)
            {
                Thought thought = thoughts[k];
                if (!thought.GroupsWith(group)) continue;
                if (!evaluated[k])
                {
                    offsets[k] = thought.MoodOffset();
                    evaluated[k] = true;
                }
                count++;
                num += offsets[k];
                num3 += num2;
                num2 *= thought.def.stackedEffectMultiplier;
            }
            if (count == 0) return 0f;
            float num4 = num / (float)count;
            return num4 * num3;
        }
    }
}
