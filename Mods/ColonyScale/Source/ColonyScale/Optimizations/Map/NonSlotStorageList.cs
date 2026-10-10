using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale.Optimizations.Map
{
    /// <summary>
    /// StoreUtility.TryFindBestBetterNonSlotGroupStorageFor (graves, bookcases, gene banks, other container storage)
    /// runs for most haul searches and walks every haul destination on the map only to skip each stockpile and shelf
    /// (ISlotGroupParent) on its first condition. Here its single read of AllHaulDestinationsListInPriorityOrder is
    /// swapped for a list of the non-ISlotGroupParent entries in the same order, cached per source list and rebuilt
    /// whenever the source list's _version or Count changes.
    ///
    /// Exact:
    /// - For an ISlotGroupParent entry vanilla evaluates only the type test (|| short-circuits) and continues: no other
    ///   call, no change to the best distance/priority/destination, and it never reaches the priority break. Dropping
    ///   those iterations leaves the same sequence of evaluations for every other entry, in the same order, so the
    ///   break and every later condition behave identically. Null entries are kept (same exception as vanilla).
    /// - The filtered list is a pure function of the source list's contents and order (ISlotGroupParent is a type
    ///   test). Every List mutation (Add, Remove, the indexer writes of InsertionSort) bumps _version, so a matching
    ///   (list, _version, Count) means identical contents. This makes extra hooks on AddHaulDestination,
    ///   RemoveHaulDestination and Notify_HaulDestinationChangedPriority unnecessary.
    /// - Assumes, as vanilla's own code does, that nothing called inside the loop (Accepts, reservations, CanReach)
    ///   adds or removes haul destinations mid-search; a rebuild creates a new list, so a running search keeps its own.
    /// Main thread only.
    /// </summary>
    public sealed class NonSlotStorageList : Optimization
    {
        public override string Id => "nonSlotStorage";
        public override string Label => "Faster special storage search";
        public override string Description =>
            "Looking for a grave, bookcase or other container-type storage for an item no longer steps over every " +
            "stockpile and shelf on the map.";
        public override string Category => "Map";

        public override IEnumerable<string> IncompatiblePackageIds => new[] { "bs.performancefish", "brage.paralleltick" };

        private static readonly MethodInfo SourceGetter =
            AccessTools.PropertyGetter(typeof(HaulDestinationManager), nameof(HaulDestinationManager.AllHaulDestinationsListInPriorityOrder));
        private static readonly MethodInfo FilteredGetter = AccessTools.Method(typeof(NonSlotStorageList), nameof(NonSlotDestinations));
        private static readonly AccessTools.FieldRef<List<IHaulDestination>, int> listVersion =
            AccessTools.FieldRefAccess<List<IHaulDestination>, int>("_version");

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterNonSlotGroupStorageFor)))
                .WithTranspiler(typeof(NonSlotStorageList), nameof(Transpiler));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.SwapCalls(instructions, "nonSlotStorage", SourceGetter, FilteredGetter, 1);

        public override void ClearCaches()
        {
            cache = new ConditionalWeakTable<List<IHaulDestination>, Filtered>();
            lastSource = null;
            lastFiltered = null;
        }

        private sealed class Filtered
        {
            public int version;
            public int count = -1;
            public List<IHaulDestination> list;
        }

        private static ConditionalWeakTable<List<IHaulDestination>, Filtered> cache = new ConditionalWeakTable<List<IHaulDestination>, Filtered>();
        private static List<IHaulDestination> lastSource;
        private static Filtered lastFiltered;

        public static List<IHaulDestination> NonSlotDestinations(HaulDestinationManager manager)
        {
            List<IHaulDestination> source = manager.AllHaulDestinationsListInPriorityOrder;
            Filtered entry;
            if (ReferenceEquals(source, lastSource)) entry = lastFiltered;
            else
            {
                entry = cache.GetValue(source, _ => new Filtered());
                lastSource = source;
                lastFiltered = entry;
            }

            int version = listVersion(source);
            if (entry.list == null || entry.version != version || entry.count != source.Count)
            {
                var list = new List<IHaulDestination>();
                for (int i = 0; i < source.Count; i++)
                {
                    IHaulDestination d = source[i];
                    if (!(d is ISlotGroupParent)) list.Add(d);
                }
                entry.list = list;
                entry.version = version;
                entry.count = source.Count;
            }
            else if (Verify.Enabled)
            {
                Verify.Record("nonSlotStorage", SameAsFresh(entry.list, source));
            }
            return entry.list;
        }

        private static bool SameAsFresh(List<IHaulDestination> filtered, List<IHaulDestination> source)
        {
            int k = 0;
            for (int i = 0; i < source.Count; i++)
            {
                IHaulDestination d = source[i];
                if (d is ISlotGroupParent) continue;
                if (k >= filtered.Count || !ReferenceEquals(filtered[k], d)) return false;
                k++;
            }
            return k == filtered.Count;
        }
    }
}
