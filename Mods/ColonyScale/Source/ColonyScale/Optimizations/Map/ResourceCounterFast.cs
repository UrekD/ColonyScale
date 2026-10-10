using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale.Optimizations.Map
{
    /// <summary>
    /// ResourceCounter.UpdateResourceCounts recounts every stack in every stockpile and shelf. It runs every 204 ticks
    /// and also on every pick-up, drop and container transfer of a counted item (Pawn_CarryTracker, ThingOwner,
    /// Toils_Recipe), so on a big base it runs many times per second. Vanilla walks SlotGroup.HeldThings (one iterator
    /// object per group), tests ThingDef.EverStorable(false) per thing (a reflection IsAssignableFrom call), and does
    /// two dictionary lookups per counted stack. This walks the same cells in the same order, caches the
    /// IsAssignableFrom part per thingClass, and adds stack counts into a per-def array that is written into the
    /// dictionary once per def at the end.
    ///
    /// Exact:
    /// - ResetResourceCounts is vanilla's own method, so the dictionary is cleared and refilled in the same key order;
    ///   afterwards only values of existing keys are set, which never changes Dictionary iteration order.
    /// - The loop is HeldThings' iterator inlined (CellsList, then parent.Map, then ThingsListAt per cell, same Count
    ///   re-reads). EverStorable(false) is IsAssignableFrom(thingClass) || (categories non-empty and category == Item);
    ///   only the first term is cached, keyed by the thingClass reference, and it is a pure function of that type.
    /// - GetInnerIfMinified, CountAsResource and vanilla's private ShouldCount are called for the same things in the
    ///   same order, so fog/rot checks (and any patches on them) see identical calls.
    /// - Int addition wraps mod 2^32, so summing per def first gives the same values. The first stack of a def in a
    ///   pass is added with vanilla's own `dict[def] += n` (KeyNotFoundException at exactly the same stack if a counted
    ///   def has no key), and pending sums are flushed in a finally block, so even after an exception the dictionary
    ///   holds what vanilla would have written up to that point.
    /// - Nothing in the scan reads countedAmounts (ShouldCount is fingerprinted and must be unpatched). If
    ///   UpdateResourceCounts is re-entered anyway, pending sums are flushed first and vanilla runs.
    /// Main thread only.
    /// </summary>
    public sealed class ResourceCounterFast : Optimization
    {
        public override string Id => "resourceCounter";
        public override string Label => "Faster resource counting";
        public override string Description =>
            "The resource readout recount, which runs whenever a pawn picks up, drops or loads a counted item, walks " +
            "stockpile cells directly and adds stacks per item type instead of updating a lookup table per stack.";
        public override string Category => "Map";

        private static readonly AccessTools.FieldRef<ResourceCounter, Verse.Map> mapField =
            AccessTools.FieldRefAccess<ResourceCounter, Verse.Map>("map");
        private static readonly AccessTools.FieldRef<ResourceCounter, Dictionary<ThingDef, int>> countedAmounts =
            AccessTools.FieldRefAccess<ResourceCounter, Dictionary<ThingDef, int>>("countedAmounts");

        private static Action<ResourceCounter> resetResourceCounts;
        private static Func<ResourceCounter, Thing, bool> shouldCount;

        public override IEnumerable<PatchSpec> GetPatches()
        {
            resetResourceCounts = AccessTools.MethodDelegate<Action<ResourceCounter>>(
                AccessTools.Method(typeof(ResourceCounter), "ResetResourceCounts"));
            shouldCount = AccessTools.MethodDelegate<Func<ResourceCounter, Thing, bool>>(
                AccessTools.Method(typeof(ResourceCounter), "ShouldCount"));

            yield return PatchSpec.For(AccessTools.Method(typeof(ResourceCounter), nameof(ResourceCounter.UpdateResourceCounts)))
                .WithPrefix(typeof(ResourceCounterFast), nameof(Prefix), replacesOriginal: true, priority: Priority.Last)
                .WithPostfix(typeof(ResourceCounterFast), nameof(Postfix), priority: Priority.First);
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            // The loop below is this iterator (and SlotGroup.Map) inlined.
            AccessTools.EnumeratorMoveNext(AccessTools.PropertyGetter(typeof(SlotGroup), nameof(SlotGroup.HeldThings))),
            AccessTools.PropertyGetter(typeof(SlotGroup), "Map"),
            AccessTools.Method(typeof(ThingDef), nameof(ThingDef.EverStorable)),
            // Must not read countedAmounts mid-count (we defer writes).
            AccessTools.Method(typeof(ResourceCounter), "ShouldCount"),
        };

        public override void ClearCaches()
        {
            classKnown = new bool[0];
            classOf = new Type[0];
            classIsMinified = new bool[0];
            seenPass = new int[0];
            seenDef = new ThingDef[0];
            sums = new int[0];
            touched.Clear();
            pass = 1;
        }

        // ── EverStorable(false) ──────────────────────────────

        private static bool[] classKnown = new bool[0];
        private static Type[] classOf = new Type[0];
        private static bool[] classIsMinified = new bool[0];

        /// <summary>ThingDef.EverStorable(willMinifyIfPossible: false) with the IsAssignableFrom result cached per thingClass.</summary>
        private static bool EverStorableNoMinify(ThingDef def)
        {
            int idx = def.index;
            Type thingClass = def.thingClass;
            bool minified;
            if (idx < classKnown.Length && classKnown[idx] && ReferenceEquals(classOf[idx], thingClass))
            {
                minified = classIsMinified[idx];
            }
            else
            {
                minified = typeof(MinifiedThing).IsAssignableFrom(thingClass);
                if (idx >= classKnown.Length)
                {
                    int size = Math.Max(idx + 1, DefDatabase<ThingDef>.DefCount + 16);
                    Array.Resize(ref classKnown, size);
                    Array.Resize(ref classOf, size);
                    Array.Resize(ref classIsMinified, size);
                }
                classKnown[idx] = true;
                classOf[idx] = thingClass;
                classIsMinified[idx] = minified;
            }
            if (minified) return true;
            // willMinifyIfPossible is false, so vanilla never evaluates Minifiable.
            return !def.thingCategories.NullOrEmpty() && def.category == ThingCategory.Item;
        }

        // ── Per-def sums ────────────────────────────────────

        private static int[] seenPass = new int[0];
        private static ThingDef[] seenDef = new ThingDef[0];
        private static int[] sums = new int[0];
        private static readonly List<int> touched = new List<int>();
        private static int pass = 1;
        private static Dictionary<ThingDef, int> pending;
        private static bool running;

        private static void Add(Dictionary<ThingDef, int> dict, ThingDef def, int count)
        {
            int idx = def.index;
            if (idx < seenPass.Length && seenPass[idx] == pass)
            {
                if (ReferenceEquals(seenDef[idx], def)) sums[idx] += count;
                else dict[def] += count; // another def with the same index (not in DefDatabase): plain vanilla update
                return;
            }
            dict[def] += count; // first stack of this def in this pass: vanilla's own update, including its exception
            if (idx >= seenPass.Length)
            {
                int size = Math.Max(idx + 1, DefDatabase<ThingDef>.DefCount + 16);
                Array.Resize(ref seenPass, size);
                Array.Resize(ref seenDef, size);
                Array.Resize(ref sums, size);
            }
            seenPass[idx] = pass;
            seenDef[idx] = def;
            sums[idx] = 0;
            touched.Add(idx);
        }

        /// <summary>Writes pending per-def sums into the dictionary being counted and starts a new pass.</summary>
        private static void Flush()
        {
            if (pending != null)
            {
                for (int k = 0; k < touched.Count; k++)
                {
                    int idx = touched[k];
                    if (sums[idx] != 0) pending[seenDef[idx]] += sums[idx];
                }
            }
            touched.Clear();
            if (++pass == int.MaxValue)
            {
                Array.Clear(seenPass, 0, seenPass.Length);
                pass = 1;
            }
        }

        private static void Count(ResourceCounter counter)
        {
            resetResourceCounts(counter);
            Dictionary<ThingDef, int> dict = countedAmounts(counter);
            running = true;
            pending = dict;
            try
            {
                List<SlotGroup> groups = mapField(counter).haulDestinationManager.AllGroupsListForReading;
                for (int i = 0; i < groups.Count; i++)
                {
                    SlotGroup group = groups[i];
                    List<IntVec3> cells = group.CellsList;
                    Verse.Map map = group.parent.Map;
                    for (int c = 0; c < cells.Count; c++)
                    {
                        List<Thing> things = map.thingGrid.ThingsListAt(cells[c]);
                        for (int j = 0; j < things.Count; j++)
                        {
                            Thing thing = things[j];
                            if (!EverStorableNoMinify(thing.def)) continue;
                            Thing inner = thing.GetInnerIfMinified();
                            if (inner.def.CountAsResource && shouldCount(counter, inner))
                                Add(dict, inner.def, inner.stackCount);
                        }
                    }
                }
            }
            finally
            {
                Flush();
                pending = null;
                running = false;
            }
        }

        public static bool Prefix(ResourceCounter __instance, out List<KeyValuePair<ThingDef, int>> __state)
        {
            __state = null;
            if (running)
            {
                // Re-entered from inside a count: vanilla would already have added everything so far.
                Flush();
                return true;
            }
            Count(__instance);
            if (Verify.Enabled)
            {
                __state = new List<KeyValuePair<ThingDef, int>>(countedAmounts(__instance));
                return true; // vanilla recounts; the postfix compares keys, order and values
            }
            return false;
        }

        public static void Postfix(ResourceCounter __instance, List<KeyValuePair<ThingDef, int>> __state)
        {
            if (__state == null) return;
            var dict = countedAmounts(__instance);
            bool equal = dict.Count == __state.Count;
            if (equal)
            {
                int i = 0;
                foreach (var kv in dict)
                {
                    if (!ReferenceEquals(kv.Key, __state[i].Key) || kv.Value != __state[i].Value) { equal = false; break; }
                    i++;
                }
            }
            Verify.Record("resourceCounter", equal);
        }
    }
}
