using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale.Optimizations.Pawns
{
    /// <summary>
    /// Pawn.Tick deselects a spawned pawn that became hidden from the player:
    ///   if (Spawned &amp;&amp; this.IsHiddenFromPlayer() &amp;&amp; Find.Selector.IsSelected(this)) Find.Selector.Deselect(this);
    /// IsHiddenFromPlayer runs every tick for every spawned pawn and, for non-player pawns, scans all hediffs with a
    /// generic TryGetComp. This swaps that call for one that first asks the selector: with nothing selected the
    /// condition is false whatever IsHiddenFromPlayer says; with a small selection, a pawn that is not selected also
    /// makes it false. Otherwise IsHiddenFromPlayer is evaluated as before, and the vanilla IsSelected check follows.
    ///
    /// Exact: the replacement returns false only when IsSelected(this) is false, so the whole condition is false in
    /// vanilla as well; when it returns true, IsHiddenFromPlayer was true and vanilla's IsSelected decides as before.
    /// IsHiddenFromPlayer and IsSelected are pure (field reads, an idempotent comp-lookup cache in
    /// HediffComp_Invisibility, Equals over the selection), consume no Rand, and dropping or repeating them changes
    /// nothing. Two things vanilla does on the way are kept: the shortcut is only taken when the play UI and its
    /// selector exist (vanilla reads Find.Selector only after IsHiddenFromPlayer returned true, so it must not start
    /// throwing here), and when the player faction resolves (otherwise Faction.OfPlayer inside IsHiddenFromPlayer
    /// logs an error, which must still happen). Runs on the main thread only (Pawn.Tick).
    /// Residual assumption: IsHiddenFromPlayer does not throw for a pawn that is not selected; it only could for a
    /// pawn with an invisibility hediff but no mindState, which the game never creates.
    /// </summary>
    public sealed class HiddenPawnDeselect : Optimization
    {
        public override string Id => "hiddenDeselect";
        public override string Label => "Cheaper hidden-pawn deselect check";
        public override string Description =>
            "The per-tick check that deselects pawns which turned invisible only looks at pawns that could be selected.";
        public override string Category => "Pawns";
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "brage.paralleltick" };

        private const int SmallSelection = 4;

        private static readonly MethodInfo IsHidden = AccessTools.Method(typeof(InvisibilityUtility), nameof(InvisibilityUtility.IsHiddenFromPlayer));

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(Pawn), "Tick"))
                .WithTranspiler(typeof(HiddenPawnDeselect), nameof(Transpiler));
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            IsHidden,
            AccessTools.Method(typeof(Selector), nameof(Selector.IsSelected)),
            AccessTools.PropertyGetter(typeof(Selector), nameof(Selector.NumSelected)),
            AccessTools.PropertyGetter(typeof(Find), nameof(Find.Selector)),
            AccessTools.PropertyGetter(typeof(Find), nameof(Find.MapUI)),
            AccessTools.PropertyGetter(typeof(Find), nameof(Find.UIRoot)),
            AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayer)),
            AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayerSilentFail)),
            AccessTools.PropertyGetter(typeof(HediffComp_Invisibility), nameof(HediffComp_Invisibility.PsychologicallyVisible)),
            AccessTools.PropertyGetter(typeof(HediffComp_Invisibility), "ForcedVisible"),
        };

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.SwapCalls(instructions, "hiddenDeselect", IsHidden, AccessTools.Method(typeof(HiddenPawnDeselect), nameof(HiddenAndMaybeSelected)), 1);

        /// <summary>False only when the pawn is certainly not selected; otherwise IsHiddenFromPlayer().</summary>
        public static bool HiddenAndMaybeSelected(Pawn pawn)
        {
            Selector selector = (Find.UIRoot as UIRoot_Play)?.mapUI?.selector;
            if (selector == null || Faction.OfPlayerSilentFail == null) return pawn.IsHiddenFromPlayer();
            int selected = selector.NumSelected;
            bool notSelected = selected == 0 || (selected <= SmallSelection && !selector.IsSelected(pawn));
            if (Verify.Enabled)
            {
                bool hidden = pawn.IsHiddenFromPlayer();
                Verify.Record("hiddenDeselect", !notSelected || !(hidden && selector.IsSelected(pawn)));
                return hidden;
            }
            return !notSelected && pawn.IsHiddenFromPlayer();
        }
    }
}
