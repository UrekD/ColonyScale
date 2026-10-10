using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale
{
    /// <summary>
    /// Exact replacements for List&lt;Thing&gt;.IndexOf/Remove/Contains.
    ///
    /// Vanilla semantics: List&lt;Thing&gt; compares through EqualityComparer&lt;Thing&gt;.Default, which for Thing (an
    /// IEquatable&lt;Thing&gt;) calls element.Equals(t) on each non-null element front to back and stops at the first
    /// match. Thing.Equals(Thing) is reference equality when the element is a mote and thingIDNumber + def equality
    /// otherwise; Thing does not override Equals(object), but that is irrelevant because the comparer uses the
    /// IEquatable implementation. Remove removes the first match, so with duplicates (the same reference twice, or two
    /// things sharing an id and def) the position removed matters for later iteration order.
    ///
    /// <see cref="IndexOf"/> is that scan with Thing.Equals inlined (no interface dispatch, one field read per element).
    /// The two-ended reference search is used only through <see cref="Remove(List{Thing}, Thing, int)"/>, when the
    /// caller has proven that at most one element of the list equals t: then the only possible match is t itself
    /// (found by reference) or an equal twin (not found by reference, so the exact scan runs).
    /// Every path records the chosen index against List.IndexOf in verify mode.
    /// </summary>
    public static class FastList
    {
        private static readonly AccessTools.FieldRef<List<Thing>, Thing[]> items = AccessTools.FieldRefAccess<List<Thing>, Thing[]>("_items");

        private static bool? vanillaEquality;

        /// <summary>
        /// False if any loaded Thing subclass re-implements IEquatable&lt;Thing&gt; or any ThingDef subclass overrides
        /// Equals(object): then the inlined equality may differ from what the comparer calls, and every operation
        /// goes through List&lt;Thing&gt; itself. Types do not change after startup, so this is computed once.
        /// </summary>
        public static bool VanillaEquality => vanillaEquality ?? (bool)(vanillaEquality = ComputeVanillaEquality());

        private static bool ComputeVanillaEquality()
        {
            try
            {
                foreach (Type type in typeof(Thing).AllSubclasses())
                {
                    if (type.ContainsGenericParameters) continue; // never instantiated as such
                    foreach (var m in type.GetInterfaceMap(typeof(IEquatable<Thing>)).TargetMethods)
                        if (m.DeclaringType != typeof(Thing)) return false;
                }
                // Thing.Equals compares defs with def.Equals(object).
                var defTypes = new List<Type>(typeof(ThingDef).AllSubclasses()) { typeof(ThingDef) };
                foreach (Type type in defTypes)
                {
                    if (type.ContainsGenericParameters) continue;
                    var eq = type.GetMethod(nameof(Equals), new[] { typeof(object) });
                    if (eq == null || eq.DeclaringType != typeof(object)) return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Exactly List&lt;Thing&gt;.IndexOf(t).</summary>
        public static int IndexOf(List<Thing> list, Thing t)
        {
            if (t == null || !VanillaEquality) return list.IndexOf(t);
            Thing[] arr = items(list);
            int n = list.Count;
            if (t.def.category == ThingCategory.Mote)
            {
                // An element with t's def is a mote too, so only t itself can match.
                for (int i = 0; i < n; i++)
                    if (ReferenceEquals(arr[i], t)) return i;
                return -1;
            }
            // e.Equals(t) for e != t: e is not a mote, same id, same def. e.def == t.def already rules out a mote e.
            int id = t.thingIDNumber;
            ThingDef def = t.def;
            for (int i = 0; i < n; i++)
            {
                Thing e = arr[i];
                if (ReferenceEquals(e, t) || (e != null && e.thingIDNumber == id && e.def == def)) return i;
            }
            return -1;
        }

        /// <summary>Exactly List&lt;Thing&gt;.Remove(t).</summary>
        public static bool Remove(List<Thing> list, Thing t) => RemoveAt(list, t, IndexOf(list, t));

        /// <summary>
        /// Exactly List&lt;Thing&gt;.Remove(t), given that at most <paramref name="atMost"/> elements of the list equal t
        /// (see <see cref="Occurrences"/>). 0 skips the scan; 1 tries the two-ended reference search first.
        /// </summary>
        public static bool Remove(List<Thing> list, Thing t, int atMost)
        {
            int i;
            _ = list.Count; // a null list throws here as it does in vanilla's List.Remove, also when atMost == 0
            if (t == null || !VanillaEquality || atMost > 1) i = IndexOf(list, t);
            else if (atMost == 0) i = -1;
            else
            {
                i = IndexOfRef(list, t);
                if (i < 0) i = IndexOf(list, t);
            }
            return RemoveAt(list, t, i);
        }

        /// <summary>Exactly List&lt;Thing&gt;.Contains(t).</summary>
        public static bool Contains(List<Thing> list, Thing t)
        {
            bool found = IndexOf(list, t) >= 0;
            if (Verify.Enabled) Verify.Record("fastList.contains", found == list.Contains(t));
            return found;
        }

        private static bool RemoveAt(List<Thing> list, Thing t, int i)
        {
            if (Verify.Enabled) Verify.Record("fastList.remove", i == list.IndexOf(t));
            if (i < 0) return false;
            list.RemoveAt(i);
            return true;
        }

        /// <summary>Some index holding t by reference, searching from both ends. Only meaningful if t occurs at most once.</summary>
        private static int IndexOfRef(List<Thing> list, Thing t)
        {
            Thing[] arr = items(list);
            int lo = 0, hi = list.Count - 1;
            while (lo <= hi)
            {
                if (ReferenceEquals(arr[hi], t)) return hi;
                if (ReferenceEquals(arr[lo], t)) return lo;
                lo++;
                hi--;
            }
            return -1;
        }
    }

    /// <summary>
    /// How many things of each Thing.Equals class (a mote by reference, anything else by thingIDNumber + def) a
    /// caller has put into a list or set of lists and not yet taken out. Correct only while the caller sees every
    /// mutation of those lists; owners drop it whenever something else may have changed them.
    /// </summary>
    public sealed class Occurrences
    {
        private readonly Dictionary<Thing, int> counts = new Dictionary<Thing, int>(EqualsClass.Instance);

        public int Count(Thing t) => t != null && counts.TryGetValue(t, out int c) ? c : 0;

        public void Added(Thing t)
        {
            if (t != null) counts[t] = Count(t) + 1;
        }

        /// <summary>One element equal to t left; no-op if none was counted.</summary>
        public void Removed(Thing t)
        {
            int c = Count(t);
            if (c > 1) counts[t] = c - 1;
            else if (c == 1) counts.Remove(t);
        }

        /// <summary>Raises each class's count to at least its number of elements in <paramref name="list"/>.</summary>
        public void CoverList(List<Thing> list, Dictionary<Thing, int> scratch)
        {
            scratch.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                Thing e = list[i];
                if (e == null) continue;
                scratch.TryGetValue(e, out int c);
                scratch[e] = c + 1;
            }
            foreach (var kv in scratch)
                if (Count(kv.Key) < kv.Value) counts[kv.Key] = kv.Value;
            scratch.Clear();
        }

        public static Dictionary<Thing, int> NewScratch() => new Dictionary<Thing, int>(EqualsClass.Instance);

        /// <summary>Thing.Equals as an equivalence: same reference, or both non-motes with the same id and def.</summary>
        private sealed class EqualsClass : IEqualityComparer<Thing>
        {
            public static readonly EqualsClass Instance = new EqualsClass();

            public bool Equals(Thing a, Thing b) =>
                ReferenceEquals(a, b) || (a.thingIDNumber == b.thingIDNumber && a.def == b.def && a.def.category != ThingCategory.Mote);

            public int GetHashCode(Thing t) =>
                t.def.category == ThingCategory.Mote ? RuntimeHelpers.GetHashCode(t) : t.thingIDNumber;
        }
    }

    /// <summary>
    /// <see cref="Occurrences"/> per List&lt;Thing&gt;, for lists whose every Add/Remove goes through
    /// <see cref="Add"/>/<see cref="Remove"/>. Small lists are not tracked (the exact scan is as cheap); a list gets
    /// counts the first time a removal finds it at <see cref="MinTracked"/> elements, counted from its contents.
    /// </summary>
    public sealed class TrackedLists
    {
        public const int MinTracked = 32;

        private ConditionalWeakTable<List<Thing>, Occurrences> table = new ConditionalWeakTable<List<Thing>, Occurrences>();
        private readonly Dictionary<Thing, int> scratch = Occurrences.NewScratch();

        public void Clear() => table = new ConditionalWeakTable<List<Thing>, Occurrences>();

        /// <summary>Call before anything else mutates the list.</summary>
        public void Forget(List<Thing> list) => table.Remove(list);

        public void Add(List<Thing> list, Thing t)
        {
            list.Add(t);
            if (table.TryGetValue(list, out var occ)) occ.Added(t);
        }

        public bool Remove(List<Thing> list, Thing t)
        {
            if (!table.TryGetValue(list, out var occ))
            {
                if (list.Count < MinTracked || !FastList.VanillaEquality) return FastList.Remove(list, t);
                occ = new Occurrences();
                occ.CoverList(list, scratch);
                table.Add(list, occ);
            }
            if (!FastList.Remove(list, t, occ.Count(t))) return false;
            occ.Removed(t);
            return true;
        }
    }
}
