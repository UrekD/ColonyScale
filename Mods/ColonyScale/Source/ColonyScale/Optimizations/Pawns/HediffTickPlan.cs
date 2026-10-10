using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace ColonyScale.Optimizations.Pawns
{
    /// <summary>
    /// Pawn_HealthTracker.HealthTick runs every tick for every living pawn and calls Tick() and PostTick() on each hediff.
    /// In 1.6 the per-hediff work moved to TickInterval: Hediff.Tick and Hediff.PostTick are empty, no vanilla hediff
    /// overrides Tick, and the only PostTick override is HediffWithComps.PostTick, which calls CompPostTick on each comp
    /// (empty in HediffComp; overridden by ~20 comp classes such as tend duration and disappearing hediffs). This inserts
    /// one check at the top of the loop body: if both calls are provably no-ops for this hediff, continue with the next
    /// hediff. Everything else (the copy into tmpHediffs, the removed-hediff set, try/catch, the Dead checks, the
    /// ShouldRemove pass) is vanilla code running unchanged.
    ///
    /// Exact: a hediff is skipped only when its runtime type overrides neither Tick nor PostTick between itself and Hediff
    /// (HediffWithComps.PostTick excepted), and, for HediffWithComps, every comp currently in its comps list has a runtime
    /// type that does not override CompPostTick (so severityAdjustment stays 0 and Severity is not written). Then vanilla's
    /// iteration for it would call two empty methods and re-read Dead, which nothing has changed since its last check;
    /// skipping it changes no state and consumes no Rand. Classification is by runtime type, so comps added at runtime and
    /// mod subclasses are handled; null hediffs, null defs and null comps take the vanilla path.
    ///
    /// Two comp classes override CompPostTick but are idle in the common case, so for those exact runtime types (not
    /// subclasses) the live state is tested on every tick: HediffComp_TendDuration.CompPostTick (after the empty
    /// HediffComp_SeverityModifierBase/HediffComp base) only decrements tendTicksLeft when tendTicksLeft > 0 and
    /// !TProps.TendIsPermanent (baseTendDurationHours &lt; 0, the default for injuries and scars); HediffComp_Link.CompPostTick
    /// only acts when drawConnection is set. Neither touches severityAdjustment, draws Rand or reads anything else when
    /// idle, and both conditions are read live, so a tend or a new link is picked up on the very next tick. Props of an
    /// unexpected type take the vanilla path (which would throw and log).
    /// </summary>
    public sealed class HediffTickPlan : Optimization
    {
        public override string Id => "hediffTickPlan";
        public override string Label => "Skip idle hediff ticks";
        public override string Description =>
            "Health conditions with nothing to do on a tick (scars, implants, missing parts, most illnesses) are no longer " +
            "ticked every tick for every pawn. Their periodic updates are unaffected.";
        public override string Category => "Pawns";
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "brage.paralleltick" };

        private static readonly FieldInfo TmpRemovedHediffs = AccessTools.Field(typeof(Pawn_HealthTracker), "tmpRemovedHediffs");
        private static readonly MethodInfo SetContains = AccessTools.Method(typeof(HashSet<Hediff>), nameof(HashSet<Hediff>.Contains));
        private static readonly AccessTools.FieldRef<Hediff, float> severityInt = AccessTools.FieldRefAccess<Hediff, float>("severityInt");

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.HealthTick)))
                .WithTranspiler(typeof(HediffTickPlan), nameof(Transpiler));
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            AccessTools.Method(typeof(Hediff), nameof(Hediff.Tick)),
            AccessTools.Method(typeof(Hediff), nameof(Hediff.PostTick)),
            AccessTools.Method(typeof(HediffWithComps), nameof(HediffWithComps.PostTick)),
            AccessTools.Method(typeof(HediffComp), nameof(HediffComp.CompPostTick)),
            AccessTools.Method(typeof(HediffComp_TendDuration), nameof(HediffComp_TendDuration.CompPostTick)),
            AccessTools.Method(typeof(HediffComp_Link), nameof(HediffComp_Link.CompPostTick)),
            AccessTools.PropertyGetter(typeof(HediffCompProperties_TendDuration), nameof(HediffCompProperties_TendDuration.TendIsPermanent)),
            AccessTools.PropertyGetter(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.Dead)),
        };

        public override void ClearCaches() => Plans.Clear();

        /// <summary>
        /// Before "if (tmpRemovedHediffs.Contains(tmpHediff)) continue;" inserts "if (CanSkip(tmpHediff)) continue;",
        /// branching to the same continue label.
        /// </summary>
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            int at = -1, matches = 0;
            for (int i = 0; i + 3 < list.Count; i++)
            {
                if (list[i].LoadsField(TmpRemovedHediffs) && list[i + 1].IsLdloc()
                    && list[i + 2].Calls(SetContains) && (list[i + 3].opcode == OpCodes.Brtrue || list[i + 3].opcode == OpCodes.Brtrue_S))
                {
                    at = i;
                    matches++;
                }
            }
            if (matches != 1)
                throw new PatternNotFoundException("hediffTickPlan", $"removed-hediff check in the tick loop (found {matches})");

            var load = new CodeInstruction(list[at + 1].opcode, list[at + 1].operand);
            load.MoveLabelsFrom(list[at]);
            load.MoveBlocksFrom(list[at]);
            object continueLabel = list[at + 3].operand;
            list.InsertRange(at, new[]
            {
                load,
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HediffTickPlan), nameof(CanSkip))),
                new CodeInstruction(OpCodes.Brtrue, continueLabel),
            });
            return list;
        }

        public static bool CanSkip(Hediff hediff)
        {
            if (!TickIsNoOp(hediff)) return false;
            return !Verify.Enabled || VerifyNoOp(hediff);
        }

        /// <summary>Verify mode: run what vanilla would run for the skipped hediff and check it changed nothing visible.</summary>
        private static bool VerifyNoOp(Hediff hediff)
        {
            float before = severityInt(hediff);
            int tendBefore = TendTicks(hediff);
            try
            {
                hediff.Tick();
                hediff.PostTick();
            }
            catch (Exception)
            {
                Verify.Record("hediffTickPlan", false);
                return false; // let vanilla run (and report) it
            }
            Verify.Record("hediffTickPlan", severityInt(hediff).Equals(before) && TendTicks(hediff) == tendBefore);
            return true;
        }

        private static int TendTicks(Hediff hediff)
        {
            int sum = 0;
            if (hediff is HediffWithComps withComps && withComps.comps != null)
                foreach (HediffComp comp in withComps.comps)
                    if (comp is HediffComp_TendDuration tend) sum += tend.tendTicksLeft;
            return sum;
        }

        private static bool TickIsNoOp(Hediff hediff)
        {
            if (hediff == null) return false;
            HediffDef def = hediff.def;
            if (def == null) return false;
            Plans.Plan plan = Plans.For(hediff.GetType(), def);
            if (plan.kind == Plans.NoOp) return true;
            if (plan.kind == Plans.Ticks) return false;

            List<HediffComp> comps = ((HediffWithComps)hediff).comps;
            if (comps == null) return true;
            Type[] known = plan.compTypes;
            byte[] kinds = plan.compKinds;
            for (int i = 0; i < comps.Count; i++)
            {
                HediffComp comp = comps[i];
                if (comp == null) return false;
                Type type = comp.GetType();
                byte kind = i < known.Length && ReferenceEquals(known[i], type) ? kinds[i] : Plans.CompKind(type);
                if (kind != Plans.CompIdle && !CompIsIdleNow(kind, comp)) return false;
            }
            return true;
        }

        /// <summary>The live-state test for the two conditionally idle comp classes; every other kind ticks.</summary>
        private static bool CompIsIdleNow(byte kind, HediffComp comp)
        {
            if (kind == Plans.CompTend)
            {
                var tend = (HediffComp_TendDuration)comp;
                return tend.tendTicksLeft <= 0 || (tend.props is HediffCompProperties_TendDuration props && props.TendIsPermanent);
            }
            if (kind == Plans.CompLink) return !((HediffComp_Link)comp).drawConnection;
            return false;
        }

        /// <summary>
        /// Per runtime hediff type: whether Tick/PostTick can do anything. Cached per HediffDef slot together with the def's
        /// comp classes, which only serve as a fast path: each comp is still matched by its exact runtime type.
        /// </summary>
        private static class Plans
        {
            public const byte Ticks = 0, NoOp = 1, DependsOnComps = 2;
            public const byte CompIdle = 0, CompTicks = 1, CompTend = 2, CompLink = 3;
            private const int MaxSlots = 1 << 15;

            public sealed class Plan
            {
                public Type type;
                public byte kind;
                public Type[] compTypes;
                public byte[] compKinds;
            }

            private static Plan[] slots = new Plan[0];
            private static readonly Dictionary<Type, byte> kindByType = new Dictionary<Type, byte>();
            private static readonly Dictionary<Type, byte> compKindByType = new Dictionary<Type, byte>();

            public static void Clear()
            {
                slots = new Plan[0];
                kindByType.Clear();
                compKindByType.Clear();
            }

            public static Plan For(Type type, HediffDef def)
            {
                int i = def.index;
                Plan[] arr = slots;
                if (i < arr.Length)
                {
                    Plan p = arr[i];
                    if (p != null && ReferenceEquals(p.type, type)) return p;
                }
                return Build(type, def, i);
            }

            private static Plan Build(Type type, HediffDef def, int slot)
            {
                var plan = new Plan { type = type, kind = KindOf(type) };
                List<HediffCompProperties> props = def.comps;
                int n = props?.Count ?? 0;
                plan.compTypes = new Type[n];
                plan.compKinds = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    Type compClass = props[i]?.compClass;
                    plan.compTypes[i] = compClass;
                    plan.compKinds[i] = compClass == null ? CompTicks : CompKind(compClass);
                }
                if (slot < MaxSlots)
                {
                    if (slot >= slots.Length) Array.Resize(ref slots, Math.Max(slot + 1, Math.Max(64, slots.Length * 2)));
                    slots[slot] = plan;
                }
                return plan;
            }

            private static byte KindOf(Type type)
            {
                if (kindByType.TryGetValue(type, out byte kind)) return kind;
                if (OverrideScan.Overrides(type, typeof(Hediff), nameof(Hediff.Tick))
                    || OverrideScan.Overrides(type, typeof(Hediff), nameof(Hediff.PostTick), except: typeof(HediffWithComps)))
                    kind = Ticks;
                else
                    kind = typeof(HediffWithComps).IsAssignableFrom(type) ? DependsOnComps : NoOp;
                kindByType[type] = kind;
                return kind;
            }

            public static byte CompKind(Type compType)
            {
                if (!compKindByType.TryGetValue(compType, out byte kind))
                {
                    if (compType == typeof(HediffComp_TendDuration)) kind = CompTend;
                    else if (compType == typeof(HediffComp_Link)) kind = CompLink;
                    else kind = OverrideScan.Overrides(compType, typeof(HediffComp), nameof(HediffComp.CompPostTick)) ? CompTicks : CompIdle;
                    compKindByType[compType] = kind;
                }
                return kind;
            }
        }
    }
}
