using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale.Optimizations.Pawns
{
    /// <summary>
    /// Pawn_GeneTracker.GeneTrackerTick (every tick) and GeneTrackerTickInterval (every tick interval) walk every gene
    /// of every humanlike, evaluating gene.Active (an age division plus a mutant lookup) and then calling Tick() /
    /// TickInterval(). No vanilla gene class overrides Tick, and the base TickInterval only acts for genes whose def
    /// can cause mental breaks. This swaps the two gene.Active calls in each method for a filter that answers "false"
    /// without evaluating anything when the following call is known to do nothing; every other gene runs exactly as
    /// vanilla (same Active call, same Tick call, same loop and list reads).
    ///
    /// Exact: a gene is skipped only if its runtime type (classified by reflection, mod types included)
    ///  - does not override Tick (resp. TickInterval), so the call is the empty Gene.Tick (resp. Gene.TickInterval, whose
    ///    body is guarded by def.mentalBreakMtbDays &gt; 0 &amp;&amp; def.mentalBreakDef != null, read live per call), and
    ///  - uses Gene.Active or Gene_ChemicalDependency.Active, which read fields only (age, mutant, ghoul) and cannot
    ///    throw while gene.def is set and the pawn is not a mutant (mutants and null defs always take the vanilla path).
    /// So vanilla's "if (Active) Tick()" has no effect for such a gene whichever way Active comes out; skipping both
    /// calls changes no state, consumes no Rand, and reorders nothing. Patches on any of these methods disable this.
    /// </summary>
    public sealed class GeneTicks : Optimization
    {
        public override string Id => "geneTicks";
        public override string Label => "Skip idle gene ticks";
        public override string Description =>
            "Genes that have nothing to do on a tick (nearly all of them) are no longer checked every tick for every pawn.";
        public override string Category => "Pawns";
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "brage.paralleltick" };

        private static readonly MethodInfo GetActive = AccessTools.PropertyGetter(typeof(Gene), nameof(Gene.Active));

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(Pawn_GeneTracker), nameof(Pawn_GeneTracker.GeneTrackerTick)))
                .WithTranspiler(typeof(GeneTicks), nameof(TickTranspiler));
            yield return PatchSpec.For(AccessTools.Method(typeof(Pawn_GeneTracker), nameof(Pawn_GeneTracker.GeneTrackerTickInterval)))
                .WithTranspiler(typeof(GeneTicks), nameof(TickIntervalTranspiler));
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            AccessTools.Method(typeof(Gene), nameof(Gene.Tick)),
            AccessTools.Method(typeof(Gene), nameof(Gene.TickInterval)),
            GetActive,
            AccessTools.DeclaredPropertyGetter(typeof(Gene_ChemicalDependency), nameof(Gene.Active)),
            AccessTools.PropertyGetter(typeof(Gene), nameof(Gene.Overridden)),
            AccessTools.PropertyGetter(typeof(Pawn_AgeTracker), nameof(Pawn_AgeTracker.AgeBiologicalYears)),
            AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.IsGhoul)),
            AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.IsMutant)),
        };

        public override void ClearCaches() => Kinds.Clear();

        public static IEnumerable<CodeInstruction> TickTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.SwapCalls(instructions, "geneTicks", GetActive, AccessTools.Method(typeof(GeneTicks), nameof(ActiveForTick)), 2);

        public static IEnumerable<CodeInstruction> TickIntervalTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.SwapCalls(instructions, "geneTicks", GetActive, AccessTools.Method(typeof(GeneTicks), nameof(ActiveForTickInterval)), 2);

        /// <summary>Replaces gene.Active in GeneTrackerTick: false (without evaluating) when the following Tick() is a no-op.</summary>
        public static bool ActiveForTick(Gene gene)
        {
            if (gene != null)
            {
                GeneDef def = gene.def;
                if (def != null && NotMutant(gene) && (Kinds.Of(gene, def) & Kinds.TickSkip) != 0) return false;
            }
            return gene.Active; // a null gene throws here, as it would in vanilla
        }

        /// <summary>Replaces gene.Active in GeneTrackerTickInterval: false when the following TickInterval() is a no-op.</summary>
        public static bool ActiveForTickInterval(Gene gene)
        {
            if (gene != null)
            {
                GeneDef def = gene.def;
                if (def != null && NotMutant(gene) && (Kinds.Of(gene, def) & Kinds.IntervalSkip) != 0
                    && !(def.mentalBreakMtbDays > 0f && def.mentalBreakDef != null))
                    return false;
            }
            return gene.Active;
        }

        private static bool NotMutant(Gene gene)
        {
            Pawn pawn = gene.pawn;
            return pawn == null || pawn.mutant == null;
        }

        /// <summary>Per runtime gene type: which calls are no-ops. Cached per GeneDef slot, keyed on the exact type.</summary>
        private static class Kinds
        {
            public const byte TickSkip = 1;
            public const byte IntervalSkip = 2;
            private const int MaxSlots = 1 << 14;

            private static Type[] slotType = new Type[0];
            private static byte[] slotKind = new byte[0];
            private static readonly Dictionary<Type, byte> byType = new Dictionary<Type, byte>();

            public static void Clear()
            {
                slotType = new Type[0];
                slotKind = new byte[0];
                byType.Clear();
            }

            public static byte Of(Gene gene, GeneDef def)
            {
                Type type = gene.GetType();
                int i = def.index;
                Type[] types = slotType;
                if (i < types.Length && ReferenceEquals(types[i], type)) return slotKind[i];
                return Slow(type, i);
            }

            private static byte Slow(Type type, int slot)
            {
                if (!byType.TryGetValue(type, out byte kind))
                {
                    kind = Classify(type);
                    byType[type] = kind;
                }
                if (slot < MaxSlots)
                {
                    if (slot >= slotType.Length)
                    {
                        int size = Math.Max(slot + 1, Math.Max(64, slotType.Length * 2));
                        Array.Resize(ref slotType, size);
                        Array.Resize(ref slotKind, size);
                    }
                    slotKind[slot] = kind;
                    slotType[slot] = type;
                }
                return kind;
            }

            private static byte Classify(Type type)
            {
                bool activeKnown = !OverrideScan.Overrides(type, typeof(Gene), "get_Active", except: typeof(Gene_ChemicalDependency));
                if (!activeKnown) return 0;
                byte kind = 0;
                if (!OverrideScan.Overrides(type, typeof(Gene), nameof(Gene.Tick))) kind |= TickSkip;
                if (!OverrideScan.Overrides(type, typeof(Gene), nameof(Gene.TickInterval))) kind |= IntervalSkip;
                return kind;
            }
        }
    }
}
