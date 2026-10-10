using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale.Optimizations.Map
{
    /// <summary>
    /// Every wastepack stack ticks CompDissolution.CompTick, which (when not frozen) evaluates
    /// `dissolveTicks >= DissolutionIntervalTicks` each tick; the interval does two room lookups (IsOutdoors, once
    /// directly and once through IsBeingRainedOn) plus room roof/size checks. This swaps the interval read for a
    /// method that returns int.MaxValue (comparison false) while dissolveTicks is below a lower bound of every value
    /// the interval can take, provided that evaluating the interval right now would have no side effects; otherwise it
    /// returns the real DissolutionIntervalTicks. IsFrozen, InAtomizer and the increment are untouched.
    ///
    /// Exact:
    /// - Bound: the interval is (int)x where x is float D=60000*days (int multiply, as vanilla) optionally divided by
    ///   the indoor factor and/or the rain factor: four cases, all considered regardless of room state. Each case's
    ///   exact quotient q is computed in double; skipping is allowed only if every q is within [1, 2e9] (so vanilla's
    ///   int conversion is a plain truncation, never Inf/NaN/overflow). Float evaluation (single or extended
    ///   precision) has at most ~5 roundings of 2^-24 relative error, so x >= q(1-1e-6) and (int)x >= L+1 with
    ///   L = floor(min q * (1-1e-6)) - 1. While dissolveTicks &lt; L, vanilla's comparison is false. The bound is cached
    ///   per props instance and its three values. The returned int.MaxValue only feeds that comparison (checked in
    ///   the transpiler) and dissolveTicks &lt; L &lt; int.MaxValue.
    /// - Side effects of the interval: GetRoom goes through RegionGrid.GetValidRegionAt, which can rebuild dirty
    ///   regions (or log a warning when the updater is disabled), and Room.UsesOutdoorTemperature fills the room's
    ///   cached open-roof and cell counts. Skipping only happens when none of these would occur: no map held, cell
    ///   out of bounds, or nothing to rebuild (then GetValidRegionAt equals GetValidRegionAt_NoRebuild) and the room is
    ///   null, touches the map edge (returns before the caches) or has both caches filled. The weather read used for
    ///   rain must also be non-null. Rand is not used by the interval.
    /// Main thread only.
    /// </summary>
    public sealed class WastepackDissolution : Optimization
    {
        public override string Id => "wastepackDissolution";
        public override string Label => "Cheaper wastepack ticking";
        public override string Description =>
            "Wastepacks only work out their dissolution interval (indoor/outdoor and rain checks) once their dissolve " +
            "timer is close enough to it to matter.";
        public override string Category => "Map";

        private static readonly MethodInfo IntervalGetter = AccessTools.PropertyGetter(typeof(CompDissolution), nameof(CompDissolution.DissolutionIntervalTicks));
        private static readonly MethodInfo FastInterval = AccessTools.Method(typeof(WastepackDissolution), nameof(IntervalTicks));
        private static readonly AccessTools.FieldRef<CompDissolution, int> dissolveTicks = AccessTools.FieldRefAccess<CompDissolution, int>("dissolveTicks");
        private static readonly AccessTools.FieldRef<Room, int> roomOpenRoofCount = AccessTools.FieldRefAccess<Room, int>("cachedOpenRoofCount");
        private static readonly AccessTools.FieldRef<Room, int> roomCellCount = AccessTools.FieldRefAccess<Room, int>("cachedCellCount");

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(CompDissolution), nameof(CompDissolution.CompTick)))
                .WithTranspiler(typeof(WastepackDissolution), nameof(Transpiler));
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            // What the interval computes and which side effects it can have.
            IntervalGetter,
            AccessTools.PropertyGetter(typeof(CompDissolution), nameof(CompDissolution.DefaultTicksUntilDissolution)),
            AccessTools.PropertyGetter(typeof(CompDissolution), "Props"),
            AccessTools.PropertyGetter(typeof(CompDissolution), nameof(CompDissolution.IsOutdoors)),
            AccessTools.PropertyGetter(typeof(CompDissolution), nameof(CompDissolution.IsBeingRainedOn)),
            AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.GetRoom), new[] { typeof(IntVec3), typeof(Verse.Map) }),
            AccessTools.Method(typeof(RegionAndRoomQuery), nameof(RegionAndRoomQuery.RoomAt)),
            AccessTools.Method(typeof(RegionAndRoomQuery), nameof(RegionAndRoomQuery.DistrictAt)),
            AccessTools.Method(typeof(RegionAndRoomQuery), nameof(RegionAndRoomQuery.RegionAt)),
            AccessTools.Method(typeof(RegionGrid), nameof(RegionGrid.GetValidRegionAt)),
            AccessTools.Method(typeof(RegionGrid), nameof(RegionGrid.GetValidRegionAt_NoRebuild)),
            AccessTools.Method(typeof(RegionAndRoomUpdater), nameof(RegionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms)),
            AccessTools.PropertyGetter(typeof(RegionAndRoomUpdater), nameof(RegionAndRoomUpdater.AnythingToRebuild)),
            AccessTools.PropertyGetter(typeof(Room), nameof(Room.UsesOutdoorTemperature)),
            AccessTools.PropertyGetter(typeof(Room), nameof(Room.TouchesMapEdge)),
            AccessTools.PropertyGetter(typeof(Room), nameof(Room.OpenRoofCount)),
            AccessTools.PropertyGetter(typeof(Room), nameof(Room.CellCount)),
        };

        /// <summary>Swaps the single interval read; it must feed `dissolveTicks >= interval` directly (blt).</summary>
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            int found = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].Calls(IntervalGetter)) continue;
                found++;
                OpCode next = i + 1 < list.Count ? list[i + 1].opcode : OpCodes.Nop;
                if (next != OpCodes.Blt && next != OpCodes.Blt_S)
                    throw new PatternNotFoundException("wastepackDissolution", "DissolutionIntervalTicks result not compared with blt");
                list[i].opcode = OpCodes.Call;
                list[i].operand = FastInterval;
            }
            if (found != 1)
                throw new PatternNotFoundException("wastepackDissolution", $"expected 1 call to DissolutionIntervalTicks, found {found}");
            return list;
        }

        public override void ClearCaches()
        {
            boundProps = null;
        }

        // ── Lower bound ─────────────────────────────────────

        private static CompProperties_Dissolution boundProps;
        private static int boundDays;
        private static float boundIndoors, boundRain;
        private static int bound;

        private static int LowerBound(CompDissolution comp)
        {
            var props = (CompProperties_Dissolution)comp.props; // vanilla's Props cast
            int days = props.dissolutionAfterDays;
            float indoors = props.dissolutinFactorIndoors;
            float rain = props.dissolutionFactorRain;
            if (ReferenceEquals(props, boundProps) && days == boundDays && indoors.Equals(boundIndoors) && rain.Equals(boundRain))
                return bound;
            boundProps = props;
            boundDays = days;
            boundIndoors = indoors;
            boundRain = rain;
            bound = ComputeLowerBound(days, indoors, rain);
            return bound;
        }

        /// <summary>A value strictly below every result DissolutionIntervalTicks can return for these props, or int.MinValue.</summary>
        public static int ComputeLowerBound(int days, float indoors, float rain)
        {
            double d = unchecked(60000 * days);
            double a = indoors, b = rain;
            double c0 = d, c1 = d / a, c2 = d / b, c3 = d / a / b;
            double min = Math.Min(Math.Min(c0, c1), Math.Min(c2, c3));
            double max = Math.Max(Math.Max(c0, c1), Math.Max(c2, c3));
            if (!(min >= 1.0) || !(max <= 2.0e9)) return int.MinValue; // also rejects NaN and infinities
            return (int)Math.Floor(min * (1.0 - 1e-6)) - 1;
        }

        // ── Side-effect check ───────────────────────────────

        /// <summary>True when evaluating DissolutionIntervalTicks now would not rebuild regions, log, or fill room caches.</summary>
        private static bool IntervalHasNoSideEffects(CompDissolution comp)
        {
            Thing parent = comp.parent;
            Verse.Map map = parent.MapHeld;
            if (map == null) return true; // IsOutdoors returns false before any lookup
            if (map.weatherManager?.curWeather == null) return false; // vanilla would throw if raining is checked
            IntVec3 pos = parent.Position;
            if (!pos.InBounds(map)) return true; // RegionAt returns null before touching the region grid
            if (map.regionAndRoomUpdater.AnythingToRebuild) return false;
            Region region = map.regionGrid.GetValidRegionAt_NoRebuild(pos);
            if (region == null || (region.type & RegionType.Set_All) == RegionType.None) return true;
            Room room = region.District?.Room;
            if (room == null || room.TouchesMapEdge) return true;
            return roomOpenRoofCount(room) != -1 && roomCellCount(room) != -1;
        }

        public static int IntervalTicks(CompDissolution comp)
        {
            int ticks = dissolveTicks(comp);
            if (ticks < LowerBound(comp) && IntervalHasNoSideEffects(comp))
            {
                if (Verify.Enabled) Verify.Record("wastepackDissolution", ticks < comp.DissolutionIntervalTicks);
                return int.MaxValue;
            }
            return comp.DissolutionIntervalTicks;
        }
    }
}
