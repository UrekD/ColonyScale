using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ColonyScale.Optimizations.World
{
    /// <summary>
    /// Once per in-game day WorldPathGrid recalculates every tile's perceived movement difficulty. Per tile the
    /// winter offset computes the tile's longitude/latitude (tile center + atan2/asin) and the seasonal
    /// temperature amplitude (two more LongLatOf calls and DistanceFromEquatorNormalized), each going through
    /// planet layer lookups: a 50-150 ms hitch on large worlds. Those values depend only on the layer's tile
    /// geometry, so they are cached per layer, filled by calling the same vanilla functions, and the day's
    /// recalculation runs a copy of the vanilla loop that uses them.
    ///
    /// Exact: cached floats are the values the vanilla functions return. Tile geometry (verts, radius) only
    /// changes in PlanetLayer.InitializeLayer, which drops the layer's cache. Everything time- or
    /// tile-state-dependent (biome, hilliness, temperature, season curves) is still read per tile through the
    /// vanilla accessors, and the copied arithmetic mirrors CalculatedMovementDifficultyAt,
    /// GetCurrentWinterMovementDifficultyOffset, GetTemperatureFromSeasonAtTile and OffsetFromSeasonCycle with
    /// the same operations and the same method boundaries (rounding points). The season-cycle cosine depends
    /// only on absTick, so it is evaluated once per recalculation. Verify mode compares every tile against
    /// vanilla's CalculatedMovementDifficultyAt and stores vanilla's value.
    /// </summary>
    public sealed class WorldPathGridDaily : Optimization
    {
        public override string Id => "worldPathGridDaily";
        public override string Label => "Faster daily world path costs";
        public override string Description =>
            "The once-a-day recalculation of world travel costs (winter slowdown) reuses each tile's position on the planet " +
            "instead of recomputing it, removing a stutter at midnight on large worlds.";
        public override string Category => "World";

        private static readonly AccessTools.FieldRef<WorldPathGrid, Dictionary<PlanetLayer, int>> recalculatedDayOfYear =
            AccessTools.FieldRefAccess<WorldPathGrid, Dictionary<PlanetLayer, int>>("layerAllPathCostsRecalculatedDayOfYear");

        private static readonly MethodInfo HillinessOffsetMethod = AccessTools.Method(typeof(WorldPathGrid), "HillinessMovementDifficultyOffset");
        private static readonly Func<Hilliness, float> HillinessOffset = AccessTools.MethodDelegate<Func<Hilliness, float>>(HillinessOffsetMethod);

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(WorldPathGrid), nameof(WorldPathGrid.RecalculateLayerPerceivedPathCosts),
                    new[] { typeof(PlanetLayer), typeof(int?) }))
                .WithPrefix(typeof(WorldPathGridDaily), nameof(Prefix), replacesOriginal: true, priority: Priority.Last);
            yield return PatchSpec.For(AccessTools.Method(typeof(PlanetLayer), nameof(PlanetLayer.InitializeLayer)))
                .WithPostfix(typeof(WorldPathGridDaily), nameof(InitializeLayerPostfix));
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            // Reproduced by the copied loop.
            AccessTools.Method(typeof(WorldPathGrid), nameof(WorldPathGrid.RecalculatePerceivedMovementDifficultyAt)),
            AccessTools.Method(typeof(WorldPathGrid), nameof(WorldPathGrid.PassableFast)),
            AccessTools.Method(typeof(WorldPathGrid), nameof(WorldPathGrid.CalculatedMovementDifficultyAt)),
            AccessTools.Method(typeof(WorldPathGrid), nameof(WorldPathGrid.GetCurrentWinterMovementDifficultyOffset)),
            AccessTools.Method(typeof(GenTemperature), nameof(GenTemperature.GetTemperatureFromSeasonAtTile)),
            AccessTools.Method(typeof(GenTemperature), nameof(GenTemperature.OffsetFromSeasonCycle)),
            HillinessOffsetMethod,
            // Cached: pure functions of the layer's tile geometry.
            AccessTools.Method(typeof(GenTemperature), nameof(GenTemperature.SeasonalShiftAmplitudeAt)),
            AccessTools.Method(typeof(WorldGrid), nameof(WorldGrid.LongLatOf), new[] { typeof(PlanetTile) }),
            AccessTools.Method(typeof(WorldGrid), nameof(WorldGrid.DistanceFromEquatorNormalized), new[] { typeof(PlanetTile) }),
            AccessTools.Method(typeof(PlanetLayer), nameof(PlanetLayer.LongLatOf), new[] { typeof(PlanetTile) }),
            AccessTools.Method(typeof(PlanetLayer), nameof(PlanetLayer.LongLatOf), new[] { typeof(int) }),
            AccessTools.Method(typeof(PlanetLayer), nameof(PlanetLayer.DistanceFromEquatorNormalized), new[] { typeof(PlanetTile) }),
            AccessTools.Method(typeof(PlanetLayer), nameof(PlanetLayer.DistanceFromEquatorNormalized), new[] { typeof(int) }),
            AccessTools.Method(typeof(PlanetLayer), nameof(PlanetLayer.GetTileCenter), new[] { typeof(int) }),
            // Assumed stable for the duration of one recalculation.
            AccessTools.PropertyGetter(typeof(PlanetTile), nameof(PlanetTile.Layer)),
            AccessTools.PropertyGetter(typeof(GenTicks), nameof(GenTicks.TicksAbs)),
        };

        public override void ClearCaches() => geometry = new ConditionalWeakTable<PlanetLayer, Geometry>();

        /// <summary>Per tile of one layer: vanilla LongLatOf and SeasonalShiftAmplitudeAt.</summary>
        private sealed class Geometry
        {
            public Vector2[] longLat;
            public float[] amplitude;
        }

        // Weak keys: layers of a previous game are collected with it. Thread-safe (world generation runs off the main thread).
        private static ConditionalWeakTable<PlanetLayer, Geometry> geometry = new ConditionalWeakTable<PlanetLayer, Geometry>();

        public static void InitializeLayerPostfix(PlanetLayer __instance) => geometry.Remove(__instance);

        private static Geometry GeometryFor(PlanetLayer layer)
        {
            if (geometry.TryGetValue(layer, out var geo) && geo.longLat.Length == layer.TilesCount) return geo;
            int n = layer.TilesCount;
            geo = new Geometry { longLat = new Vector2[n], amplitude = new float[n] };
            WorldGrid grid = Find.WorldGrid;
            for (int i = 0; i < n; i++)
            {
                PlanetTile tile = new PlanetTile(i, layer);
                geo.longLat[i] = grid.LongLatOf(tile);
                geo.amplitude[i] = GenTemperature.SeasonalShiftAmplitudeAt(tile);
            }
            geometry.Remove(layer);
            geometry.Add(layer, geo);
            return geo;
        }

        public static bool Prefix(WorldPathGrid __instance, PlanetLayer layer, int? ticksAbs)
        {
            bool flag = false;
            recalculatedDayOfYear(__instance)[layer] = -1;
            float[] array = __instance.layerMovementDifficulty[layer];
            WorldGrid grid = Find.WorldGrid;
            int now = ticksAbs ?? GenTicks.TicksAbs;
            float seasonCos = SeasonCos(now == 0 ? 1 : now);
            bool verify = Verify.Enabled;
            bool matched = true;
            float[] difficulty = null;
            Geometry geo = null;
            for (int i = 0; i < array.Length; i++)
            {
                // RecalculatePerceivedMovementDifficultyAt
                PlanetTile tile = new PlanetTile(i, layer);
                if (!grid.InBounds(tile)) continue;
                if (difficulty == null)
                {
                    PlanetLayer resolved = tile.Layer;
                    difficulty = __instance.layerMovementDifficulty[resolved];
                    geo = GeometryFor(resolved);
                }
                bool passable = difficulty[i] < 1000f;
                float value = MovementDifficulty(grid, tile, now, geo.longLat[i], geo.amplitude[i], seasonCos);
                if (verify)
                {
                    float vanilla = WorldPathGrid.CalculatedMovementDifficultyAt(tile, perceivedStatic: true, ticksAbs);
                    matched &= value.Equals(vanilla);
                    value = vanilla;
                }
                difficulty[i] = value;
                flag = flag || passable != difficulty[i] < 1000f;
            }
            if (verify) Verify.Record("worldPathGridDaily", matched);
            if (flag)
            {
                Find.WorldReachability.ClearCache();
            }
            return false;
        }

        /// <summary>CalculatedMovementDifficultyAt without explanation.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float MovementDifficulty(WorldGrid grid, PlanetTile tile, int ticksAbs, Vector2 longLat, float amplitude, float seasonCos)
        {
            Tile tile2 = grid[tile];
            if (tile2.PrimaryBiome.impassable || tile2.hilliness == Hilliness.Impassable)
            {
                return 1000f;
            }
            // One expression, as in vanilla's IL: the partial sums stay on the evaluation stack.
            return 0f + tile2.PrimaryBiome.movementDifficulty + HillinessOffset(tile2.hilliness)
                + WinterOffset(grid, tile, ticksAbs, longLat, amplitude, seasonCos);
        }

        /// <summary>GetCurrentWinterMovementDifficultyOffset without explanation, LongLatOf cached.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float WinterOffset(WorldGrid grid, PlanetTile tile, int ticksAbs, Vector2 vector, float amplitude, float seasonCos)
        {
            SeasonUtility.GetSeason(GenDate.YearPercent(ticksAbs, vector.x), vector.y, out _, out _, out _, out float winter, out _, out float permanentWinter);
            float num = winter + permanentWinter;
            num *= Mathf.InverseLerp(5f, 0f, TemperatureFromSeason(grid, ticksAbs, tile, amplitude, seasonCos));
            if (num > 0.01f)
            {
                return 2f * num;
            }
            return 0f;
        }

        /// <summary>GenTemperature.GetTemperatureFromSeasonAtTile.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float TemperatureFromSeason(WorldGrid grid, int absTick, PlanetTile tile, float amplitude, float seasonCos)
        {
            Tile tile2 = grid[tile];
            if (tile2 == null)
            {
                return 21f;
            }
            return tile2.temperature + SeasonOffset(seasonCos, amplitude);
        }

        /// <summary>GenTemperature.OffsetFromSeasonCycle with SeasonalShiftAmplitudeAt cached and the cosine hoisted.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float SeasonOffset(float seasonCos, float amplitude)
        {
            return seasonCos * -amplitude;
        }

        /// <summary>The cosine factor of GenTemperature.OffsetFromSeasonCycle; absTick already adjusted from 0 to 1.</summary>
        private static float SeasonCos(int absTick)
        {
            float num = (float)absTick / 60000f % 60f / 60f;
            return Mathf.Cos(Mathf.PI * 2f * (num - Season.Winter.GetMiddleTwelfth(0f).GetBeginningYearPct()));
        }
    }
}
