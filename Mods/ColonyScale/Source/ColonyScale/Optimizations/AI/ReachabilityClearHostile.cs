using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ColonyScale.Optimizations.AI
{
    /// <summary>
    /// Every door open or close calls ReachabilityCache.ClearForHostile(door), which walks every cached reachability
    /// entry (thousands on a busy map) and calls pawn.HostileTo(door) for each one, although the entries belong to a
    /// few dozen pawns. This swaps that one call for a lookup that evaluates HostileTo once per pawn per call. The
    /// rest of the method is untouched: the same entries are collected into vanilla's list in enumeration order and
    /// removed in that order.
    ///
    /// Exact: within one ClearForHostile call the door is fixed and the loop does nothing but enumerate the dictionary,
    /// call HostileTo and append to a list, so HostileTo(pawn, door) gives the same answer every time if it is a pure
    /// function of game state. With b a door (never a Pawn) the vanilla call tree only reads state (Destroyed, Faction,
    /// comps, kindDef, lord job types, host faction, mind state flags, faction relations, overseer comp lazily cached);
    /// it draws no random numbers. The only effects skipped by not repeating it are a lazily cached comp lookup and
    /// Faction.RelationWith's error log for a missing faction relation (an already-corrupt game). Virtual calls that
    /// mods can override (MentalState.ForceHostileTo, Thing.Faction) are covered by memoizing only when the pawn, its
    /// mental state and the door are game types; anything else is evaluated per entry as vanilla does.
    ///
    /// Threading: door open/close runs on the main thread and vanilla's static entry list already rules out concurrent
    /// calls; the memo is only used on the main thread.
    /// </summary>
    public sealed class ReachabilityClearHostile : Optimization
    {
        public override string Id => "reachabilityClearHostile";
        public override string Label => "Faster reachability reset on doors";
        public override string Description =>
            "Opening or closing a door checks each pawn's hostility to the door once instead of once per cached path " +
            "check. Biggest effect with many pawns walking through doors.";
        public override string Category => "AI";

        private static readonly MethodInfo HostileTo =
            AccessTools.Method(typeof(GenHostility), nameof(GenHostility.HostileTo), new[] { typeof(Thing), typeof(Thing) });
        private static readonly MethodInfo HostileToMemoMethod = AccessTools.Method(typeof(ReachabilityClearHostile), nameof(HostileToMemo));

        private static readonly Assembly GameAssembly = typeof(Pawn).Assembly;

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(ReachabilityCache), nameof(ReachabilityCache.ClearForHostile)))
                .WithPrefix(typeof(ReachabilityClearHostile), nameof(Prefix))
                .WithPostfix(typeof(ReachabilityClearHostile), nameof(Postfix))
                .WithTranspiler(typeof(ReachabilityClearHostile), nameof(Transpiler));
        }

        // The memoized function and the non-trivial parts of its call tree for a pawn vs. a door.
        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            HostileTo,
            AccessTools.Method(typeof(GenHostility), "IsActivityDormant"),
            AccessTools.Method(typeof(GenHostility), nameof(GenHostility.HostileTo), new[] { typeof(Thing), typeof(Faction) }),
            AccessTools.Method(typeof(FactionUtility), nameof(FactionUtility.HostileTo), new[] { typeof(Faction), typeof(Faction) }),
            AccessTools.Method(typeof(Faction), nameof(Faction.RelationWith)),
            AccessTools.Method(typeof(PrisonBreakUtility), nameof(PrisonBreakUtility.IsPrisonBreaking)),
            AccessTools.Method(typeof(SlaveRebellionUtility), nameof(SlaveRebellionUtility.IsRebelling)),
            AccessTools.Method(typeof(MechanitorUtility), nameof(MechanitorUtility.IsPlayerOverseerSubject)),
        };

        public override void ClearCaches()
        {
            memo.Clear();
            memoFor = null;
        }

        private sealed class RefComparer : IEqualityComparer<Thing>
        {
            public static readonly RefComparer Instance = new RefComparer();
            public bool Equals(Thing a, Thing b) => ReferenceEquals(a, b);
            public int GetHashCode(Thing t) => RuntimeHelpers.GetHashCode(t);
        }

        private static readonly Dictionary<Thing, bool> memo = new Dictionary<Thing, bool>(RefComparer.Instance);

        // The door of the ClearForHostile call in progress on the main thread, or null. A nested call (none exists in
        // vanilla) resets it, after which the outer call evaluates every entry unmemoized. If HostileTo throws, the
        // postfix is skipped; the next call's prefix starts over, and HostileToMemo is only reachable from this method.
        private static Thing memoFor;

        public static void Prefix(Thing hostileTo)
        {
            memo.Clear();
            memoFor = hostileTo != null && UnityData.IsInMainThread && IsGameType(hostileTo) ? hostileTo : null;
        }

        public static void Postfix()
        {
            memo.Clear();
            memoFor = null;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.SwapCalls(instructions, "reachabilityClearHostile", HostileTo, HostileToMemoMethod, 1);

        public static bool HostileToMemo(Thing a, Thing b)
        {
            if (b == null || !ReferenceEquals(b, memoFor) || !Memoizable(a)) return GenHostility.HostileTo(a, b);
            if (memo.TryGetValue(a, out bool hostile))
            {
                if (Verify.Enabled) Verify.Record("reachabilityClearHostile", hostile == GenHostility.HostileTo(a, b));
                return hostile;
            }
            hostile = GenHostility.HostileTo(a, b);
            memo[a] = hostile;
            return hostile;
        }

        private static bool Memoizable(Thing a)
        {
            if (!(a is Pawn pawn) || !IsGameType(pawn)) return false;
            // Read without Pawn.MentalState's dereferences: deciding to memoize must not throw where vanilla would not.
            MentalState state = pawn.mindState?.mentalStateHandler?.CurState;
            return state == null || state.GetType().Assembly == GameAssembly;
        }

        private static bool IsGameType(Thing t) => t.GetType().Assembly == GameAssembly;
    }
}
