using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyScale
{
    public sealed class BenchConfig
    {
        public int warmupTicks = 600;
        /// <summary>Discarded ticks after every switch between on and off (cache rebuilds, fresh Harmony wrappers).</summary>
        public int settleTicks = 120;
        public int blockTicks = 500;
        public int rounds = 4;
        /// <summary>Ticks per rendered frame; ~15 is what speed 3 manages on a busy late-game colony.</summary>
        public int ticksPerFrame = 15;
        public int colonists;
        public int animals;
        public int itemStacks;
        public string output;
        public bool quitWhenDone;
        public bool quickTest;
        /// <summary>Correctness run: optimizations also run vanilla side by side and count mismatches. Timings are meaningless.</summary>
        public bool verify;

        public static BenchConfig Parse(IEnumerable<string> lines)
        {
            var cfg = new BenchConfig();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.Length == 0 || line[0] == '#' || eq < 0) continue;
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n);
                switch (key)
                {
                    case "warmupTicks": cfg.warmupTicks = n; break;
                    case "settleTicks": cfg.settleTicks = n; break;
                    case "blockTicks": cfg.blockTicks = n; break;
                    case "rounds": cfg.rounds = n; break;
                    case "ticksPerFrame": cfg.ticksPerFrame = Math.Max(1, n); break;
                    case "colonists": cfg.colonists = n; break;
                    case "animals": cfg.animals = n; break;
                    case "itemStacks": cfg.itemStacks = n; break;
                    case "output": cfg.output = value; break;
                    case "quitWhenDone": cfg.quitWhenDone = value == "true"; break;
                    case "start": cfg.quickTest = value == "quicktest"; break;
                    case "verify": cfg.verify = value == "true"; break;
                }
            }
            return cfg;
        }
    }

    /// <summary>
    /// Interleaved A/B benchmark: alternates blocks of ticks with this mod's optimizations removed and
    /// applied (ABBA order to cancel drift), timing only TickManager.DoSingleTick. Ticks are spread
    /// over frames like real play so per-frame caches in vanilla behave normally. After every actual switch
    /// settleTicks ticks are discarded, so blocks measure steady-state tick cost, not the switch.
    /// </summary>
    public sealed class BenchmarkRunner : MonoBehaviour
    {
        private enum Phase { Warmup, Settle, Measure }

        private static BenchmarkRunner instance;
        public static bool Running => instance != null;

        private BenchConfig cfg;
        private Action<string> onDone;
        private List<Optimization> userActive;
        private TimeSpeed previousSpeed;
        private bool previousVerify;
        private readonly List<(bool on, double msPerTick, int gcs)> blocks = new List<(bool, double, int)>();
        private readonly int[] errors = new int[2];
        private Phase phase = Phase.Warmup;
        private int blockIndex;
        private int ticksLeft;
        private long elapsedTicks;
        private int gcAtStart;
        private int transitions;
        private bool currentOn = true;        // the mode actually in effect (verified against Harmony's patch state)
        private bool finished;

        public static void Start(BenchConfig cfg, Action<string> onDone)
        {
            if (Running) { Messages.Message("Colony Scale benchmark already running", MessageTypeDefOf.RejectInput, false); return; }
            var go = new GameObject("ColonyScaleBenchmark");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<BenchmarkRunner>();
            instance.cfg = cfg;
            instance.onDone = onDone;
            instance.userActive = PatchManager.All.Where(o => o.IsActive).ToList();
            instance.previousSpeed = Find.TickManager.CurTimeSpeed;
            instance.previousVerify = Verify.Enabled;
            instance.ticksLeft = cfg.warmupTicks;
            Application.logMessageReceived += instance.CountErrors;
            if (cfg.verify)
            {
                Verify.Reset();
                Verify.Enabled = true;
            }
            CSLog.Message($"benchmark started: {cfg.rounds} rounds x 2 x {cfg.blockTicks} ticks, {instance.userActive.Count} optimizations under test");
        }

        private void CountErrors(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception) errors[currentOn ? 1 : 0]++;
        }

        private void Update()
        {
            if (finished) return;
            try { Step(); }
            catch (Exception e) { Finish("benchmark aborted by an exception: " + e); }
        }

        private void Step()
        {
            var tickManager = Current.Game?.tickManager;
            if (Current.ProgramState != ProgramState.Playing || tickManager == null || Find.CurrentMap == null)
            {
                Finish("benchmark aborted: game no longer running");
                return;
            }
            tickManager.CurTimeSpeed = TimeSpeed.Paused; // we drive the ticks ourselves

            int n = Math.Min(cfg.ticksPerFrame, ticksLeft);
            for (int i = 0; i < n; i++)
            {
                // Never tick where vanilla would not: force-pausing windows and long events (Root_Play.Update).
                // Checked before every tick, since a tick can open a dialog or queue a long event.
                if (tickManager.ForcePaused || LongEventHandler.ShouldWaitForEvent) break;
                long start = Stopwatch.GetTimestamp();
                tickManager.DoSingleTick();
                elapsedTicks += Stopwatch.GetTimestamp() - start;
                ticksLeft--;
            }
            if (ticksLeft > 0) return;

            if (phase == Phase.Measure)
            {
                double ms = elapsedTicks * 1000.0 / Stopwatch.Frequency / cfg.blockTicks;
                blocks.Add((currentOn, ms, GC.CollectionCount(0) - gcAtStart));
                blockIndex++;
                if (blockIndex >= cfg.rounds * 2)
                {
                    Finish(Report());
                    return;
                }
            }
            if (phase != Phase.Settle && !BeginBlock()) return;
            StartMeasuring();
        }

        /// <summary>Switches to the mode of the current block. False when the run was aborted or now settles first.</summary>
        private bool BeginBlock()
        {
            // ABBA: round 0 = off,on; round 1 = on,off; ...
            bool firstOn = blockIndex / 2 % 2 == 1;
            bool on = blockIndex % 2 == 0 ? firstOn : !firstOn;
            if (on == currentOn) return true;

            var failed = SetOptimizations(on);
            if (failed.Count > 0)
            {
                Finish($"benchmark aborted: could not switch these optimizations {(on ? "on" : "off")}, the comparison would be invalid: " + Describe(failed));
                return false;
            }
            currentOn = on;
            transitions++;
            if (cfg.settleTicks <= 0) return true;
            phase = Phase.Settle;
            ticksLeft = cfg.settleTicks;
            return false;
        }

        private void StartMeasuring()
        {
            phase = Phase.Measure;
            GC.Collect();
            gcAtStart = GC.CollectionCount(0);
            ticksLeft = cfg.blockTicks;
            elapsedTicks = 0;
        }

        /// <summary>Returns the optimizations whose actual Harmony patch state does not match the requested mode.</summary>
        private List<Optimization> SetOptimizations(bool on)
        {
            var failed = new List<Optimization>();
            foreach (var opt in userActive)
            {
                bool ok = on
                    ? PatchManager.TryEnable(opt) && PatchManager.HasAllPatchesOf(opt)
                    : PatchManager.Disable(opt, OptimizationState.NotApplied) && PatchManager.HasNoPatchesOf(opt);
                if (!ok) failed.Add(opt);
            }
            return failed;
        }

        private static string Describe(IEnumerable<Optimization> opts) =>
            string.Join("; ", opts.Select(o => $"{o.Id} ({o.State}{(o.StateReason != null ? ": " + o.StateReason : "")})"));

        private string Report()
        {
            var off = blocks.Where(b => !b.on).Select(b => b.msPerTick).ToList();
            var on = blocks.Where(b => b.on).Select(b => b.msPerTick).ToList();
            double offMean = off.Average(), onMean = on.Average();
            var sb = new StringBuilder();
            sb.AppendLine("Colony Scale A/B benchmark");
            sb.AppendLine($"game {VersionControl.CurrentVersionStringWithRev}, map pawns {Find.CurrentMap.mapPawns.AllPawnsSpawnedCount}, colonists {Find.CurrentMap.mapPawns.FreeColonistsSpawnedCount}, things {Find.CurrentMap.listerThings.AllThings.Count}");
            sb.AppendLine($"blocks {cfg.rounds}x2 of {cfg.blockTicks} ticks, {cfg.ticksPerFrame} ticks/frame, warmup {cfg.warmupTicks}, {cfg.settleTicks} discarded ticks after each of {transitions} on/off switches");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "off: {0:F3} ms/tick (median {1:F3})  errors {2}", offMean, Median(off), errors[0]));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "on:  {0:F3} ms/tick (median {1:F3})  errors {2}", onMean, Median(on), errors[1]));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "speedup: {0:F1}% less tick time; tick-only TPS ceiling {1:F0} -> {2:F0} (DoSingleTick time only, excludes rendering and other frame work)",
                100.0 * (1 - onMean / offMean), 1000 / offMean, 1000 / onMean));
            sb.AppendLine("block\tmode\tms/tick\tgen0 GCs");
            for (int i = 0; i < blocks.Count; i++)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}\t{1}\t{2:F4}\t{3}", i, blocks[i].on ? "on" : "off", blocks[i].msPerTick, blocks[i].gcs));
            sb.AppendLine("optimizations under test: " + string.Join(", ", userActive.Select(o => o.Id)));
            if (cfg.verify) sb.Append("VERIFY RUN (timings not meaningful). ").Append(Verify.Report());
            return sb.ToString();
        }

        private static double Median(List<double> xs)
        {
            var s = xs.OrderBy(x => x).ToList();
            return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
        }

        /// <summary>Restores the optimizations that were active at the start, whatever mode the run ended in.</summary>
        private void Finish(string report)
        {
            if (finished) return;
            finished = true;
            var notRestored = new List<Optimization>();
            try
            {
                foreach (var opt in userActive)
                    if (!PatchManager.TryEnable(opt) || !PatchManager.HasAllPatchesOf(opt)) notRestored.Add(opt);
            }
            catch (Exception e)
            {
                report += "\nrestoring the optimizations threw: " + e;
            }
            finally
            {
                Verify.Enabled = previousVerify;
                Application.logMessageReceived -= CountErrors;
                var tickManager = Current.Game?.tickManager;
                if (tickManager != null) tickManager.CurTimeSpeed = previousSpeed;
                instance = null;
                Destroy(gameObject);
            }
            if (notRestored.Count > 0)
            {
                string msg = "these optimizations were active before the benchmark and could not be restored: " + Describe(notRestored);
                CSLog.Error(msg);
                report += "\n" + msg;
            }

            CSLog.Message(report);
            string path = cfg.output ?? Path.Combine(GenFilePaths.SaveDataFolderPath, "ColonyScale-benchmark.txt");
            try { File.WriteAllText(path, report); }
            catch (Exception e) { CSLog.Error("could not write benchmark report: " + e.Message); }
            onDone?.Invoke(report);
        }
    }

    public static class BenchmarkDebugActions
    {
        [DebugAction("Colony Scale", "A/B benchmark (about 1 min)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void Quick() => Run(new BenchConfig { warmupTicks = 300, settleTicks = 100, blockTicks = 300, rounds = 3 });

        [DebugAction("Colony Scale", "A/B benchmark (long, more precise)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void Long() => Run(new BenchConfig { warmupTicks = 600, settleTicks = 200, blockTicks = 1000, rounds = 5 });

        private static void Run(BenchConfig cfg) =>
            BenchmarkRunner.Start(cfg, report => Find.WindowStack.Add(new Dialog_MessageBox(report)));
    }
}
