using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace ColonyScale.Optimizations.Pawns
{
    /// <summary>
    /// Pawn.Tick asks IsWorldPawn twice and Suspended once per tick, Pawn.TickInterval once each, and
    /// Pawn.ProcessPostTickVisuals (every frame, every spawned pawn) asks Suspended once; every answer is up
    /// to three HashSet lookups in WorldPawns (Suspended goes through WorldPawns.GetSituation). For a pawn on a map
    /// both answers are always false. Only these call sites in these three methods are swapped; Spawned is re-read at
    /// each site because a pawn can leave the map (and become a world pawn) in the middle of its own tick.
    ///
    /// Exact: a spawned pawn is never in WorldPawns. Pawns enter WorldPawns only through PassToWorld (AddPawn is
    /// private), which refuses spawned pawns; Notify_PawnDestroyed only moves pawns already there. Pawn.SpawnSetup
    /// removes the pawn right after Thing.SpawnSetup marks it spawned, and nothing ticks it in between: tick-list
    /// registration is deferred to the next TickList.Tick. So for a spawned pawn WorldPawns.Contains is false,
    /// IsWorldPawn is false, GetSituation is None, and Pawn.Suspended is false (Thing.Suspended returns false when
    /// spawned). Suspended is only short-circuited when the runtime type's override is Pawn's own; subclasses that
    /// override it get the virtual call. The skipped lookups have no side effects and use no randomness.
    /// </summary>
    public sealed class SpawnedPawnWorldChecks : Optimization
    {
        private const string OptId = "spawnedPawnWorldChecks";

        public override string Id => OptId;
        public override string Label => "Skip world-pawn checks for pawns on a map";
        public override string Description =>
            "Pawns on a map no longer look themselves up in the world pawn lists every tick; only pawns off the map can be world pawns.";
        public override string Category => "Pawns";

        // Free Performance's time dilation already rewrites pawn ticking.
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "brage.paralleltick" };

        private static readonly MethodInfo IsWorldPawnMethod = AccessTools.Method(typeof(WorldPawnsUtility), nameof(WorldPawnsUtility.IsWorldPawn));
        private static readonly MethodInfo ThingSuspended = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Suspended));
        private static readonly MethodInfo FastIsWorldPawnMethod = AccessTools.Method(typeof(SpawnedPawnWorldChecks), nameof(FastIsWorldPawn));
        private static readonly MethodInfo FastSuspendedMethod = AccessTools.Method(typeof(SpawnedPawnWorldChecks), nameof(FastSuspended));

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(Pawn), "Tick"))
                .WithTranspiler(typeof(SpawnedPawnWorldChecks), nameof(TickTranspiler));
            yield return PatchSpec.For(AccessTools.Method(typeof(Pawn), "TickInterval", new[] { typeof(int) }))
                .WithTranspiler(typeof(SpawnedPawnWorldChecks), nameof(TickIntervalTranspiler));
            yield return PatchSpec.For(AccessTools.Method(typeof(Pawn), nameof(Pawn.ProcessPostTickVisuals)))
                .WithTranspiler(typeof(SpawnedPawnWorldChecks), nameof(PostTickVisualsTranspiler));
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            IsWorldPawnMethod,
            AccessTools.Method(typeof(WorldPawns), nameof(WorldPawns.Contains)),
            AccessTools.Method(typeof(WorldPawns), nameof(WorldPawns.GetSituation)),
            AccessTools.Method(typeof(WorldPawns), nameof(WorldPawns.PassToWorld)),
            AccessTools.Method(typeof(WorldPawns), "AddPawn"),
            AccessTools.Method(typeof(WorldPawns), nameof(WorldPawns.Notify_PawnDestroyed)),
            AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Suspended)),
            ThingSuspended,
            AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Spawned)),
            AccessTools.Method(typeof(Pawn), nameof(Pawn.SpawnSetup)),
        };

        public override void ClearCaches()
        {
            lock (sync) overridesSuspended = new Dictionary<Type, bool>();
        }

        // Pawn.Tick: 2x IsWorldPawn, 1x Suspended. Pawn.TickInterval: 1x each.
        public static IEnumerable<CodeInstruction> TickTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.SwapCalls(Transpilers.SwapCalls(instructions, OptId, IsWorldPawnMethod, FastIsWorldPawnMethod, 2),
                OptId, ThingSuspended, FastSuspendedMethod, 1);

        public static IEnumerable<CodeInstruction> TickIntervalTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.SwapCalls(Transpilers.SwapCalls(instructions, OptId, IsWorldPawnMethod, FastIsWorldPawnMethod, 1),
                OptId, ThingSuspended, FastSuspendedMethod, 1);

        // Pawn.ProcessPostTickVisuals: 1x Suspended (main thread, from Map.MapUpdate).
        public static IEnumerable<CodeInstruction> PostTickVisualsTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.SwapCalls(instructions, OptId, ThingSuspended, FastSuspendedMethod, 1);

        public static bool FastIsWorldPawn(Pawn p)
        {
            if (p.Spawned)
            {
                if (Verify.Enabled) Verify.Record(OptId, !WorldPawnsUtility.IsWorldPawn(p));
                return false;
            }
            return WorldPawnsUtility.IsWorldPawn(p);
        }

        public static bool FastSuspended(Pawn p)
        {
            if (p.Spawned && !OverridesSuspended(p.GetType()))
            {
                if (Verify.Enabled) Verify.Record(OptId, !p.Suspended);
                return false;
            }
            return p.Suspended; // virtual, as the replaced callvirt
        }

        private static readonly object sync = new object();
        // Copy-on-write: readers never see a dictionary that is being written (pawns may tick off the main thread
        // under other mods).
        private static Dictionary<Type, bool> overridesSuspended = new Dictionary<Type, bool>();

        /// <summary>True if <paramref name="type"/> (a Pawn subclass) overrides Thing.Suspended below Pawn.</summary>
        private static bool OverridesSuspended(Type type)
        {
            if (type == typeof(Pawn)) return false;
            var cache = overridesSuspended;
            if (cache.TryGetValue(type, out bool result)) return result;
            result = false;
            for (Type t = type; t != null && t != typeof(Pawn); t = t.BaseType)
            {
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != ThingSuspended.Name || m.GetParameters().Length != 0) continue;
                    var baseDef = m.GetBaseDefinition();
                    if (baseDef.DeclaringType == ThingSuspended.DeclaringType && baseDef.Name == ThingSuspended.Name) result = true;
                }
            }
            lock (sync)
            {
                var copy = new Dictionary<Type, bool>(overridesSuspended) { [type] = result };
                overridesSuspended = copy;
            }
            return result;
        }
    }
}
