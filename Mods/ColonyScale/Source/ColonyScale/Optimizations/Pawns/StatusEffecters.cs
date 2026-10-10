using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace ColonyScale.Optimizations.Pawns
{
    /// <summary>
    /// PawnStatusEffecters.EffectersTick runs every tick for every pawn and calls hediff.TryGetComp&lt;HediffComp_Effecter&gt;()
    /// on each hediff. TryGetComp is a shared generic method, so every comp of every hediff gets a type test that goes
    /// through a runtime generic-type lookup. This swaps that one call for a non-generic copy of TryGetComp with the
    /// type fixed to HediffComp_Effecter: same hediff type test, same comps list, same order, same first match.
    ///
    /// Exact: the replacement is TryGetComp's body with T = HediffComp_Effecter, so it returns the same comp (or null)
    /// for every input and throws in the same cases. Both are pure and consume no Rand. TryGetComp is in DependsOn,
    /// so a patch on it disables this. No per-def shortcut is used: comps can be added or swapped at runtime, and
    /// only a scan of the live list is exact.
    /// </summary>
    public sealed class StatusEffecters : Optimization
    {
        public override string Id => "statusEffecters";
        public override string Label => "Cheaper status effecter scan";
        public override string Description =>
            "Each tick, pawns look for hediff visual effects with a cheaper type check.";
        public override string Category => "Pawns";
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "brage.paralleltick" };

        private static readonly MethodInfo TryGetEffecter =
            AccessTools.Method(typeof(HediffUtility), nameof(HediffUtility.TryGetComp), new[] { typeof(Hediff) }, new[] { typeof(HediffComp_Effecter) });

        public override IEnumerable<PatchSpec> GetPatches()
        {
            Type statusEffecters = typeof(Pawn).Assembly.GetType("Verse.PawnStatusEffecters", throwOnError: true);
            yield return PatchSpec.For(AccessTools.Method(statusEffecters, "EffectersTick"))
                .WithTranspiler(typeof(StatusEffecters), nameof(Transpiler));
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[] { TryGetEffecter };

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.SwapCalls(instructions, "statusEffecters", TryGetEffecter, AccessTools.Method(typeof(StatusEffecters), nameof(EffecterComp)), 1);

        /// <summary>HediffUtility.TryGetComp&lt;HediffComp_Effecter&gt;, statement for statement.</summary>
        public static HediffComp_Effecter EffecterComp(Hediff hd)
        {
            HediffComp_Effecter result = null;
            if (hd is HediffWithComps withComps && withComps.comps != null)
            {
                List<HediffComp> comps = withComps.comps;
                for (int i = 0; i < comps.Count; i++)
                {
                    if (comps[i] is HediffComp_Effecter found)
                    {
                        result = found;
                        break;
                    }
                }
            }
            if (Verify.Enabled) Verify.Record("statusEffecters", ReferenceEquals(result, hd.TryGetComp<HediffComp_Effecter>()));
            return result;
        }
    }
}
