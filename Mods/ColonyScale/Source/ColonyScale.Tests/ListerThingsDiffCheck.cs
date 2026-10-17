using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using ColonyScale.Optimizations.Map;
using HarmonyLib;
using Verse;

namespace ColonyScale.Tests
{
    /// <summary>
    /// Differential check of <see cref="ListerThingsFast"/> against the game's own ListerThings: the same random
    /// Add/Remove sequence goes through vanilla's methods on one lister and through the replacing prefixes on another,
    /// then every def list, group list (element order included) and group state hash must match. Catches vanilla
    /// changing its list semantics (1.7 switched Remove to swap-removal). Defs are bare ThingDefs, nothing is spawned.
    /// </summary>
    public static class ListerThingsDiffCheck
    {
        private static readonly AccessTools.FieldRef<ListerThings, Dictionary<ThingDef, List<Thing>>> listsByDef =
            AccessTools.FieldRefAccess<ListerThings, Dictionary<ThingDef, List<Thing>>>("listsByDef");
        private static readonly AccessTools.FieldRef<ListerThings, List<Thing>[]> listsByGroup =
            AccessTools.FieldRefAccess<ListerThings, List<Thing>[]>("listsByGroup");
        private static readonly AccessTools.FieldRef<ListerThings, int[]> stateHashByGroup =
            AccessTools.FieldRefAccess<ListerThings, int[]>("stateHashByGroup");

        public static int Run()
        {
            int problems = 0;
            void Fail(string msg) { problems++; Console.WriteLine("FAIL  listerThings differential: " + msg); }

            // Includes asks ModsConfig.OdysseyActive; ModsConfig's static constructor needs the save-data folder, prefs and
            // the installed mod list. Stub the getter instead (Core only), so ModsConfig is never initialized here.
            var harmony = new Harmony("ColonyScale.OfflineCheck.ListerThingsDiff");
            harmony.Patch(AccessTools.PropertyGetter(typeof(ModsConfig), nameof(ModsConfig.OdysseyActive)),
                prefix: new HarmonyMethod(typeof(ListerThingsDiffCheck), nameof(DlcInactive)));
            try { Differential(Fail); }
            finally { harmony.UnpatchAll(harmony.Id); }
            if (problems == 0) Console.WriteLine("ok    listerThings differential (vanilla vs replaced Add/Remove)");
            return problems;
        }

        private static bool DlcInactive(ref bool __result)
        {
            __result = false;
            return false;
        }

        private static void Differential(Action<string> Fail)
        {

            var defs = new List<ThingDef>();
            for (int i = 0; i < 3; i++)
                defs.Add(new ThingDef { defName = "ColonyScaleCheckItem" + i, category = ThingCategory.Item, thingClass = typeof(Thing), index = (ushort)i });
            Thing Make(ThingDef def, int id)
            {
                var t = (Thing)RuntimeHelpers.GetUninitializedObject(typeof(Thing));
                t.def = def;
                t.thingIDNumber = id;
                return t;
            }

            foreach (var use in new[] { ListerThingsUse.Global, ListerThingsUse.Region })
            {
                ListerThingsFast.ResetCaches();
                var rand = new Random(777 + (int)use);
                var pool = new List<Thing>();
                for (int i = 0; i < 30; i++) pool.Add(Make(defs[rand.Next(defs.Count)], i));
                for (int i = 0; i < 6; i++) { var src = pool[rand.Next(30)]; pool.Add(Make(src.def, src.thingIDNumber)); } // twins
                var vanilla = new ListerThings(use);
                var fast = new ListerThings(use);
                var inLister = new List<Thing>();
                for (int step = 0; step < 3000; step++)
                {
                    // Remove only what is listed (vanilla's callers do the same); twins and repeats still happen.
                    if (inLister.Count > 0 && rand.Next(5) < 2)
                    {
                        var t = inLister[rand.Next(inLister.Count)];
                        inLister.Remove(t);
                        vanilla.Remove(t);
                        ListerThingsFast.RemovePrefix(fast, t);
                    }
                    else
                    {
                        var t = pool[rand.Next(pool.Count)];
                        inLister.Add(t);
                        vanilla.Add(t);
                        ListerThingsFast.AddPrefix(fast, t);
                    }
                    string diff = Compare(vanilla, fast);
                    if (diff != null) { Fail($"{use} lister, step {step}: {diff}"); break; }
                }
            }
            ListerThingsFast.ResetCaches();
        }

        private static string Compare(ListerThings a, ListerThings b)
        {
            var da = listsByDef(a);
            var db = listsByDef(b);
            foreach (var kv in da)
            {
                if (!db.TryGetValue(kv.Key, out var other)) return "def list missing for " + kv.Key.defName;
                if (!kv.Value.SequenceEqual(other)) return "def list order differs for " + kv.Key.defName;
            }
            if (da.Count != db.Count) return "different def list count";
            var ga = listsByGroup(a);
            var gb = listsByGroup(b);
            for (int g = 0; g < ga.Length; g++)
            {
                if ((ga[g] == null) != (gb[g] == null)) return "group list " + g + " created on one side only";
                if (ga[g] != null && !ga[g].SequenceEqual(gb[g])) return "group list " + g + " order differs";
            }
            if (!stateHashByGroup(a).SequenceEqual(stateHashByGroup(b))) return "group state hashes differ";
            return null;
        }
    }
}
