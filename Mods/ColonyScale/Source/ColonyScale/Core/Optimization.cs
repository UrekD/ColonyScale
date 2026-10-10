using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ColonyScale
{
    public enum OptimizationState
    {
        NotApplied,
        Active,
        DisabledByUser,
        DisabledTargetChanged,
        DisabledConflict,
        Failed,
        /// <summary>Removing its patches failed; some of them may still be installed.</summary>
        DisableFailed,
    }

    /// <summary>
    /// One self-contained optimization: a set of Harmony patches that are applied and removed together.
    /// Subclasses must be stateless apart from caches they clear in <see cref="ClearCaches"/>.
    /// </summary>
    public abstract class Optimization
    {
        /// <summary>Stable key used for settings and fingerprints. Never rename once shipped.</summary>
        public abstract string Id { get; }
        public abstract string Label { get; }
        public abstract string Description { get; }
        public virtual string Category => "General";
        public virtual bool EnabledByDefault => true;

        /// <summary>Package ids of mods that already optimize the same code; if one is active this optimization stays off.</summary>
        public virtual IEnumerable<string> IncompatiblePackageIds => Enumerable.Empty<string>();

        /// <summary>Resolve targets and build patch methods. Throw if a target cannot be found.</summary>
        public abstract IEnumerable<PatchSpec> GetPatches();

        /// <summary>
        /// Game methods (besides the patch targets) whose current code this optimization's correctness relies on.
        /// They are fingerprinted like targets, and if another mod patches one of them the optimization stays off.
        /// </summary>
        public virtual IEnumerable<MethodBase> DependsOn => Enumerable.Empty<MethodBase>();

        /// <summary>Called after the patches are applied (live toggling included).</summary>
        public virtual void OnEnabled() { }

        /// <summary>Called after the patches are removed. Drop every cache so a later re-enable starts clean.</summary>
        public virtual void ClearCaches() { }

        public OptimizationState State { get; internal set; } = OptimizationState.NotApplied;
        public string StateReason { get; internal set; }
        internal readonly List<PatchSpec> applied = new List<PatchSpec>();

        public bool IsActive => State == OptimizationState.Active;
    }

    public sealed class PatchSpec
    {
        public MethodBase Target;
        public HarmonyMethod Prefix;
        public HarmonyMethod Postfix;
        public HarmonyMethod Transpiler;

        /// <summary>True when the prefix returns false and fully replaces the original method body.</summary>
        public bool ReplacesOriginal;

        /// <summary>
        /// The target only marks a point in time (a cache reset, a flush); its body is not part of any correctness
        /// argument, so it is not fingerprinted and a game update that changes it leaves the optimization on.
        /// Allowed for postfixes and non-replacing prefixes only. A method whose body does matter belongs in a normal
        /// spec or in DependsOn, which are still fingerprinted.
        /// </summary>
        public bool HookOnly;

        public static PatchSpec For(MethodBase target)
        {
            if (target == null) throw new MissingMethodException("patch target not found");
            return new PatchSpec { Target = target };
        }

        public PatchSpec WithPrefix(Type type, string method, bool replacesOriginal = false, int priority = -1)
        {
            Prefix = Make(type, method, priority);
            ReplacesOriginal = replacesOriginal;
            return this;
        }

        public PatchSpec WithPostfix(Type type, string method, int priority = -1)
        {
            Postfix = Make(type, method, priority);
            return this;
        }

        public PatchSpec WithTranspiler(Type type, string method)
        {
            Transpiler = Make(type, method, -1);
            return this;
        }

        /// <summary>Marks the target as <see cref="HookOnly"/>.</summary>
        public PatchSpec AsHook()
        {
            HookOnly = true;
            return this;
        }

        /// <summary>Null when the spec is well-formed, else why not.</summary>
        public string Problem() =>
            HookOnly && (Transpiler != null || ReplacesOriginal)
                ? "hook-only patch on " + Target.DeclaringType?.Name + "." + Target.Name + " must not transpile or replace it"
                : null;

        /// <summary>
        /// The methods whose IL is fingerprinted for an optimization: every target except hook-only ones, plus DependsOn.
        /// Shared by the in-game check and the offline fingerprint writer and checker.
        /// </summary>
        public static IEnumerable<MethodBase> Fingerprinted(IEnumerable<PatchSpec> patches, IEnumerable<MethodBase> dependsOn) =>
            patches.Where(p => !p.HookOnly).Select(p => p.Target).Concat(dependsOn).Distinct();

        private static HarmonyMethod Make(Type type, string method, int priority)
        {
            var info = AccessTools.Method(type, method);
            if (info == null) throw new MissingMethodException(type.FullName, method);
            var hm = new HarmonyMethod(info);
            if (priority >= 0) hm.priority = priority;
            return hm;
        }

        public IEnumerable<MethodInfo> PatchMethods()
        {
            if (Prefix != null) yield return Prefix.method;
            if (Postfix != null) yield return Postfix.method;
            if (Transpiler != null) yield return Transpiler.method;
        }
    }

    /// <summary>Thrown by transpilers when the instruction pattern they rewrite is not present.</summary>
    public sealed class PatternNotFoundException : Exception
    {
        public PatternNotFoundException(string optimizationId, string what)
            : base($"[{optimizationId}] IL pattern not found: {what}") { }
    }

    /// <summary>
    /// Stands in for an optimization whose type could not be created, typically because a static field initializer
    /// resolves a game member that an update renamed. It reports <see cref="OptimizationState.Failed"/> with the reason
    /// and is never applied. Id, label, description and category are read from the real class's property getters
    /// without running any of its code (a getter whose body is a single constant), so the settings key stays the one
    /// the working class uses; a getter of any other shape falls back to the type name.
    /// </summary>
    public sealed class UnavailableOptimization : Optimization
    {
        public readonly Type Type;
        private readonly string id, label, description, category;

        internal UnavailableOptimization(Type type, string reason)
        {
            Type = type;
            id = ConstantString(type, nameof(Id)) ?? type.Name;
            label = ConstantString(type, nameof(Label)) ?? type.Name;
            description = ConstantString(type, nameof(Description)) ?? "";
            category = ConstantString(type, nameof(Category)) ?? base.Category;
            State = OptimizationState.Failed;
            StateReason = reason;
        }

        public override string Id => id;
        public override string Label => label;
        public override string Description => description;
        public override string Category => category;
        public override IEnumerable<PatchSpec> GetPatches() => throw new InvalidOperationException(StateReason);

        /// <summary>The string a property getter returns when its IL is exactly <c>ldstr; ret</c>, else null.</summary>
        private static string ConstantString(Type type, string property)
        {
            try
            {
                var getter = type.GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                var il = getter?.GetMethodBody()?.GetILAsByteArray();
                if (il == null || il.Length != 6 || il[0] != 0x72 || il[5] != 0x2a) return null;
                return getter.Module.ResolveString(BitConverter.ToInt32(il, 1));
            }
            catch (Exception) { return null; }
        }
    }

    public static class OptimizationRegistry
    {
        /// <summary>
        /// All optimizations in this assembly, ordered for display. A type that cannot be created becomes an
        /// <see cref="UnavailableOptimization"/>, so one broken optimization never takes the others down.
        /// </summary>
        public static List<Optimization> Discover()
        {
            Type[] types;
            try { types = typeof(Optimization).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            return types
                .Where(t => !t.IsAbstract && typeof(Optimization).IsAssignableFrom(t) && t != typeof(UnavailableOptimization))
                .Select(Create)
                .OrderBy(o => o.Category).ThenBy(o => o.Label)
                .ToList();
        }

        /// <summary>
        /// Runs the type's static initializers explicitly (they resolve game members by name), so a failure surfaces
        /// here and not, depending on the runtime's type-initialization timing, inside a patch at play time.
        /// </summary>
        private static Optimization Create(Type type)
        {
            try
            {
                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                return (Optimization)Activator.CreateInstance(type);
            }
            catch (Exception e)
            {
                while ((e is TypeInitializationException || e is TargetInvocationException) && e.InnerException != null) e = e.InnerException;
                var stub = new UnavailableOptimization(type, "could not be created: " + e.Message);
                CSLog.Warning($"{stub.Label} is off: {stub.StateReason}");
                return stub;
            }
        }
    }
}
