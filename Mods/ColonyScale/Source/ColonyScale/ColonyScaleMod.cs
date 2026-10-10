using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ColonyScale
{
    public sealed class ColonyScaleSettings : ModSettings
    {
        /// <summary>Only user deviations from each optimization's default are stored.</summary>
        public Dictionary<string, bool> overrides = new Dictionary<string, bool>();
        public bool trustChangedTargets;

        public bool IsEnabled(Optimization opt) =>
            overrides.TryGetValue(opt.Id, out var on) ? on : opt.EnabledByDefault;

        public void SetEnabled(Optimization opt, bool on)
        {
            if (on == opt.EnabledByDefault) overrides.Remove(opt.Id);
            else overrides[opt.Id] = on;
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref overrides, "overrides", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref trustChangedTargets, "trustChangedTargets", false);
            if (overrides == null) overrides = new Dictionary<string, bool>();
        }
    }

    public sealed class ColonyScaleMod : Mod
    {
        public static ColonyScaleMod Instance;
        public static ColonyScaleSettings Settings;
        /// <summary>Why the mod refused to start (unsupported Harmony); null when it runs normally.</summary>
        public static string StartupProblem;
        private Vector2 scroll;

        public ColonyScaleMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<ColonyScaleSettings>();
            CSLog.MessageSink = Log.Message;
            CSLog.WarningSink = Log.Warning;
            CSLog.ErrorSink = Log.Error;
            StartupProblem = PatchManager.HarmonyVersionProblem(PatchManager.HarmonyVersion);
            if (StartupProblem != null)
            {
                CSLog.Error(StartupProblem);
                return;
            }
            // A second (e.g. Workshop) copy of a mod is active under "<packageId>_steam".
            PatchManager.IsModActive = id => ModsConfig.IsActive(id) || ModsConfig.IsActive(id + "_steam");
            PatchManager.TrustChangedTargets = Settings.trustChangedTargets;
            PatchManager.Init(FingerprintStore.Load(Path.Combine(content.RootDir, FingerprintStore.FileName)));
            // Optimizations are applied by ColonyScaleStartup, once every mod has installed its startup patches.
            HeadlessBenchmark.CheckCommandLine();
        }

        internal static void ApplyAll()
        {
            if (PatchManager.Fingerprints.GameVersion != RimWorld.VersionControl.CurrentVersionStringWithRev)
                CSLog.Warning($"built for game {PatchManager.Fingerprints.GameVersion}, running on {RimWorld.VersionControl.CurrentVersionStringWithRev}; " +
                              "optimizations whose target code changed will stay off");
            foreach (var opt in PatchManager.All)
            {
                if (opt is UnavailableOptimization) continue; // keeps its Failed state and reason
                if (Settings.IsEnabled(opt)) PatchManager.TryEnable(opt);
                else opt.State = OptimizationState.DisabledByUser;
            }
            int changed = PatchManager.All.Count(o => o.State == OptimizationState.DisabledTargetChanged);
            if (changed > 0)
            {
                var recorded = PatchManager.Fingerprints;
                string recordedHarmony = recorded.HarmonyVersion.Length > 0 ? recorded.HarmonyVersion : "an unrecorded version";
                CSLog.Warning($"{changed} optimization(s) are off because the code they patch no longer matches its fingerprint. " +
                              $"Fingerprints were recorded on game {recorded.GameVersion} with Harmony {recordedHarmony}; running game " +
                              $"{RimWorld.VersionControl.CurrentVersionStringWithRev} with Harmony {PatchManager.HarmonyVersion}. " +
                              "If only the Harmony version differs, Harmony may read the IL differently and the game code may be unchanged.");
            }
            int active = PatchManager.All.Count(o => o.IsActive);
            CSLog.Message($"{active}/{PatchManager.All.Count} optimizations active");
        }

        public override string SettingsCategory() => "Colony Scale";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            if (StartupProblem != null)
            {
                GUI.color = Color.red;
                Widgets.Label(inRect, StartupProblem);
                GUI.color = Color.white;
                return;
            }
            var opts = PatchManager.All;
            float rowHeight = 54f;
            var view = new Rect(0f, 0f, inRect.width - 20f, 90f + opts.Count * rowHeight + 30f * opts.Select(o => o.Category).Distinct().Count());
            Widgets.BeginScrollView(inRect, ref scroll, view);
            var list = new Listing_Standard();
            list.Begin(view);

            bool trust = Settings.trustChangedTargets;
            list.CheckboxLabeled("Apply optimizations even if the game code they patch changed (unsafe, needs restart)", ref trust,
                "After a game update, optimizations whose patched methods changed turn themselves off until Colony Scale is rebuilt. " +
                "Turning this on applies them anyway.");
            if (trust != Settings.trustChangedTargets) { Settings.trustChangedTargets = trust; PatchManager.TrustChangedTargets = trust; }
            list.GapLine();

            string category = null;
            foreach (var opt in opts)
            {
                if (opt.Category != category)
                {
                    category = opt.Category;
                    Text.Font = GameFont.Medium;
                    list.Label(category);
                    Text.Font = GameFont.Small;
                }
                bool on = Settings.IsEnabled(opt);
                bool was = on;
                list.CheckboxLabeled(opt.Label, ref on, opt.Description);
                if (on != was && !(opt is UnavailableOptimization))
                {
                    Settings.SetEnabled(opt, on);
                    if (on) PatchManager.TryEnable(opt);
                    else PatchManager.Disable(opt, OptimizationState.DisabledByUser);
                }
                GUI.color = opt.IsActive ? Color.green
                    : opt.State == OptimizationState.DisabledByUser ? Color.gray
                    : opt.State == OptimizationState.DisableFailed ? Color.red : Color.yellow;
                Text.Font = GameFont.Tiny;
                list.Label("   " + StateText(opt));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
            list.End();
            Widgets.EndScrollView();
        }

        private static string StateText(Optimization opt)
        {
            switch (opt.State)
            {
                case OptimizationState.Active: return "active";
                case OptimizationState.DisabledByUser: return "off";
                case OptimizationState.DisableFailed: return "failed to disable: " + opt.StateReason;
                default: return opt.State + (opt.StateReason != null ? ": " + opt.StateReason : "");
            }
        }
    }

    /// <summary>
    /// Applies the optimizations after other mods' startup patches. Mod constructors' ExecuteWhenFinished callbacks
    /// run before StaticConstructorOnStartupUtility.CallAll (PlayDataLoader queues it after LoadAllActiveMods),
    /// and CallAll itself runs inside LongEventHandler's in-order ExecuteWhenFinished loop, so a callback queued
    /// here lands after every static constructor; the second hop also lets the callbacks those constructors
    /// queue run first. Patches installed later still are caught by the revalidation at game start.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ColonyScaleStartup
    {
        static ColonyScaleStartup()
        {
            if (ColonyScaleMod.StartupProblem != null) return;
            LongEventHandler.ExecuteWhenFinished(() => LongEventHandler.ExecuteWhenFinished(ColonyScaleMod.ApplyAll));
            new Harmony(PatchManager.HarmonyId).Patch(AccessTools.Method(typeof(Game), nameof(Game.FinalizeInit)),
                postfix: new HarmonyMethod(typeof(ColonyScaleStartup), nameof(FinalizeInitPostfix)));
        }

        /// <summary>New game and load both end here; revalidate on the main thread once loading finishes.</summary>
        private static void FinalizeInitPostfix() =>
            LongEventHandler.ExecuteWhenFinished(() => PatchManager.RevalidateActive("at game start: another mod patched their code after startup"));
    }
}
