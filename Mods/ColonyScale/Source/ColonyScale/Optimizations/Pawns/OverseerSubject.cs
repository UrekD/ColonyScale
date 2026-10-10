using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale.Optimizations.Pawns
{
    /// <summary>
    /// Every colony mech's CompOverseerSubject.CompTick evaluates CanGoFeral twice per tick, and each evaluation reaches
    /// CompOverseerSubject.State, which looks up the overseer relation twice and then runs List.Contains over the
    /// mechanitor's controlled mechs (Pawn.Equals per element, string compare included). State is also behind
    /// Pawn.IsColonyMechPlayerControlled, used widely by AI and UI.
    ///
    /// CompTick (replaced, statement for statement): when the first CanGoFeral returns false and no effecter is attached
    /// (the steady state of a controlled mech), vanilla runs nothing between the two calls (a null check and storing
    /// null into a field that is already null), so the second call is answered with the first result. Whenever an
    /// effecter is spawned, cleaned up or ticked in between, or the debug flag skips the first call, the second call
    /// runs as in vanilla. CanGoFeral is deterministic and only fills idempotent caches (capacity levels, comp lookup).
    ///
    /// State (replaced): the overseer is looked up once instead of twice (nothing runs between vanilla's two lookups).
    /// Membership is answered from a reference set mirroring the controlled list, rebuilt whenever the list's _version
    /// or count changes. A reference hit implies vanilla's Contains is true: Contains calls element.Equals(pawn) from
    /// the front, Pawn.Equals compares def.defName and thingIDNumber, so it is true for the pawn itself and cannot throw
    /// on earlier elements as long as the pawn and every element have a def; the mirror is only used when that was true
    /// at its last rebuild and the pawn has a def. A miss falls back to vanilla's Contains, so pawns that are equal
    /// without being identical are still found. Short lists use vanilla's Contains directly.
    ///
    /// Threading: State is read only from main-thread code (ticks, AI, UI); 1.6's parallel path-grid sources never
    /// read pawn state, so the static mirrors are not shared across threads.
    /// </summary>
    public sealed class OverseerSubject : Optimization
    {
        public override string Id => "overseerSubject";
        public override string Label => "Faster mech control checks";
        public override string Description =>
            "Colony mechs check whether their mechanitor controls them without scanning the mechanitor's whole mech list, " +
            "and only once per tick when nothing changed in between. Helps colonies with many mechs.";
        public override string Category => "Pawns";
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "brage.paralleltick" };

        private static readonly AccessTools.FieldRef<CompOverseerSubject, Effecter> effectRef =
            AccessTools.FieldRefAccess<CompOverseerSubject, Effecter>("effect");
        private static readonly AccessTools.FieldRef<CompOverseerSubject, int> delayRef =
            AccessTools.FieldRefAccess<CompOverseerSubject, int>("delayUntilFeralCheck");
        private static readonly MethodInfo CanGoFeralMethod = AccessTools.Method(typeof(CompOverseerSubject), "CanGoFeral");
        private static readonly Func<Pawn, bool> canGoFeral = AccessTools.MethodDelegate<Func<Pawn, bool>>(CanGoFeralMethod);

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(CompOverseerSubject), nameof(CompOverseerSubject.CompTick)))
                .WithPrefix(typeof(OverseerSubject), nameof(CompTickPrefix), replacesOriginal: true, priority: Priority.Last);
            yield return PatchSpec.For(AccessTools.PropertyGetter(typeof(CompOverseerSubject), nameof(CompOverseerSubject.State)))
                .WithPrefix(typeof(OverseerSubject), nameof(StatePrefix), replacesOriginal: true, priority: Priority.Last)
                .WithPostfix(typeof(OverseerSubject), nameof(StatePostfix), priority: Priority.First);
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            AccessTools.Method(typeof(ThingComp), nameof(ThingComp.CompTick)),
            CanGoFeralMethod,
            AccessTools.PropertyGetter(typeof(CompOverseerSubject), "Overseer"),
            AccessTools.Method(typeof(global::MechanitorUtility), nameof(global::MechanitorUtility.IsColonyMechRequiringMechanitor)),
            AccessTools.Method(typeof(RestUtility), nameof(RestUtility.Awake)),
            AccessTools.Method(typeof(RestUtility), nameof(RestUtility.IsDeactivated)),
            AccessTools.PropertyGetter(typeof(PawnCapacitiesHandler), nameof(PawnCapacitiesHandler.CanBeAwake)),
            AccessTools.Method(typeof(PawnCapacitiesHandler), nameof(PawnCapacitiesHandler.GetLevel)),
            AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.IsColonyMech)),
            AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.OverseerSubject)),
            AccessTools.Method(typeof(Pawn_RelationsTracker), nameof(Pawn_RelationsTracker.GetFirstDirectRelationPawn)),
            AccessTools.PropertyGetter(typeof(Pawn_MechanitorTracker), nameof(Pawn_MechanitorTracker.ControlledPawns)),
            AccessTools.Method(typeof(Pawn), nameof(Pawn.Equals), new[] { typeof(Pawn) }),
        };

        public override void ClearCaches() => Mirrors.Clear();

        public static bool CompTickPrefix(CompOverseerSubject __instance)
        {
            // base.CompTick(): ThingComp.CompTick is empty.
            bool known = false;
            bool first = false;
            bool showEffect;
            if (CompOverseerSubject.debugDisableNeedsOverseerEffect)
            {
                showEffect = false;
            }
            else
            {
                first = canGoFeral(__instance.Parent);
                known = true;
                showEffect = first;
            }

            ref Effecter effect = ref effectRef(__instance);
            if (showEffect)
            {
                if (__instance.Props.needsOverseerEffect != null && effect == null)
                    effect = __instance.Props.needsOverseerEffect.SpawnAttached(__instance.parent, __instance.parent.Map);
                known = false;
            }
            else
            {
                Effecter old = effect;
                if (old != null)
                {
                    old.Cleanup();
                    known = false;
                }
                effect = null;
            }
            Effecter current = effect;
            if (current != null)
            {
                current.EffectTick(__instance.parent, __instance.parent);
                known = false;
            }

            bool second;
            if (known)
            {
                second = first;
                if (Verify.Enabled) Verify.Record("overseerSubject.tick", canGoFeral(__instance.Parent) == first);
            }
            else
            {
                second = canGoFeral(__instance.Parent);
            }

            if (second)
            {
                ref int delay = ref delayRef(__instance);
                if (delay > 0) delay--;
                if (delay <= 0 && Rand.MTBEventOccurs(__instance.Props.feralMtbDays, 60000f, 1f))
                    __instance.TryMakeFeral();
            }
            return false;
        }

        public static bool StatePrefix(CompOverseerSubject __instance, ref OverseerSubjectState __result, out OverseerSubjectState? __state)
        {
            __state = null;
            OverseerSubjectState fast = FastState(__instance);
            if (Verify.Enabled)
            {
                __state = fast;
                return true;
            }
            __result = fast;
            return false;
        }

        public static void StatePostfix(OverseerSubjectState __result, OverseerSubjectState? __state)
        {
            if (__state.HasValue) Verify.Record("overseerSubject.state", __result == __state.Value);
        }

        private static OverseerSubjectState FastState(CompOverseerSubject comp)
        {
            // Body of the private Overseer getter, evaluated once.
            Pawn overseer = ((Pawn)comp.parent)?.relations?.GetFirstDirectRelationPawn(PawnRelationDefOf.Overseer);
            if (overseer?.mechanitor == null) return OverseerSubjectState.RequiresOverseer;
            List<Pawn> controlled = overseer.mechanitor.ControlledPawns;
            return Mirrors.Contains(controlled, comp.Parent) ? OverseerSubjectState.Overseen : OverseerSubjectState.RequiresBandwidth;
        }

        /// <summary>Reference sets mirroring mechanitors' controlled-mech lists (one entry per list, a handful at most).</summary>
        private static class Mirrors
        {
            private const int SmallList = 8;
            private const int Capacity = 16;

            private sealed class Mirror
            {
                public List<Pawn> list;
                public int version;
                public int count;
                public bool usable; // every element had a def at the last rebuild
                public readonly HashSet<Pawn> set = new HashSet<Pawn>(ReferenceComparer.Instance);
            }

            private static readonly Mirror[] mirrors = new Mirror[Capacity];
            private static int nextEvict;
            private static Game game; // mirrors hold pawns: drop them when another game is loaded

            public static void Clear()
            {
                for (int i = 0; i < mirrors.Length; i++) mirrors[i] = null;
                nextEvict = 0;
                game = null;
            }

            public static bool Contains(List<Pawn> list, Pawn pawn)
            {
                if (list.Count <= SmallList || pawn.def == null) return list.Contains(pawn);
                Mirror m = For(list);
                if (m.usable && m.set.Contains(pawn)) return true;
                return list.Contains(pawn);
            }

            private static Mirror For(List<Pawn> list)
            {
                if (!ReferenceEquals(Current.Game, game))
                {
                    Clear();
                    game = Current.Game;
                }
                int version = ListVersion<Pawn>.Of(list);
                Mirror m = null;
                for (int i = 0; i < mirrors.Length; i++)
                {
                    Mirror c = mirrors[i];
                    if (c != null && ReferenceEquals(c.list, list)) { m = c; break; }
                }
                if (m == null)
                {
                    m = new Mirror { list = list, version = version - 1 };
                    mirrors[nextEvict] = m;
                    nextEvict = (nextEvict + 1) % mirrors.Length;
                }
                if (m.version != version || m.count != list.Count)
                {
                    m.set.Clear();
                    m.usable = true;
                    for (int i = 0; i < list.Count; i++)
                    {
                        Pawn p = list[i];
                        if (p == null) continue;
                        if (p.def == null) m.usable = false;
                        m.set.Add(p);
                    }
                    m.version = version;
                    m.count = list.Count;
                }
                return m;
            }

            private sealed class ReferenceComparer : IEqualityComparer<Pawn>
            {
                public static readonly ReferenceComparer Instance = new ReferenceComparer();
                public bool Equals(Pawn x, Pawn y) => ReferenceEquals(x, y);
                public int GetHashCode(Pawn obj) => RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
