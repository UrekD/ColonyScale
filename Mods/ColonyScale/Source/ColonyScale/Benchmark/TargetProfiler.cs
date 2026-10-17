using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Verse;

namespace ColonyScale
{
    /// <summary>
    /// Profile runs of the A/B benchmark (`profile=true`): times every patched target of every optimization under test,
    /// with the optimizations off and on, so the report shows what each one saves on the running game, not only the
    /// total. A probe (first prefix + finalizer, own Harmony id) stays on each target in both modes, so off measures
    /// vanilla and on measures the optimized code with the same probe cost.
    /// Main thread only. Exclusive time: a profiled target called inside another one is subtracted from the outer one,
    /// so nested targets of different optimizations are not counted twice. Probe cost is calibrated and subtracted:
    /// the part inside a call's own timed span per call, and the part a nested probed call adds to its caller per child
    /// call (an optimization that stops calling a probed target would otherwise be credited with the probe cost).
    /// Calls inside TickManager.DoSingleTick and calls elsewhere in the block's frames (drawing, UI) are kept apart.
    /// </summary>
    public static class TargetProfiler
    {
        public const string HarmonyId = PatchManager.ProfileHarmonyId;

        private const int Tick = 0, Frame = 1;

        private sealed class Slot
        {
            public MethodBase Method;
            public int Opt;
            public string SharedWith;
            // [where (Tick/Frame), mode (off/on)]
            public readonly long[,] Calls = new long[2, 2];
            public readonly long[,] Exclusive = new long[2, 2];
            public readonly long[,] ChildCalls = new long[2, 2];
        }

        private struct Span
        {
            public long Start;
            public long Child;
            public long ChildCalls;
        }

        private static Harmony harmony;
        private static readonly Dictionary<MethodBase, int> slotOf = new Dictionary<MethodBase, int>();
        private static readonly List<Slot> slots = new List<Slot>();
        private static List<Optimization> opts = new List<Optimization>();
        private static Span[] stack = new Span[64];
        private static int depth;
        private static int mode = -1; // -1: not recording; 0 off; 1 on
        private static int where = Frame;
        private static readonly long[] ticks = new long[2];
        private static double overheadTicks;       // Stopwatch ticks the probe adds inside a call's own span
        private static double nestedOverheadTicks; // Stopwatch ticks a probed child call adds to its caller's span
        private static string calibrationProblem;
        private static readonly List<string> skipped = new List<string>();

        public static bool Installed => harmony != null;

        /// <summary>Probes the non-hook targets of the given optimizations (they must be active, so their specs resolve).</summary>
        public static void Install(List<Optimization> underTest)
        {
            harmony = new Harmony(HarmonyId);
            opts = underTest;
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(TargetProfiler), nameof(Prefix))) { priority = Priority.First + 1000 };
            var finalizer = new HarmonyMethod(AccessTools.Method(typeof(TargetProfiler), nameof(Finalizer)));
            for (int i = 0; i < opts.Count; i++)
            {
                foreach (var spec in opts[i].applied.Where(p => !p.HookOnly))
                {
                    if (slotOf.TryGetValue(spec.Target, out int existing))
                    {
                        // Shared target: timed once, attributed to the first optimization, marked under both.
                        var slot = slots[existing];
                        if (slot.Opt != i && (slot.SharedWith == null || !slot.SharedWith.Split(',').Contains(opts[i].Id)))
                            slot.SharedWith = slot.SharedWith == null ? opts[i].Id : slot.SharedWith + "," + opts[i].Id;
                        continue;
                    }
                    try
                    {
                        harmony.Patch(spec.Target, prefix: prefix, finalizer: finalizer);
                        slotOf[spec.Target] = slots.Count;
                        slots.Add(new Slot { Method = spec.Target, Opt = i });
                    }
                    catch (Exception e)
                    {
                        skipped.Add($"{opts[i].Id} {spec.Target.DeclaringType?.Name}.{spec.Target.Name}: {e.InnerException?.Message ?? e.Message}");
                    }
                }
            }
            Calibrate(prefix, finalizer);
            CSLog.Message($"profiler: {slots.Count} targets probed, {skipped.Count} skipped, probe overhead {Ns(overheadTicks):F0} ns/call " +
                          $"+ {Ns(nestedOverheadTicks):F0} ns per nested call{(calibrationProblem != null ? "; " + calibrationProblem : "")}");
        }

        public static void Uninstall()
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            mode = -1;
        }

        /// <summary>Starts recording a measured block (on = optimizations applied) of <paramref name="blockTicks"/> ticks.</summary>
        public static void BeginBlock(bool on, int blockTicks)
        {
            mode = on ? 1 : 0;
            where = Frame;
            ticks[mode] += blockTicks;
            depth = 0;
        }

        public static void EndBlock() => mode = -1;

        /// <summary>Brackets TickManager.DoSingleTick; calls outside it count as frame work.</summary>
        public static void BeginTick() => where = Tick;

        public static void EndTick() => where = Frame;

        public static void Prefix(MethodBase __originalMethod, out int __state)
        {
            long now = Stopwatch.GetTimestamp(); // first, so the checks below fall inside the calibrated span
            __state = -1;
            if (mode < 0 || !UnityData.IsInMainThread) return;
            if (!slotOf.TryGetValue(__originalMethod, out int s)) return;
            if (depth == stack.Length) Array.Resize(ref stack, depth * 2);
            __state = s;
            stack[depth++] = new Span { Start = now };
        }

        // Void (not returning the exception), so Harmony keeps `rethrow` and the original stack trace.
        public static void Finalizer(int __state)
        {
            if (__state < 0 || depth == 0) return;
            long end = Stopwatch.GetTimestamp();
            var f = stack[--depth];
            long total = end - f.Start;
            if (depth > 0)
            {
                stack[depth - 1].Child += total;
                stack[depth - 1].ChildCalls++;
            }
            if (mode >= 0)
            {
                var slot = slots[__state];
                slot.Calls[where, mode]++;
                slot.Exclusive[where, mode] += total - f.Child;
                slot.ChildCalls[where, mode] += f.ChildCalls;
            }
        }

        private static int calibrationSink;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Empty() => calibrationSink++;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Outer() => Empty();

        /// <summary>
        /// Times an empty probed method (the probe's cost inside a call's own span) and a probed method that only calls
        /// it (what a nested probed call adds to its caller beyond the child's own span).
        /// </summary>
        private static void Calibrate(HarmonyMethod prefix, HarmonyMethod finalizer)
        {
            var empty = AccessTools.Method(typeof(TargetProfiler), nameof(Empty));
            var outer = AccessTools.Method(typeof(TargetProfiler), nameof(Outer));
            harmony.Patch(empty, prefix: prefix, finalizer: finalizer);
            harmony.Patch(outer, prefix: prefix, finalizer: finalizer);
            int se = slots.Count, so = se + 1;
            slotOf[empty] = se;
            slotOf[outer] = so;
            slots.Add(new Slot { Method = empty, Opt = -1 });
            slots.Add(new Slot { Method = outer, Opt = -1 });
            try
            {
                if (!UnityData.IsInMainThread) { calibrationProblem = "calibration skipped (not on the main thread)"; return; }
                const int n = 200000;
                mode = 0;
                where = Tick;
                for (int i = 0; i < n; i++) Empty();
                for (int i = 0; i < n; i++) Outer();
                mode = -1;
                long emptyCalls = slots[se].Calls[Tick, 0] - n, outerCalls = slots[so].Calls[Tick, 0];
                // The Outer loop also counted n more Empty calls; Empty's own span per call comes from the first loop.
                if (emptyCalls != n || outerCalls != n)
                {
                    calibrationProblem = $"calibration FAILED (probe hit {emptyCalls}/{n} and {outerCalls}/{n} times); nothing subtracted";
                    overheadTicks = nestedOverheadTicks = 0;
                    return;
                }
                overheadTicks = (double)slots[se].Exclusive[Tick, 0] / slots[se].Calls[Tick, 0];
                nestedOverheadTicks = Math.Max(0, (double)slots[so].Exclusive[Tick, 0] / n - overheadTicks);
            }
            finally
            {
                mode = -1;
                where = Frame;
                slots.RemoveRange(se, 2);
                slotOf.Remove(empty);
                slotOf.Remove(outer);
                harmony.Unpatch(empty, HarmonyPatchType.All, HarmonyId);
                harmony.Unpatch(outer, HarmonyPatchType.All, HarmonyId);
            }
        }

        private static double Ns(double stopwatchTicks) => stopwatchTicks * 1e9 / Stopwatch.Frequency;

        /// <summary>Probe-corrected exclusive ms per 1000 ticks of one slot.</summary>
        private static double Per1000(Slot sl, int w, int m) =>
            ticks[m] == 0 ? 0
                : Math.Max(0, sl.Exclusive[w, m] - sl.Calls[w, m] * overheadTicks - sl.ChildCalls[w, m] * nestedOverheadTicks)
                  * 1000.0 / Stopwatch.Frequency * 1000.0 / ticks[m];

        private static double CallsPerTick(long calls, int m) => ticks[m] == 0 ? 0 : (double)calls / ticks[m];

        /// <summary>Per-optimization tables (tick work, then frame work): probe-corrected exclusive ms per 1000 ticks, off vs on.</summary>
        public static string Report(double offMsPerTick)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "PROFILE (main thread, exclusive time in patched targets, ms per 1000 ticks, so 1000 = 1 ms/tick; probe overhead subtracted: {0:F0} ns/call + {1:F0} ns per nested probed call){2}",
                Ns(overheadTicks), Ns(nestedOverheadTicks), calibrationProblem != null ? "; " + calibrationProblem : ""));
            Table(sb, Tick, "inside DoSingleTick", offMsPerTick);
            Table(sb, Frame, "outside ticks (drawing, UI, other frame work; not part of the tick totals above)", 0);
            if (slots.Any(sl => sl.SharedWith != null))
                sb.AppendLine("shared targets (timed once, counted under the first optimization): " +
                              string.Join("; ", slots.Where(sl => sl.SharedWith != null).Select(sl =>
                                  $"{sl.Method.DeclaringType?.Name}.{sl.Method.Name} under {opts[sl.Opt].Id}, shared with {sl.SharedWith}")));
            if (skipped.Count > 0) sb.AppendLine("not probed: " + string.Join("; ", skipped));
            return sb.ToString();
        }

        private static void Table(StringBuilder sb, int w, string title, double offMsPerTick)
        {
            var rows = opts.Select((o, i) =>
            {
                var mine = slots.Where(sl => sl.Opt == i).ToList();
                return (o.Id, targets: mine,
                    off: mine.Sum(sl => Per1000(sl, w, 0)), on: mine.Sum(sl => Per1000(sl, w, 1)),
                    c0: CallsPerTick(mine.Sum(sl => sl.Calls[w, 0]), 0), c1: CallsPerTick(mine.Sum(sl => sl.Calls[w, 1]), 1));
            }).Where(r => r.c0 > 0 || r.c1 > 0).OrderByDescending(r => r.off - r.on).ToList();
            sb.AppendLine($"-- {title}: {rows.Count} optimizations with calls");
            bool pct = offMsPerTick > 0;
            sb.AppendLine("optimization\toff ms/1k\ton ms/1k\tsaved ms/1k" + (pct ? "\tsaved % of off tick" : "") + "\toff calls/tick\ton calls/tick");
            foreach (var r in rows)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}\t{1:F2}\t{2:F2}\t{3:F2}{4}\t{5:F1}\t{6:F1}",
                    r.Id, r.off, r.on, r.off - r.on,
                    pct ? string.Format(CultureInfo.InvariantCulture, "\t{0:F2}", 100.0 * (r.off - r.on) / 1000.0 / offMsPerTick) : "",
                    r.c0, r.c1));
            sb.AppendLine("per target (off ms/1k, on ms/1k, off calls/tick, on calls/tick):");
            foreach (var r in rows)
                foreach (var sl in r.targets.Where(sl => sl.Calls[w, 0] + sl.Calls[w, 1] > 0))
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0}\t{1}.{2}\t{3:F2}\t{4:F2}\t{5:F1}\t{6:F1}{7}",
                        r.Id, sl.Method.DeclaringType?.Name, sl.Method.Name, Per1000(sl, w, 0), Per1000(sl, w, 1),
                        CallsPerTick(sl.Calls[w, 0], 0), CallsPerTick(sl.Calls[w, 1], 1),
                        sl.SharedWith != null ? "\t(shared with " + sl.SharedWith + ")" : ""));
            var silent = opts.Where((o, i) => !rows.Any(r => r.Id == o.Id)).Select(o => o.Id).ToList();
            if (silent.Count > 0) sb.AppendLine("no calls here: " + string.Join(", ", silent));
        }

        public static void Reset()
        {
            slotOf.Clear();
            slots.Clear();
            skipped.Clear();
            ticks[0] = ticks[1] = 0;
            depth = 0;
            calibrationProblem = null;
            overheadTicks = nestedOverheadTicks = 0;
        }
    }
}
