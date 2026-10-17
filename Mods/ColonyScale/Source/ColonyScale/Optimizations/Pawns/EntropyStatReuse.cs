using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale.Optimizations.Pawns
{
    /// <summary>
    /// Royalty/Biotech neural heat. Pawn_PsychicEntropyTracker.MaxEntropy is a full, uncached stat calculation
    /// (PsychicEntropyMax: gear offsets, times the PsychicSensitivity stat), and MaxPotentialEntropy is
    /// Mathf.Max(that same stat, MaxEntropy). EntropyToRelativeValue reads MaxEntropy three times below the limit and
    /// MaxEntropy plus MaxPotentialEntropy twice each above it (up to six stat calculations), and runs for every pawn with
    /// neural heat on every tick (HediffComp_SeverityFromEntropy.CompPostTick and CompShouldRemove). The Severity getter
    /// re-reads EntropyRelativeValue once per threshold it walks past (every TickInterval of a heated pawn, and around
    /// every psycast). This reads the stat once per call of each and reuses it.
    /// Inspired by Performance Plus by axen-o7 (Steam Workshop 3821145631, "psychic entropy reuse"); extended here to
    /// the MaxPotentialEntropy reads.
    ///
    /// Exact, given that a stat read returns the same value when nothing changes between two reads: within
    /// EntropyToRelativeValue the first MaxEntropy read (the comparison after the val &lt; epsilon early return) comes
    /// before every other read on every path, and between the reads only float compares and arithmetic run, so every
    /// later MaxEntropy read would return the cached value, and MaxPotentialEntropy = Max(v, v) = v (NaN included; on
    /// 1.7 Mathf.Max is MathF.Max). Within Severity only the threshold dictionary is read between iterations, and the
    /// first EntropyRelativeValue is still read lazily inside the loop, as vanilla does. StatWorker.GetValue with no
    /// cache window writes only its temporary cache entry (same value, same tick on every read), so dropping repeated
    /// reads leaves the same state; the stat parts involved draw no Rand. The getters are in DependsOn (fingerprinted,
    /// foreign patches turn this off); StatWorker.GetValue itself is not, as Missile Girl transpiles it (a stat cache
    /// keeps the reads equal too). Verify mode recomputes vanilla's expression with fresh reads and compares.
    /// </summary>
    public sealed class EntropyStatReuse : Optimization
    {
        private const string OptId = "entropyStatReuse";

        public override string Id => OptId;
        public override string Label => "Fewer neural heat stat calculations";
        public override string Description =>
            "Pawns with neural heat calculate their heat limit once per check instead of up to six times, every tick.";
        public override string Category => "Pawns";

        // Performance Plus transpiles the same two methods and counts the reads it rewrites.
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "axeno7.performanceplus" };

        private static readonly MethodInfo MaxEntropyGetter = AccessTools.PropertyGetter(typeof(Pawn_PsychicEntropyTracker), nameof(Pawn_PsychicEntropyTracker.MaxEntropy));
        private static readonly MethodInfo MaxPotentialGetter = AccessTools.PropertyGetter(typeof(Pawn_PsychicEntropyTracker), nameof(Pawn_PsychicEntropyTracker.MaxPotentialEntropy));
        private static readonly MethodInfo RelativeGetter = AccessTools.PropertyGetter(typeof(Pawn_PsychicEntropyTracker), nameof(Pawn_PsychicEntropyTracker.EntropyRelativeValue));
        private static readonly MethodInfo ToRelative = AccessTools.Method(typeof(Pawn_PsychicEntropyTracker), nameof(Pawn_PsychicEntropyTracker.EntropyToRelativeValue));

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(ToRelative)
                .WithTranspiler(typeof(EntropyStatReuse), nameof(ToRelativeTranspiler))
                .WithPostfix(typeof(EntropyStatReuse), nameof(ToRelativePostfix));
            yield return PatchSpec.For(AccessTools.PropertyGetter(typeof(Pawn_PsychicEntropyTracker), nameof(Pawn_PsychicEntropyTracker.Severity)))
                .WithTranspiler(typeof(EntropyStatReuse), nameof(SeverityTranspiler));
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[] { MaxEntropyGetter, MaxPotentialGetter, RelativeGetter };

        /// <summary>
        /// The first MaxEntropy read keeps its value in a local (call; dup; stloc); every later MaxEntropy or
        /// MaxPotentialEntropy read becomes pop (the tracker) + ldloc.
        /// </summary>
        public static IEnumerable<CodeInstruction> ToRelativeTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var list = instructions.ToList();
            LocalBuilder max = il.DeclareLocal(typeof(float));
            var output = new List<CodeInstruction>();
            int firsts = 0, reused = 0;
            foreach (var ins in list)
            {
                bool isMax = ins.Calls(MaxEntropyGetter), isPotential = ins.Calls(MaxPotentialGetter);
                if (!isMax && !isPotential) { output.Add(ins); continue; }
                if (isMax && firsts == 0 && reused == 0)
                {
                    firsts++;
                    output.Add(ins);
                    output.Add(new CodeInstruction(OpCodes.Dup));
                    output.Add(new CodeInstruction(OpCodes.Stloc, max));
                    continue;
                }
                reused++;
                output.Add(new CodeInstruction(OpCodes.Pop).MoveLabelsFrom(ins).MoveBlocksFrom(ins));
                output.Add(new CodeInstruction(OpCodes.Ldloc, max));
            }
            // 1.7.4980: MaxEntropy at IL_0010 (first), _0018, _002c, _004d; MaxPotentialEntropy at _0034, _0054.
            if (firsts != 1 || reused != 5)
                throw new PatternNotFoundException(OptId, $"expected 1 + 5 heat limit reads in EntropyToRelativeValue, found {firsts} + {reused}");
            return output;
        }

        /// <summary>
        /// The EntropyRelativeValue read in the threshold loop goes through <see cref="RelativeOnce"/> with two new locals
        /// (has-value flag, value), cleared at the method start.
        /// </summary>
        public static IEnumerable<CodeInstruction> SeverityTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            LocalBuilder has = il.DeclareLocal(typeof(bool));
            LocalBuilder value = il.DeclareLocal(typeof(float));
            var output = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldc_I4_0),
                new CodeInstruction(OpCodes.Stloc, has),
                new CodeInstruction(OpCodes.Ldc_R4, 0f),
                new CodeInstruction(OpCodes.Stloc, value),
            };
            int swapped = 0;
            foreach (var ins in instructions)
            {
                if (ins.Calls(RelativeGetter))
                {
                    swapped++;
                    output.Add(new CodeInstruction(OpCodes.Ldloca, has).MoveLabelsFrom(ins).MoveBlocksFrom(ins));
                    output.Add(new CodeInstruction(OpCodes.Ldloca, value));
                    output.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EntropyStatReuse), nameof(RelativeOnce))));
                    continue;
                }
                output.Add(ins);
            }
            if (swapped != 1) throw new PatternNotFoundException(OptId, $"expected 1 EntropyRelativeValue read in Severity, found {swapped}");
            return output;
        }

        public static float RelativeOnce(Pawn_PsychicEntropyTracker tracker, ref bool has, ref float value)
        {
            if (!has)
            {
                value = tracker.EntropyRelativeValue;
                has = true;
            }
            else if (Verify.Enabled)
            {
                Verify.Record(OptId, SameFloat(value, tracker.EntropyRelativeValue));
            }
            return value;
        }

        /// <summary>Verify mode: vanilla's expression with a fresh read at every place vanilla reads.</summary>
        public static void ToRelativePostfix(Pawn_PsychicEntropyTracker __instance, float val, float __result)
        {
            if (!Verify.Enabled) return;
            float expected;
            if (val < float.Epsilon) expected = 0f;
            else if (val < __instance.MaxEntropy) expected = !(__instance.MaxEntropy > float.Epsilon) ? 0f : val / __instance.MaxEntropy;
            else if (!(__instance.MaxPotentialEntropy > float.Epsilon)) expected = 0f;
            else expected = 1f + (val - __instance.MaxEntropy) / __instance.MaxPotentialEntropy;
            Verify.Record(OptId, SameFloat(expected, __result));
        }

        private static bool SameFloat(float a, float b) => a.Equals(b); // equal values, NaN equal to NaN
    }
}
