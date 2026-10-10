using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyScale.Optimizations.Frame
{
    /// <summary>
    /// SilhouetteUtility.GetCachedSilhouetteData looks up a material cache keyed by a private struct that overrides
    /// GetHashCode but not Equals, so the dictionary's default comparer boxes both keys and compares them with
    /// reflection-based ValueType.Equals: allocations on every lookup, i.e. per visible pawn per frame with
    /// silhouettes on. This keeps vanilla's dictionary as the only store and maintains a mirror index of it with a
    /// non-boxing comparer; hits are answered from the mirror, misses and clears run vanilla, and the mirror is
    /// resynced from vanilla's dictionary afterwards.
    ///
    /// Exact: the key's instance fields are checked to be exactly thingDef, lifeStageDef, graphicIndex, gender and
    /// rotMode, built here by the same expressions as vanilla's two constructors. ValueType.Equals compares the
    /// int and byte-enum fields by value and the reference fields with null checks and virtual Equals(object), which
    /// the comparer does the same way (in the same direction, stored key first). The mirror holds the very Material
    /// instances in vanilla's entries, and vanilla still does every insert, the clear-without-Dispose above 512
    /// entries and NotifyGraphicDirty's removal, so contents and clearing points are vanilla's; a count mismatch
    /// rebuilds the mirror. Rotation and the mesh are read as vanilla reads them. Only main-thread calls are served.
    /// </summary>
    public sealed class SilhouetteCache : Optimization
    {
        private const string OptId = "silhouetteCache";
        private const int VanillaMaxEntries = 512;

        public override string Id => OptId;
        public override string Label => "Faster silhouette lookups";
        public override string Description =>
            "Pawn silhouettes (shown when zoomed out) find their material without allocating memory for every pawn every frame.";
        public override string Category => "Frame";

        private static Type keyType, valueType;
        private static FieldInfo cacheField;
        private static MethodInfo getKeyMethod;
        private static FieldInfo kThingDef, kLifeStageDef, kGraphicIndex, kGender, kRotMode;
        private static AccessTools.FieldRef<object, Material> eastRef, westRef;

        private static readonly (string name, Type type)[] ExpectedKeyFields =
        {
            ("thingDef", typeof(ThingDef)),
            ("lifeStageDef", typeof(LifeStageDef)),
            ("graphicIndex", typeof(int)),
            ("gender", typeof(Gender)),
            ("rotMode", typeof(RotStage)),
        };

        private static void Resolve()
        {
            keyType = AccessTools.Inner(typeof(SilhouetteUtility), "SilhouetteCacheKey") ?? throw new MissingMemberException(nameof(SilhouetteUtility), "SilhouetteCacheKey");
            valueType = AccessTools.Inner(typeof(SilhouetteUtility), "SilhouetteCacheValue") ?? throw new MissingMemberException(nameof(SilhouetteUtility), "SilhouetteCacheValue");
            var fields = keyType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fields.Length != ExpectedKeyFields.Length || ExpectedKeyFields.Any(e => !fields.Any(f => f.Name == e.name && f.FieldType == e.type)))
                throw new MissingMemberException("SilhouetteCacheKey fields changed: " + string.Join(", ", fields.Select(f => f.FieldType.Name + " " + f.Name)));
            if (keyType.GetMethod(nameof(Equals), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, new[] { typeof(object) }, null) != null
                || typeof(IEquatable<>).MakeGenericType(keyType).IsAssignableFrom(keyType))
                throw new MissingMemberException("SilhouetteCacheKey now defines its own equality");
            kThingDef = AccessTools.Field(keyType, "thingDef");
            kLifeStageDef = AccessTools.Field(keyType, "lifeStageDef");
            kGraphicIndex = AccessTools.Field(keyType, "graphicIndex");
            kGender = AccessTools.Field(keyType, "gender");
            kRotMode = AccessTools.Field(keyType, "rotMode");
            cacheField = AccessTools.Field(typeof(SilhouetteUtility), "materialCache");
            if (cacheField == null || cacheField.FieldType != typeof(Dictionary<,>).MakeGenericType(keyType, valueType))
                throw new MissingFieldException(nameof(SilhouetteUtility), "materialCache");
            getKeyMethod = AccessTools.Method(typeof(SilhouetteUtility), "GetSilhouetteCacheKey") ?? throw new MissingMethodException(nameof(SilhouetteUtility), "GetSilhouetteCacheKey");
            eastRef = AccessTools.FieldRefAccess<Material>(valueType, "east");
            westRef = AccessTools.FieldRefAccess<Material>(valueType, "west");
        }

        public override IEnumerable<PatchSpec> GetPatches()
        {
            Resolve();
            yield return PatchSpec.For(AccessTools.Method(typeof(SilhouetteUtility), "GetCachedSilhouetteData"))
                .WithPrefix(typeof(SilhouetteCache), nameof(Prefix), replacesOriginal: true, priority: Priority.Last)
                .WithPostfix(typeof(SilhouetteCache), nameof(Postfix), priority: Priority.First);
            yield return PatchSpec.For(AccessTools.Method(typeof(SilhouetteUtility), nameof(SilhouetteUtility.NotifyGraphicDirty)))
                .WithPostfix(typeof(SilhouetteCache), nameof(NotifyGraphicDirtyPostfix), priority: Priority.First);
        }

        public override IEnumerable<MethodBase> DependsOn
        {
            get
            {
                if (keyType == null) Resolve();
                return new MethodBase[]
                {
                    getKeyMethod,
                    AccessTools.Constructor(keyType, new[] { typeof(Pawn) }),
                    AccessTools.Constructor(keyType, new[] { typeof(Thing) }),
                    typeof(SilhouetteUtility).TypeInitializer,
                };
            }
        }

        public override void OnEnabled()
        {
            vanillaCache = null;
            mirror.Clear();
            var dict = cacheField.GetValue(null);
            // Only mirror vanilla's own dictionary with the default comparer (what the equality argument is about).
            var comparer = dict?.GetType().GetProperty("Comparer")?.GetValue(dict);
            var defaultComparer = typeof(EqualityComparer<>).MakeGenericType(keyType).GetProperty("Default")?.GetValue(null);
            if (comparer != null && ReferenceEquals(comparer, defaultComparer)) vanillaCache = (IDictionary)dict;
            else CSLog.Warning($"{OptId}: silhouette cache is not vanilla's, leaving it alone");
        }

        public override void ClearCaches()
        {
            vanillaCache = null;
            mirror.Clear();
        }

        private readonly struct Key
        {
            public readonly ThingDef thingDef;
            public readonly LifeStageDef lifeStageDef;
            public readonly int graphicIndex;
            public readonly Gender gender;
            public readonly RotStage rotMode;

            public Key(ThingDef thingDef, LifeStageDef lifeStageDef, int graphicIndex, Gender gender, RotStage rotMode)
            {
                this.thingDef = thingDef;
                this.lifeStageDef = lifeStageDef;
                this.graphicIndex = graphicIndex;
                this.gender = gender;
                this.rotMode = rotMode;
            }
        }

        /// <summary>ValueType.Equals for the key's fields; vanilla's GetHashCode formula.</summary>
        private sealed class KeyComparer : IEqualityComparer<Key>
        {
            public bool Equals(Key x, Key y) =>
                Same(x.thingDef, y.thingDef) && Same(x.lifeStageDef, y.lifeStageDef)
                && x.graphicIndex == y.graphicIndex && x.gender == y.gender && x.rotMode == y.rotMode;

            private static bool Same(object a, object b) => a == null ? b == null : a.Equals(b);

            public int GetHashCode(Key k)
            {
                int seed = k.thingDef.GetHashCode();
                if (k.lifeStageDef != null) seed = Gen.HashCombineInt(seed, k.lifeStageDef.GetHashCode());
                seed = Gen.HashCombineInt(seed, k.graphicIndex);
                seed = Gen.HashCombineInt(seed, (int)k.gender);
                return Gen.HashCombineInt(seed, (int)k.rotMode);
            }
        }

        private sealed class Entry
        {
            public Material east, west;
        }

        private static IDictionary vanillaCache;
        private static readonly Dictionary<Key, Entry> mirror = new Dictionary<Key, Entry>(VanillaMaxEntries + 1, new KeyComparer());

        /// <summary>Same expressions, same order as SilhouetteCacheKey's constructors (via GetSilhouetteCacheKey).</summary>
        private static Key MakeKey(Thing thing)
        {
            if (thing is Pawn pawn)
                return new Key(pawn.def, pawn.ageTracker.CurLifeStage, pawn.GetGraphicIndex(), pawn.gender, pawn.mutant?.rotStage ?? RotStage.Fresh);
            return new Key(thing.def, null, thing.OverrideGraphicIndex ?? (-1), Gender.None, RotStage.Fresh);
        }

        private static Key KeyFromBoxed(object boxed) => new Key(
            (ThingDef)kThingDef.GetValue(boxed), (LifeStageDef)kLifeStageDef.GetValue(boxed), (int)kGraphicIndex.GetValue(boxed),
            (Gender)kGender.GetValue(boxed), (RotStage)kRotMode.GetValue(boxed));

        private static Entry EntryFromValue(object value) => new Entry { east = eastRef(value), west = westRef(value) };

        private static void Rebuild()
        {
            mirror.Clear();
            foreach (DictionaryEntry e in vanillaCache) mirror[KeyFromBoxed(e.Key)] = EntryFromValue(e.Value);
        }

        public struct CallState
        {
            public bool resync;   // vanilla ran on a miss or a clear
            public bool verify;
            public Mesh mesh;
            public Material material;
        }

        public static bool Prefix(Thing thing, ref ValueTuple<Mesh, Material> __result, out CallState __state)
        {
            __state = default;
            var cache = vanillaCache;
            if (cache == null || !UnityData.IsInMainThread) return true;
            int count = cache.Count;
            if (count > VanillaMaxEntries || mirror.Count != count)
            {
                __state.resync = true; // vanilla clears (or we lost track); rebuild after it ran
                return true;
            }
            Key key;
            try { key = MakeKey(thing); }
            catch (Exception) { return true; } // vanilla throws the same way
            if (!mirror.TryGetValue(key, out Entry entry))
            {
                __state.resync = true;
                return true;
            }
            ValueTuple<Mesh, Material> fast = thing.Rotation == Rot4.West
                ? (MeshPool.GridPlaneFlip(Vector2.one), entry.west)
                : (MeshPool.GridPlane(Vector2.one), entry.east);
            if (Verify.Enabled)
            {
                __state.verify = true;
                __state.mesh = fast.Item1;
                __state.material = fast.Item2;
                return true;
            }
            __result = fast;
            return false;
        }

        public static void Postfix(Thing thing, ValueTuple<Mesh, Material> __result, CallState __state)
        {
            if (__state.verify)
            {
                Verify.Record(OptId, ReferenceEquals(__result.Item1, __state.mesh) && ReferenceEquals(__result.Item2, __state.material));
                return;
            }
            if (!__state.resync || vanillaCache == null) return;
            int count = vanillaCache.Count;
            if (count == mirror.Count + 1)
            {
                // The usual miss: vanilla added exactly this thing's entry.
                try
                {
                    object boxed = getKeyMethod.Invoke(null, new object[] { thing });
                    object value = vanillaCache[boxed];
                    if (value != null)
                    {
                        mirror[KeyFromBoxed(boxed)] = EntryFromValue(value);
                        if (mirror.Count == count) return;
                    }
                }
                catch (Exception) { } // fall through to a full rebuild
            }
            Rebuild();
        }

        public static void NotifyGraphicDirtyPostfix(Thing thing)
        {
            if (vanillaCache == null || !UnityData.IsInMainThread) return;
            try { mirror.Remove(MakeKey(thing)); }
            catch (Exception) { mirror.Clear(); } // count mismatch forces a rebuild on the next lookup
        }
    }
}
