using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using HarmonyLib;

namespace ColonyScale.Tests
{
    /// <summary>
    /// Offline checks against the installed game assemblies (no Unity runtime needed):
    ///   check [--fingerprints F]       resolve targets, run transpilers, apply patches, compare fingerprints
    ///   fingerprints --write F --game V record current fingerprints of every patch target
    ///   canonical OPT_ID               print canonical IL of an optimization's targets (for diffs)
    /// check also runs the game-independent fingerprint fixtures in SelfTest.
    /// Exit code 0 = all good, 1 = at least one problem.
    /// </summary>
    public static class Program
    {
        private static string ManagedDir =>
            Path.Combine(Environment.GetEnvironmentVariable("RIMWORLD_DIR")
                ?? throw new InvalidOperationException("RIMWORLD_DIR is not set (the Makefile sets it; run the checker through make)"), "RimWorldWin64_Data", "Managed");

        public static int Main(string[] args)
        {
            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                string path = Path.Combine(ManagedDir, name.Name + ".dll");
                // Prefer .NET's own BCL; only game and Unity assemblies come from the install.
                if (name.Name == "mscorlib" || name.Name == "netstandard" || name.Name.StartsWith("System")) return null;
                return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
            };
            return Run(args);
        }

        private static int Run(string[] args)
        {
            string cmd = args.Length > 0 ? args[0] : "check";
            string Arg(string flag) { int i = Array.IndexOf(args, flag); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

            // A broken real optimization is reported as a FAIL line by check; the fixture stub's warning is expected noise.
            var warn = CSLog.WarningSink;
            CSLog.WarningSink = _ => { };
            List<Optimization> all;
            try { all = OptimizationRegistry.Discover().Where(o => !IsFixture(o)).ToList(); }
            finally { CSLog.WarningSink = warn; }
            switch (cmd)
            {
                case "check": return Check(all, Arg("--fingerprints"));
                case "fingerprints": return WriteFingerprints(all, Arg("--write"), Arg("--game") ?? "");
                case "canonical":
                    foreach (var p in all.Single(o => o.Id == args[1]).GetPatches())
                        Console.WriteLine(ILFingerprint.Canonical(p.Target));
                    return 0;
                case "list":
                    foreach (var o in all)
                        foreach (var p in o.GetPatches())
                            Console.WriteLine(o.Id + "\t" + ILFingerprint.MemberId(p.Target));
                    return 0;
                case "gameversion":
                    Console.WriteLine(GameVersion());
                    return 0;
                case "dump-il":
                    return DumpIL(all, Arg("--out"));
                default:
                    Console.Error.WriteLine("unknown command " + cmd);
                    return 2;
            }
        }

        private static bool IsFixture(Optimization o) =>
            typeof(IOfflineFixture).IsAssignableFrom(o is UnavailableOptimization u ? u.Type : o.GetType());

        /// <summary>Same formula as RimWorld.VersionControl, without running its Unity-touching static constructor.</summary>
        private static string GameVersion()
        {
            var v = typeof(Verse.Thing).Assembly.GetName().Version;
            return $"{v.Major}.{v.Minor}.{v.Build - 4805} rev{v.Revision * 2 / 60}";
        }

        /// <summary>One file per patch target with its canonical IL, so updates can be reviewed as plain diffs.</summary>
        private static int DumpIL(List<Optimization> all, string dir)
        {
            Directory.CreateDirectory(dir);
            foreach (var o in all)
            {
                List<MethodBase> methods;
                try { methods = Methods(o).ToList(); }
                catch (Exception e)
                {
                    File.WriteAllText(Path.Combine(dir, o.Id + "__TARGET_MISSING.il"), e.Message + "\n");
                    continue;
                }
                foreach (var method in methods)
                {
                    string member = ILFingerprint.MemberId(method);
                    string safe = new string(member.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_').ToArray());
                    if (safe.Length > 120) safe = safe.Substring(0, 120);
                    File.WriteAllText(Path.Combine(dir, o.Id + "__" + safe + ".il"), ILFingerprint.Canonical(method));
                }
            }
            return 0;
        }

        /// <summary>Every method an optimization's correctness rests on: patch targets (except hook-only ones) plus DependsOn.</summary>
        private static IEnumerable<MethodBase> Methods(Optimization o) => PatchSpec.Fingerprinted(o.GetPatches(), o.DependsOn);

        private static int WriteFingerprints(List<Optimization> all, string path, string game)
        {
            var store = new FingerprintStore { GameVersion = game, HarmonyVersion = PatchManager.HarmonyVersion.ToString() };
            foreach (var o in all)
                foreach (var m in Methods(o))
                    store.Set(o.Id, ILFingerprint.MemberId(m), ILFingerprint.Compute(m));
            store.Save(path);
            Console.WriteLine($"wrote {path} ({all.Count} optimizations, game {game}, Harmony {store.HarmonyVersion})");
            return 0;
        }

        private static int Check(List<Optimization> all, string fingerprintPath)
        {
            var store = fingerprintPath != null ? FingerprintStore.Load(fingerprintPath) : null;
            int problems = 0;
            void Fail(Optimization o, string msg) { problems++; Console.WriteLine($"FAIL  {o.Id}: {msg}"); }

            Console.WriteLine($"checking {all.Count} optimizations against {ManagedDir}");
            if (all.Select(o => o.Id).Distinct().Count() != all.Count) { problems++; Console.WriteLine("FAIL  duplicate optimization ids"); }
            int selfTestProblems = SelfTest.Run(msg => Console.WriteLine("FAIL  fingerprint self-test: " + msg));
            problems += selfTestProblems;
            if (selfTestProblems == 0) Console.WriteLine("ok    fingerprint self-test");

            var harmony = new Harmony("ColonyScale.OfflineCheck");
            foreach (var o in all)
            {
                List<PatchSpec> patches;
                try { patches = o.GetPatches().ToList(); }
                catch (Exception e) { Fail(o, "target resolution: " + e.Message); continue; }
                if (patches.Count == 0) { Fail(o, "no patches"); continue; }

                bool ok = true;
                List<MethodBase> methods;
                try { methods = Methods(o).ToList(); }
                catch (Exception e) { Fail(o, "dependency resolution: " + e.Message); continue; }
                if (methods.Any(m => m == null)) { Fail(o, "a DependsOn method was not found"); continue; }
                if (store != null)
                    foreach (var m in methods)
                    {
                        string member = ILFingerprint.MemberId(m);
                        string actual = ILFingerprint.Compute(m);
                        if (!store.TryGet(o.Id, member, out var expected)) { Fail(o, "no recorded fingerprint for " + member); ok = false; }
                        else if (expected != actual) { Fail(o, $"code changed: {member} (recorded {expected}, now {actual})"); ok = false; }
                    }
                foreach (var p in patches)
                {
                    string member = ILFingerprint.MemberId(p.Target);
                    string invalid = p.Problem();
                    if (invalid != null) { Fail(o, invalid); ok = false; continue; }
                    if (p.Transpiler != null)
                    {
                        try { RunTranspiler(p); }
                        catch (Exception e) { Fail(o, $"transpiler on {member}: {(e.InnerException ?? e).Message}"); ok = false; continue; }
                    }
                    try { harmony.Patch(p.Target, p.Prefix, p.Postfix, p.Transpiler); }
                    catch (Exception e) { Fail(o, $"applying patch to {member}: {(e.InnerException ?? e).Message}"); ok = false; }
                }
                if (ok) Console.WriteLine($"ok    {o.Id} ({patches.Count} patch{(patches.Count == 1 ? "" : "es")})");
            }
            harmony.UnpatchAll(harmony.Id);
            problems += ListSemanticsCheck.Run();
            int managerProblems = PatchManagerTest.Run(msg => Console.WriteLine("FAIL  patch manager self-test: " + msg));
            problems += managerProblems;
            if (managerProblems == 0) Console.WriteLine("ok    patch manager self-test");
            Console.WriteLine(problems == 0 ? "all checks passed" : $"{problems} problem(s)");
            return problems == 0 ? 0 : 1;
        }

        /// <summary>Runs a transpiler on the original instructions the way Harmony would, outside of patching.</summary>
        private static void RunTranspiler(PatchSpec p)
        {
            var instructions = PatchProcessor.GetOriginalInstructions(p.Target, out ILGenerator generator);
            var method = p.Transpiler.method;
            var args = method.GetParameters().Select(param =>
                param.ParameterType == typeof(ILGenerator) ? generator :
                typeof(MethodBase).IsAssignableFrom(param.ParameterType) ? (object)p.Target :
                instructions.Select(i => i.Clone())).ToArray();
            var result = ((IEnumerable<CodeInstruction>)method.Invoke(null, args)).ToList();
            if (result.Count == 0) throw new InvalidOperationException("transpiler returned no instructions");
        }
    }
}
