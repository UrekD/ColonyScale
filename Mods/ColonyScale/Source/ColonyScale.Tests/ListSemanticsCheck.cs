using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Verse;

namespace ColonyScale.Tests
{
    /// <summary>
    /// Differential check of <see cref="FastList"/> and <see cref="TrackedLists"/> against List&lt;Thing&gt; itself, on
    /// the states vanilla can reach: the same reference twice (TickList registers before it deregisters), distinct
    /// things equal by id and def, motes (reference equality) and nulls. Things are plain objects; nothing is spawned.
    /// </summary>
    public static class ListSemanticsCheck
    {
        public static int Run()
        {
            int problems = 0;
            void Fail(string msg) { problems++; Console.WriteLine("FAIL  list semantics: " + msg); }

            if (!FastList.VanillaEquality) Fail("FastList.VanillaEquality is false for the game's own types");

            // Constructors touch Unity; only the fields Thing.Equals reads are needed.
            ThingDef Def(ThingCategory category)
            {
                var def = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef));
                def.category = category;
                return def;
            }
            Thing Make(ThingDef def, int id)
            {
                var t = (Thing)RuntimeHelpers.GetUninitializedObject(typeof(Thing));
                t.def = def;
                t.thingIDNumber = id;
                return t;
            }
            var item = Def(ThingCategory.Item);
            var mote = Def(ThingCategory.Mote);

            // Vanilla removes the earlier copy: [t, b, t] -> [b, t]. Padded so TrackedLists counts the list.
            {
                var t = Make(item, 1);
                var b = Make(item, 2);
                var fill = Enumerable.Range(100, TrackedLists.MinTracked).Select(i => Make(item, i)).ToList();
                var start = new List<Thing> { t, b };
                start.AddRange(fill);
                start.Add(t);

                var plain = new List<Thing>(start);
                var expected = new List<Thing>(start);
                expected.Remove(t);
                FastList.Remove(plain, t);
                if (!plain.SequenceEqual(expected)) Fail("FastList.Remove removed a later duplicate");

                var tracker = new TrackedLists();
                var tracked = new List<Thing>();
                foreach (var x in start) tracker.Add(tracked, x);
                tracker.Remove(tracked, t);
                if (!tracked.SequenceEqual(expected)) Fail("TrackedLists.Remove removed a later duplicate");
            }

            // A null list throws vanilla's NullReferenceException whatever the occurrence bound, including 0.
            foreach (int atMost in new[] { 0, 1, 2 })
            {
                try
                {
                    FastList.Remove(null, Make(item, 1), atMost);
                    Fail("FastList.Remove on a null list did not throw (atMost " + atMost + ")");
                }
                catch (NullReferenceException) { }
            }

            // 1.7's ListerThings.RemoveSwap: first equal element (List.IndexOf) gets the last element, the last slot goes.
            void VanillaRemoveSwap(List<Thing> list, Thing t)
            {
                int i = list.IndexOf(t);
                if (i < 0) return;
                int last = list.Count - 1;
                list[i] = list[last];
                list.RemoveAt(last);
            }
            foreach (int atMost in new[] { 0, 1, 2 })
            {
                try
                {
                    FastList.RemoveSwap(null, Make(item, 1), atMost);
                    Fail("FastList.RemoveSwap on a null list did not throw (atMost " + atMost + ")");
                }
                catch (NullReferenceException) { }
            }
            {
                var swapRand = new Random(424242);
                for (int round = 0; round < 2000; round++)
                {
                    var pool = new List<Thing>();
                    for (int i = 0; i < 5; i++) pool.Add(Make(item, swapRand.Next(4)));      // twins
                    for (int i = 0; i < 2; i++) pool.Add(Make(mote, -1));
                    pool.Add(null);
                    var list = new List<Thing>();
                    for (int i = swapRand.Next(12); i > 0; i--) list.Add(pool[swapRand.Next(pool.Count)]);
                    Thing t = pool[swapRand.Next(pool.Count)];
                    int equal = list.Count(e => e != null && e.Equals(t)) + (t == null ? list.Count(e => e == null) : 0);
                    // Any bound at least the true count is allowed (Occurrences may overcount, never undercount).
                    int atMost = equal + swapRand.Next(2);
                    var expected = new List<Thing>(list);
                    VanillaRemoveSwap(expected, t);
                    bool removed = FastList.RemoveSwap(list, t, atMost);
                    if (!list.SequenceEqual(expected) || removed != (equal > 0))
                    {
                        Fail($"FastList.RemoveSwap differs from vanilla RemoveSwap (round {round}, atMost {atMost})");
                        break;
                    }
                }
            }

            // Random operation sequences over a small pool so duplicates, twins, motes and nulls are frequent.
            var rand = new Random(12345);
            int trackedRemovals = 0;
            for (int round = 0; round < 300; round++)
            {
                var pool = new List<Thing>();
                for (int i = 0; i < 12; i++) pool.Add(Make(item, rand.Next(6)));           // id collisions: twins
                for (int i = 0; i < 4; i++) pool.Add(Make(mote, -1));                        // motes share id -1
                for (int i = 0; i < 40; i++) pool.Add(Make(item, 1000 + round * 100 + i));  // unique filler
                pool.Add(null);

                var tracker = new TrackedLists();
                var actual = new List<Thing>();
                var reference = new List<Thing>();
                for (int step = 0; step < 400; step++)
                {
                    Thing t = pool[rand.Next(pool.Count)];
                    int op = rand.Next(10);
                    if (op < 5)
                    {
                        tracker.Add(actual, t);
                        reference.Add(t);
                    }
                    else if (op < 9)
                    {
                        if (actual.Count >= TrackedLists.MinTracked) trackedRemovals++;
                        bool a = tracker.Remove(actual, t), r = reference.Remove(t);
                        if (a != r) { Fail($"Remove returned {a}, List.Remove {r}"); break; }
                    }
                    else
                    {
                        if (FastList.IndexOf(actual, t) != reference.IndexOf(t)) { Fail("IndexOf differs"); break; }
                        if (FastList.Contains(actual, t) != reference.Contains(t)) { Fail("Contains differs"); break; }
                    }
                    if (!actual.SequenceEqual(reference)) { Fail($"list differs after step {step} of round {round}"); break; }
                    if (rand.Next(50) == 0) tracker.Forget(actual);
                }
                if (problems > 0) break;
            }

            if (problems == 0 && trackedRemovals < 10000) Fail($"only {trackedRemovals} removals from lists long enough to be counted");
            if (problems == 0) Console.WriteLine("ok    list semantics (FastList, TrackedLists vs List<Thing>)");
            return problems;
        }
    }
}
