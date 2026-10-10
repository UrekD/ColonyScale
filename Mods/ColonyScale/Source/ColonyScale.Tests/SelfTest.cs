using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ColonyScale.Tests
{
    /// <summary>
    /// Fixtures for <see cref="ILFingerprint"/> that need no game: float operands are canonical bit patterns, and
    /// method/local signature changes that leave the instruction bytes alone still change the fingerprint.
    /// </summary>
    public static class SelfTest
    {
        public static int Run(Action<string> fail)
        {
            int problems = 0;
            void Expect(bool ok, string what) { if (!ok) { problems++; fail(what); } }
            var noLabels = new Dictionary<Label, int>();
            string Op(object o) => ILFingerprint.Operand(o, noLabels);

            // 2π as stored in GenTemperature.OffsetFromSeasonCycle: Mono's "R" gives 6.28318548, .NET's 6.2831855.
            Expect(Op(6.2831855f) == "f32:40c90fdb", "float operand 6.2831855f is " + Op(6.2831855f));
            Expect(Op(BitConverter.ToSingle(new byte[] { 0xdb, 0x0f, 0xc9, 0x40 }, 0)) == "f32:40c90fdb", "float operand read from IL bytes");
            Expect(Op(Math.PI) == "f64:400921fb54442d18", "double operand Math.PI is " + Op(Math.PI));
            Expect(Op(0f) != Op(-0f), "float +0 and -0 must differ");
            Expect(Op(0d) != Op(-0d), "double +0 and -0 must differ");
            float nanA = BitConverter.ToSingle(BitConverter.GetBytes(0x7fc00000), 0);
            float nanB = BitConverter.ToSingle(BitConverter.GetBytes(0x7fc00001), 0);
            Expect(Op(nanA) != Op(nanB), "float NaN payloads must differ");
            Expect(Op("a\"\\\né") == "\"a\\\"\\\\\\u000a\\u00e9\"", "string operand escaping is " + Op("a\"\\\né"));

            // Same instructions (ldc.i4.m1; ret), different return type.
            SameBodyDifferentFingerprint(nameof(Fixtures.MinusOneInt), nameof(Fixtures.MinusOneUInt), "return type", Expect);
            // Same instructions (ldc.i4.1; ret), static vs instance.
            SameBodyDifferentFingerprint(nameof(Fixtures.OneStatic), nameof(Fixtures.OneInstance), "calling convention", Expect);
            // Same instructions (ldnull; stloc.0; ...; ldloc.0; ret), different local type.
            SameBodyDifferentFingerprint(nameof(Fixtures.LocalObject), nameof(Fixtures.LocalString), "local signature", Expect);

            // L7: the header records the Harmony version next to the game version and survives a round trip.
            string path = System.IO.Path.GetTempFileName();
            try
            {
                var written = new FingerprintStore { GameVersion = "1.6.0 rev1", HarmonyVersion = "2.4.2.0" };
                written.Set("opt", "member", "hash");
                written.Save(path);
                var read = FingerprintStore.Load(path);
                Expect(read.GameVersion == "1.6.0 rev1" && read.HarmonyVersion == "2.4.2.0" && read.TryGet("opt", "member", out var h) && h == "hash",
                    $"fingerprint header round trip gave game '{read.GameVersion}', Harmony '{read.HarmonyVersion}'");
            }
            finally { System.IO.File.Delete(path); }
            return problems;
        }

        private static void SameBodyDifferentFingerprint(string a, string b, string what, Action<bool, string> expect)
        {
            var ma = typeof(Fixtures).GetMethod(a, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
            var mb = typeof(Fixtures).GetMethod(b, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
            // Header = signature and locals lines; the rest is the instruction listing.
            var la = ILFingerprint.Canonical(ma).Split('\n');
            var lb = ILFingerprint.Canonical(mb).Split('\n');
            expect(la.Skip(2).SequenceEqual(lb.Skip(2)), $"fixture {a}/{b} must have identical instructions to test the {what}");
            expect(ILFingerprint.Canonical(ma).Replace(a, "M") != ILFingerprint.Canonical(mb).Replace(b, "M"), $"fingerprint ignores the {what} ({a} vs {b})");
        }
    }

    public sealed class Fixtures
    {
        public static bool Flag;

        public static int MinusOneInt() => -1;
        public static uint MinusOneUInt() => uint.MaxValue;
        public static int OneStatic() => 1;
        public int OneInstance() => 1;

        public static object LocalObject()
        {
            object t = null;
            if (Flag) t = null;
            return t;
        }

        public static object LocalString()
        {
            string t = null;
            if (Flag) t = null;
            return t;
        }
    }
}

namespace ColonyScale.Tests
{
    /// <summary>Optimizations that exist only for the offline fixtures; the real checks skip them.</summary>
    public interface IOfflineFixture { }

    public sealed class FixtureOptimization : Optimization, IOfflineFixture
    {
        public override string Id => "offlineFixture";
        public override string Label => "offline fixture";
        public override string Description => "";
        public List<PatchSpec> Patches = new List<PatchSpec>();
        public List<System.Reflection.MethodBase> Depends = new List<System.Reflection.MethodBase>();
        public int Cleared;
        public bool ThrowOnEnabled;
        public override IEnumerable<PatchSpec> GetPatches() => Patches;
        public override void OnEnabled() { if (ThrowOnEnabled) throw new InvalidOperationException("fixture OnEnabled throws"); }
        public override IEnumerable<System.Reflection.MethodBase> DependsOn => Depends;
        public override void ClearCaches() => Cleared++;
    }

    /// <summary>An optimization whose static initializer throws, as one resolving a renamed game field would.</summary>
    public sealed class BrokenInitFixture : Optimization, IOfflineFixture
    {
        private const string OptId = "offlineBrokenInit";
        private static readonly int Broken = Throw();
        private static int Throw() => throw new MissingFieldException("Fixture", "renamedField");

        public override string Id => OptId;
        public override string Label => "offline broken fixture";
        public override string Description => "";
        public override string Category => "Fixtures";
        public override IEnumerable<PatchSpec> GetPatches() { if (Broken == 0) yield break; }
    }

    /// <summary>
    /// Drives the real PatchManager against fixture methods: late foreign patches (F3), prefixes that sort after a
    /// replacing prefix (F4), an unpatch that Harmony fails to complete (F8), an exception while enabling (M1), an
    /// optimization whose static initializer throws (M2) and hook-only targets (L4).
    /// </summary>
    public static class PatchManagerTest
    {
        public const string ForeignId = "ColonyScale.OfflineCheck.Foreign";
        public static bool ForeignTranspilerArmed;

        [MethodImpl(MethodImplOptions.NoInlining)] public static int ReplacedTarget(int x) => x + 1;
        [MethodImpl(MethodImplOptions.NoInlining)] public static int ReplacedTarget2(int x) => x + 2;
        [MethodImpl(MethodImplOptions.NoInlining)] public static int DependedOn(int x) => x + 3;
        [MethodImpl(MethodImplOptions.NoInlining)] public static int TranspiledTarget(int x) => x + 4;
        [MethodImpl(MethodImplOptions.NoInlining)] public static int HookTarget(int x) => x + 5; // deliberately not fingerprinted

        public static bool OurReplacingPrefix() => false;
        public static IEnumerable<CodeInstruction> OurTranspiler(IEnumerable<CodeInstruction> instructions) => instructions;
        public static void ForeignPrefix() { }
        public static void ForeignPostfix() { }
        public static IEnumerable<CodeInstruction> ForeignTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            if (ForeignTranspilerArmed) throw new InvalidOperationException("foreign transpiler refuses the restored body");
            return instructions;
        }

        public static int Run(Action<string> fail)
        {
            int problems = 0;
            void Expect(bool ok, string what) { if (!ok) { problems++; fail(what); } }
            var sinks = (CSLog.MessageSink, CSLog.WarningSink, CSLog.ErrorSink);
            CSLog.MessageSink = CSLog.WarningSink = CSLog.ErrorSink = _ => { };
            var foreign = new Harmony(ForeignId);
            try
            {
                var replaced = Method(nameof(ReplacedTarget));
                var replaced2 = Method(nameof(ReplacedTarget2));
                var dependedOn = Method(nameof(DependedOn));
                var transpiled = Method(nameof(TranspiledTarget));
                var store = new FingerprintStore();
                var fixture = new FixtureOptimization();
                foreach (var m in new[] { replaced, replaced2, dependedOn, transpiled })
                    store.Set(fixture.Id, ILFingerprint.MemberId(m), ILFingerprint.Compute(m));
                PatchManager.Init(store);

                // F4: a foreign prefix that can sort after our replacing prefix is a conflict; one that surely runs first is not.
                fixture.Patches = new List<PatchSpec> { PatchSpec.For(replaced).WithPrefix(typeof(PatchManagerTest), nameof(OurReplacingPrefix), replacesOriginal: true, priority: Priority.Last) };
                void WithForeignPrefix(HarmonyMethod prefix, bool expectActive, string what)
                {
                    foreign.Patch(replaced, prefix: prefix);
                    bool on = PatchManager.TryEnable(fixture);
                    Expect(on == expectActive, $"{what}: expected {(expectActive ? "active" : "conflict")}, got {fixture.State} {fixture.StateReason}");
                    if (on) PatchManager.Disable(fixture, OptimizationState.DisabledByUser);
                    foreign.Unpatch(replaced, HarmonyPatchType.All, ForeignId);
                }
                WithForeignPrefix(Hm(nameof(ForeignPrefix), Priority.Last), false, "foreign prefix at the same priority");
                WithForeignPrefix(Hm(nameof(ForeignPrefix), Priority.First), true, "foreign prefix at a higher priority");
                var constrained = Hm(nameof(ForeignPrefix), Priority.First);
                constrained.after = new[] { PatchManager.HarmonyId };
                WithForeignPrefix(constrained, false, "foreign prefix ordered after ours");
                foreign.Patch(replaced, postfix: Hm(nameof(ForeignPostfix), Priority.Last));
                Expect(PatchManager.TryEnable(fixture), "a foreign postfix on a replaced body must be accepted: " + fixture.StateReason);
                PatchManager.Disable(fixture, OptimizationState.DisabledByUser);
                foreign.Unpatch(replaced, HarmonyPatchType.All, ForeignId);

                // F3: a foreign patch installed after enabling turns the active optimization off when re-checked.
                fixture.Patches = new List<PatchSpec> { PatchSpec.For(replaced2).WithPrefix(typeof(PatchManagerTest), nameof(OurReplacingPrefix), replacesOriginal: true, priority: Priority.Last) };
                fixture.Depends = new List<MethodBase> { dependedOn };
                Expect(PatchManager.TryEnable(fixture), "fixture should enable: " + fixture.StateReason);
                foreign.Patch(dependedOn, postfix: Hm(nameof(ForeignPostfix), Priority.Normal));
                int cleared = fixture.Cleared;
                Expect(!PatchManager.TryEnable(fixture) && fixture.State == OptimizationState.DisabledConflict,
                    "TryEnable on an active optimization must re-check dependencies, got " + fixture.State);
                Expect(PatchManager.HasNoPatchesOf(fixture) && fixture.Cleared == cleared + 1, "a late conflict must remove the patches and clear caches");
                foreign.Unpatch(dependedOn, HarmonyPatchType.All, ForeignId);
                Expect(PatchManager.TryEnable(fixture), "fixture should enable again: " + fixture.StateReason);
                PatchManager.All.Add(fixture);
                foreign.Patch(replaced2, prefix: Hm(nameof(ForeignPrefix), Priority.Last));
                Expect(PatchManager.RevalidateActive("in the offline fixture") == 1 && fixture.State == OptimizationState.DisabledConflict,
                    "revalidation must turn off an optimization whose replaced body gained a late foreign prefix, got " + fixture.State);
                PatchManager.All.Remove(fixture);
                foreign.Unpatch(replaced2, HarmonyPatchType.All, ForeignId);
                fixture.Depends = new List<MethodBase>();

                // F8: Harmony rebuilds the wrapper on unpatch; if a remaining foreign transpiler throws, our patch stays.
                fixture.Patches = new List<PatchSpec> { PatchSpec.For(transpiled).WithTranspiler(typeof(PatchManagerTest), nameof(OurTranspiler)) };
                foreign.Patch(transpiled, transpiler: Hm(nameof(ForeignTranspiler), Priority.Normal));
                Expect(PatchManager.TryEnable(fixture), "transpiler fixture should enable: " + fixture.StateReason);
                ForeignTranspilerArmed = true;
                cleared = fixture.Cleared;
                Expect(!PatchManager.Disable(fixture, OptimizationState.DisabledByUser), "a failed unpatch must not report success");
                Expect(fixture.State == OptimizationState.DisableFailed, "a failed unpatch must report DisableFailed, got " + fixture.State);
                Expect(fixture.Cleared == cleared, "caches must not be cleared while our patch is still installed");
                Expect(PatchManager.HasAnyPatchOf(fixture.Patches[0]), "fixture precondition: our transpiler is still installed");
                Expect(!PatchManager.TryEnable(fixture) && OwnPatchCount(transpiled) == 1, "must not re-apply while residual patches remain");
                ForeignTranspilerArmed = false;
                Expect(PatchManager.Disable(fixture, OptimizationState.DisabledByUser) && fixture.State == OptimizationState.DisabledByUser,
                    "retrying the disable once unpatching works must succeed, got " + fixture.State);
                Expect(PatchManager.HasNoPatchesOf(fixture) && fixture.Cleared == cleared + 1, "a successful retry must remove the patch and clear caches");

                // M1: an unexpected exception while enabling costs only that optimization, and leaves no patch behind.
                fixture.Patches = new List<PatchSpec> { PatchSpec.For(replaced).WithPrefix(typeof(PatchManagerTest), nameof(OurReplacingPrefix), replacesOriginal: true, priority: Priority.Last) };
                fixture.ThrowOnEnabled = true;
                Expect(!PatchManager.TryEnable(fixture) && fixture.State == OptimizationState.Failed && fixture.StateReason.Contains("fixture OnEnabled throws"),
                    "a throwing OnEnabled must report Failed with the reason, got " + fixture.State + " " + fixture.StateReason);
                Expect(PatchManager.HasNoPatchesOf(fixture), "a throwing OnEnabled must leave no patch installed");
                fixture.ThrowOnEnabled = false;
                Expect(PatchManager.HarmonyVersionProblem(new Version(2, 3, 6, 0)) != null, "Harmony 2.3.6 must be refused");
                Expect(PatchManager.HarmonyVersionProblem(new Version(2, 4, 0, 0)) == null, "Harmony 2.4.0 must be accepted");
                Expect(PatchManager.HarmonyVersionProblem(PatchManager.HarmonyVersion) == null, "the offline Harmony must be accepted: " + PatchManager.HarmonyVersion);

                // L4: a hook-only target is not fingerprinted; a hook-only spec may not replace its target.
                var hook = Method(nameof(HookTarget));
                fixture.Patches = new List<PatchSpec> { PatchSpec.For(hook).WithPostfix(typeof(PatchManagerTest), nameof(ForeignPostfix)).AsHook() };
                Expect(PatchManager.TryEnable(fixture), "a hook-only target needs no recorded fingerprint: " + fixture.StateReason);
                PatchManager.Disable(fixture, OptimizationState.DisabledByUser);
                fixture.Patches = new List<PatchSpec> { PatchSpec.For(hook).WithPostfix(typeof(PatchManagerTest), nameof(ForeignPostfix)) };
                Expect(!PatchManager.TryEnable(fixture) && fixture.State == OptimizationState.DisabledTargetChanged,
                    "a normal target without a recorded fingerprint must stay off, got " + fixture.State);
                fixture.Patches = new List<PatchSpec> { PatchSpec.For(hook).WithPrefix(typeof(PatchManagerTest), nameof(OurReplacingPrefix), replacesOriginal: true).AsHook() };
                Expect(!PatchManager.TryEnable(fixture) && fixture.State == OptimizationState.Failed && PatchManager.HasNoPatchesOf(fixture),
                    "a hook-only spec that replaces its target must be refused, got " + fixture.State);

                // M2: a type whose static initializer throws is discovered as a Failed stub under its real Id.
                var stub = PatchManager.All.OfType<UnavailableOptimization>().SingleOrDefault(o => o.Type == typeof(BrokenInitFixture));
                Expect(stub != null, "a throwing static initializer must yield an UnavailableOptimization");
                if (stub != null)
                {
                    Expect(stub.Id == "offlineBrokenInit" && stub.Label == "offline broken fixture" && stub.Category == "Fixtures",
                        $"the stub must report the real class's Id/Label/Category, got {stub.Id}/{stub.Label}/{stub.Category}");
                    Expect(stub.State == OptimizationState.Failed && stub.StateReason.Contains("renamedField"), "the stub must be Failed with the cause, got " + stub.State + " " + stub.StateReason);
                    Expect(!PatchManager.TryEnable(stub) && stub.State == OptimizationState.Failed && stub.StateReason.Contains("renamedField"),
                        "enabling the stub must fail and keep its reason, got " + stub.State + " " + stub.StateReason);
                }
            }
            catch (Exception e)
            {
                problems++;
                fail("threw " + e);
            }
            finally
            {
                ForeignTranspilerArmed = false;
                foreign.UnpatchAll(ForeignId);
                new Harmony(PatchManager.HarmonyId).UnpatchAll(PatchManager.HarmonyId);
                (CSLog.MessageSink, CSLog.WarningSink, CSLog.ErrorSink) = sinks;
            }
            return problems;
        }

        private static MethodInfo Method(string name) => typeof(PatchManagerTest).GetMethod(name);

        private static HarmonyMethod Hm(string name, int priority) => new HarmonyMethod(Method(name)) { priority = priority };

        private static int OwnPatchCount(MethodBase method)
        {
            var info = Harmony.GetPatchInfo(method);
            return info == null ? 0 : info.Transpilers.Count(p => p.owner == PatchManager.HarmonyId);
        }
    }
}
