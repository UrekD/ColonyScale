using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale.Optimizations.AI
{
    /// <summary>
    /// WorkGiver_Merge.JobOnThing looks for a stack to merge a partial stack into by walking every thing held by the
    /// stack's whole stockpile or shelf group (SlotGroup/StorageGroup.HeldThings: every cell, every thing, an
    /// EverStorable reflection test per thing) and testing each with CanStackWith. Every partial stack in storage is a
    /// merge candidate, and a lone partial stack fails after walking the whole group, so a hauler's job search costs
    /// candidates x group size. The HeldThings call is swapped for an iterator that walks the same members, cells and
    /// thing lists in the same order, lazily as vanilla, but only yields things of the candidate's def (other than the
    /// candidate itself). Nothing is cached between calls, so no assumption about storage staying unchanged is needed.
    ///
    /// Exact:
    /// - For every skipped thing the loop condition is false without side effects: it is the candidate itself
    ///   (heldThing != t is false), or its def differs, and then heldThing.CanStackWith(t) is false. Thing.CanStackWith
    ///   returns false on a def mismatch after only pure reads (Destroyed, category, IsRelic); ThingWithComps calls base
    ///   first; Book returns false; MinifiedThing returns false for a non-minified other and otherwise calls base.
    ///   Mod types overriding CanStackWith are not covered: if any loaded Thing subclass outside these declares it,
    ///   vanilla runs.
    /// - Yielded things are exactly the same-def things vanilla yields (EverStorable is a function of the def, so it
    ///   is evaluated once), in the same order, and the loop body is untouched, so the first match (and the job) is
    ///   the same. The walk calls CellsList, parent.Map, GetSlotGroup and ThingsListAt in the same sequence as the
    ///   vanilla iterators and stops where they stop, so lazy caches, logs and exceptions happen at the same points.
    /// - No Rand. Main thread only (job search).
    /// </summary>
    public sealed class MergeSameDef : Optimization
    {
        private const string OptId = "mergeSameDef";

        public override string Id => OptId;
        public override string Label => "Faster stack merge search";
        public override string Description =>
            "When looking for a stack to merge a partial stack into, haulers only look at stacks of the same item in that " +
            "stockpile or shelf instead of everything stored there. Helps with large stockpiles.";
        public override string Category => "AI";

        // Free Performance's merge index transpiles the same HeldThings call.
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "brage.paralleltick" };

        private static readonly MethodInfo HeldThingsGetter = AccessTools.PropertyGetter(typeof(ISlotGroup), nameof(ISlotGroup.HeldThings));
        private static readonly MethodInfo SameDefMethod = AccessTools.Method(typeof(MergeSameDef), nameof(HeldThingsLike));

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(WorkGiver_Merge), nameof(WorkGiver_Merge.JobOnThing)))
                .WithTranspiler(typeof(MergeSameDef), nameof(Transpiler));
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            // The walk below is these two iterators, restricted to one def.
            AccessTools.EnumeratorMoveNext(AccessTools.PropertyGetter(typeof(SlotGroup), nameof(SlotGroup.HeldThings))),
            AccessTools.EnumeratorMoveNext(AccessTools.PropertyGetter(typeof(StorageGroup), nameof(StorageGroup.HeldThings))),
            AccessTools.PropertyGetter(typeof(SlotGroup), "Map"),
            AccessTools.Method(typeof(ThingDef), nameof(ThingDef.EverStorable)),
            // False on a def mismatch, without side effects.
            AccessTools.Method(typeof(Thing), nameof(Thing.CanStackWith)),
            AccessTools.Method(typeof(ThingWithComps), nameof(Thing.CanStackWith)),
            AccessTools.Method(typeof(Book), nameof(Thing.CanStackWith)),
            AccessTools.Method(typeof(MinifiedThing), nameof(Thing.CanStackWith)),
            AccessTools.Method(typeof(ReliquaryUtility), nameof(ReliquaryUtility.IsRelic)),
            AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Destroyed)),
        };

        public override void ClearCaches()
        {
            onlyVettedOverrides = null;
        }

        /// <summary>
        /// Replaces <c>callvirt ISlotGroup::get_HeldThings()</c> with <c>ldarg.2 (t); call HeldThingsLike</c>.
        /// </summary>
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            int found = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].Calls(HeldThingsGetter)) continue;
                found++;
                list[i].opcode = OpCodes.Call;
                list[i].operand = SameDefMethod;
                list.Insert(i, new CodeInstruction(OpCodes.Ldarg_2));
                i++;
            }
            if (found != 1) throw new PatternNotFoundException(OptId, $"expected 1 call to ISlotGroup.HeldThings, found {found}");
            return list;
        }

        /// <summary>The things of <paramref name="t"/>'s def (other than t) among group.HeldThings, in vanilla order.</summary>
        public static IEnumerable<Thing> HeldThingsLike(ISlotGroup group, Thing t)
        {
            // group.GetType(): NullReferenceException for a null group, as the replaced callvirt.
            Type type = group.GetType();
            if ((type != typeof(SlotGroup) && type != typeof(StorageGroup)) || t == null || !OnlyVettedOverrides())
                return group.HeldThings;
            if (Verify.Enabled) VerifySameCandidates(group, t);
            return type == typeof(SlotGroup) ? SlotGroupLike((SlotGroup)group, t) : StorageGroupLike((StorageGroup)group, t);
        }

        /// <summary>SlotGroup.HeldThings, yielding only things of t's def other than t.</summary>
        private static IEnumerable<Thing> SlotGroupLike(SlotGroup group, Thing t)
        {
            List<IntVec3> cellsList = group.CellsList;
            Verse.Map map = group.parent.Map;
            ThingDef def = t.def;
            bool storable = def.EverStorable(willMinifyIfPossible: false);
            for (int i = 0; i < cellsList.Count; i++)
            {
                List<Thing> thingList = map.thingGrid.ThingsListAt(cellsList[i]);
                for (int j = 0; j < thingList.Count; j++)
                {
                    Thing thing = thingList[j];
                    if (thing.def == def && storable && thing != t)
                        yield return thing;
                }
            }
        }

        /// <summary>StorageGroup.HeldThings over <see cref="SlotGroupLike"/>.</summary>
        private static IEnumerable<Thing> StorageGroupLike(StorageGroup group, Thing t)
        {
            foreach (IStorageGroupMember member in group.members)
            {
                if (!(member is ISlotGroupParent slotGroupParent)) continue;
                foreach (Thing thing in SlotGroupLike(slotGroupParent.GetSlotGroup(), t))
                    yield return thing;
            }
        }

        // Null until computed; types cannot be added once the game runs.
        private static bool? onlyVettedOverrides;

        /// <summary>
        /// True if no Thing subclass besides the vanilla ones above declares CanStackWith. Looks at every loaded
        /// subclass and at every ThingDef's thingClass hierarchy (the types things are created from).
        /// </summary>
        private static bool OnlyVettedOverrides()
        {
            if (onlyVettedOverrides is bool known) return known;
            bool ok;
            try
            {
                var seen = new HashSet<Type>();
                ok = typeof(Thing).AllSubclasses().All(type => HierarchyVetted(type, seen))
                    && DefDatabase<ThingDef>.AllDefsListForReading.All(def => def.thingClass == null || HierarchyVetted(def.thingClass, seen));
            }
            catch (Exception)
            {
                ok = false; // a type that cannot be inspected: run vanilla
            }
            onlyVettedOverrides = ok;
            return ok;
        }

        private static bool HierarchyVetted(Type type, HashSet<Type> seen)
        {
            for (Type c = type; c != null && c != typeof(Thing); c = c.BaseType)
            {
                if (!seen.Add(c)) return true; // this class and its bases were already checked
                if (c == typeof(ThingWithComps) || c == typeof(Book) || c == typeof(MinifiedThing)) continue;
                foreach (MethodInfo m in c.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (m.Name == nameof(Thing.CanStackWith)) return false;
            }
            return true;
        }

        private static readonly List<Thing> verifyVanilla = new List<Thing>();
        private static readonly List<Thing> verifyFast = new List<Thing>();

        /// <summary>Both walks must leave the same things for the loop condition's first two tests.</summary>
        private static void VerifySameCandidates(ISlotGroup group, Thing t)
        {
            verifyVanilla.Clear();
            verifyFast.Clear();
            try
            {
                foreach (Thing thing in group.HeldThings)
                    if (thing != t && thing.CanStackWith(t)) verifyVanilla.Add(thing);
                var fast = group is SlotGroup sg ? SlotGroupLike(sg, t) : StorageGroupLike((StorageGroup)group, t);
                foreach (Thing thing in fast)
                    if (thing.CanStackWith(t)) verifyFast.Add(thing);
            }
            catch (Exception)
            {
                return; // the real walk that follows fails the same way
            }
            bool same = verifyVanilla.Count == verifyFast.Count;
            for (int i = 0; same && i < verifyVanilla.Count; i++)
                same = ReferenceEquals(verifyVanilla[i], verifyFast[i]);
            Verify.Record(OptId, same);
        }
    }
}
