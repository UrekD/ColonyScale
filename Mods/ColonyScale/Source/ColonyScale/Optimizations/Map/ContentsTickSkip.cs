using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyScale.Optimizations.Map
{
    /// <summary>
    /// Thing.DoTick ends, for every thing holder, with a walk over its contents: the holder chain test
    /// (ThingOwnerUtility.ContentsSuspended), GetChildHolders into a list, and ThingOwner.DoTick on every held
    /// container, which calls Thing.DoTick on every held thing. For a pawn that is inventory, carried thing, equipment
    /// and apparel, every tick, and almost always nothing in there does anything. The ContentsSuspended call is
    /// swapped for one that also answers "suspended" (return without walking) when the walk would do nothing.
    ///
    /// Exact: vanilla's ContentsSuspended still runs first and its true answer is returned as is. Only exact types
    /// Pawn, Frame and Building_Bookcase are considered, whose GetChildHolders/GetDirectlyHeldThings are known: Pawn's
    /// holder list is the pawn (no own container) plus its non-null inventory, carry, equipment and apparel trackers;
    /// a frame's and a bookcase's is the building (resource or book container) plus any held thing that is a holder
    /// and any holder comp of a held thing, which we require to be absent. The walk is then ThingOwner.DoTick on
    /// those containers, a no-op for dontTickContents, otherwise Thing.DoTick on each held thing from the back and
    /// a Remove if it is destroyed. For a held thing that is not null, not destroyed, not spawned, has
    /// holdingOwner == that container and is not a holder, Thing.DoTick computes flag = true, so a Normal ticker
    /// would Tick (not skipped), a Rare or Long ticker only ticks on IsHashIntervalTick(250/2000) (we call the same
    /// test and do not skip when it is true), a Never ticker does nothing, and the method returns at !cachedIsHolder.
    /// What is left unrun is the lazy fill of the private cached/cachedHolder/cachedTickable/cachedIsHolder fields
    /// (type tests with the same result whenever they do run, read nowhere else) and the lazy allocation of the
    /// holder's private tmpHolders list (only used inside this walk). Nothing in the skipped walk draws Rand.
    /// Verify mode compares the skip against vanilla's own holder list (GetChildHolders) and then walks as vanilla.
    ///
    /// Threading: Thing.DoTick runs on the main thread (TickList); the verify buffer is only touched there.
    /// </summary>
    public sealed class ContentsTickSkip : Optimization
    {
        private const string OptId = "contentsTickSkip";

        public override string Id => OptId;
        public override string Label => "Skip idle inventory ticks";
        public override string Description =>
            "Pawns, construction frames and bookcases no longer walk their inventory, equipment, apparel or stored items every tick " +
            "when nothing in there needs to tick. Helps with every pawn on the map.";
        public override string Category => "Map";

        // Free Performance's contentsskip swaps the same call.
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "brage.paralleltick" };

        private static readonly MethodInfo ContentsSuspendedMethod =
            AccessTools.Method(typeof(ThingOwnerUtility), nameof(ThingOwnerUtility.ContentsSuspended));
        private static readonly MethodInfo FastMethod = AccessTools.Method(typeof(ContentsTickSkip), nameof(ContentsSuspendedOrIdle));

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(Thing), nameof(Thing.DoTick)))
                .WithTranspiler(typeof(ContentsTickSkip), nameof(Transpiler));
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            // The held things' own DoTick (another mod's patch on it would be skipped for them).
            AccessTools.Method(typeof(Thing), nameof(Thing.DoTick)),
            ContentsSuspendedMethod,
            AccessTools.Method(typeof(ThingOwner), nameof(ThingOwner.DoTick)),
            AccessTools.Method(typeof(ThingOwnerUtility), nameof(ThingOwnerUtility.AppendThingHoldersFromThings)),
            AccessTools.Method(typeof(Pawn), nameof(Pawn.GetChildHolders)),
            AccessTools.Method(typeof(Pawn), nameof(Pawn.GetDirectlyHeldThings)),
            AccessTools.Method(typeof(Pawn_InventoryTracker), nameof(Pawn_InventoryTracker.GetDirectlyHeldThings)),
            AccessTools.Method(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.GetDirectlyHeldThings)),
            AccessTools.Method(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.GetDirectlyHeldThings)),
            AccessTools.Method(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.GetDirectlyHeldThings)),
            AccessTools.Method(typeof(RimWorld.Frame), nameof(RimWorld.Frame.GetChildHolders)),
            AccessTools.Method(typeof(RimWorld.Frame), nameof(RimWorld.Frame.GetDirectlyHeldThings)),
            AccessTools.Method(typeof(Building_Bookcase), nameof(Building_Bookcase.GetChildHolders)),
            AccessTools.Method(typeof(Building_Bookcase), nameof(Building_Bookcase.GetDirectlyHeldThings)),
            AccessTools.PropertyGetter(typeof(ThingWithComps), nameof(ThingWithComps.AllComps)),
            AccessTools.Method(typeof(Gen), nameof(Gen.IsHashIntervalTick), new[] { typeof(Thing), typeof(int) }),
            AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Destroyed)),
            AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Spawned)),
        };

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.SwapCalls(instructions, OptId, ContentsSuspendedMethod, FastMethod, 1);

        /// <summary>Vanilla's ContentsSuspended, or true when the contents walk would do nothing.</summary>
        public static bool ContentsSuspendedOrIdle(IThingHolder holder)
        {
            if (ThingOwnerUtility.ContentsSuspended(holder)) return true;
            System.Type type = holder.GetType();
            bool idle;
            if (type == typeof(Pawn)) idle = PawnContentsIdle((Pawn)holder);
            else if (type == typeof(RimWorld.Frame)) idle = StoredContentsIdle(((RimWorld.Frame)holder).GetDirectlyHeldThings());
            else if (type == typeof(Building_Bookcase)) idle = StoredContentsIdle(((Building_Bookcase)holder).GetDirectlyHeldThings());
            else return false;
            if (idle && Verify.Enabled)
            {
                Verify.Record(OptId, VanillaWalkIdle(holder));
                return false;
            }
            return idle;
        }

        private static bool PawnContentsIdle(Pawn pawn) =>
            (pawn.inventory == null || OwnerIdle(pawn.inventory.GetDirectlyHeldThings()))
            && (pawn.carryTracker == null || OwnerIdle(pawn.carryTracker.GetDirectlyHeldThings()))
            && (pawn.equipment == null || OwnerIdle(pawn.equipment.GetDirectlyHeldThings()))
            && (pawn.apparel == null || OwnerIdle(pawn.apparel.GetDirectlyHeldThings()));

        /// <summary>
        /// A frame's or bookcase's container: GetChildHolders would add held holders and holder comps to the walk,
        /// independent of dontTickContents.
        /// </summary>
        private static bool StoredContentsIdle(ThingOwner owner)
        {
            if (owner == null) return true;
            bool ticks = !owner.dontTickContents;
            for (int i = owner.Count - 1; i >= 0; i--)
            {
                Thing t = owner[i];
                if (t is IThingHolder || (t is ThingWithComps withComps && HasHolderComp(withComps))) return false;
                if (ticks && !ThingIdle(t, owner)) return false;
            }
            return true;
        }

        /// <summary>ThingOwner.DoTick would only run no-op Thing.DoTick calls.</summary>
        private static bool OwnerIdle(ThingOwner owner)
        {
            if (owner == null || owner.dontTickContents) return true;
            for (int i = owner.Count - 1; i >= 0; i--)
                if (!ThingIdle(owner[i], owner)) return false;
            return true;
        }

        /// <summary>Thing.DoTick on this held thing would do nothing but its lazy cached-field fill.</summary>
        private static bool ThingIdle(Thing t, ThingOwner owner)
        {
            // Null things and defs are left to vanilla, which throws where it always did.
            if (t == null || t.def == null || t.Destroyed || t.holdingOwner != owner || t.Spawned || t is IThingHolder) return false;
            switch (t.def.tickerType)
            {
                case TickerType.Never: return true;
                case TickerType.Rare: return !t.IsHashIntervalTick(250);
                case TickerType.Long: return !t.IsHashIntervalTick(2000);
                default: return false;
            }
        }

        private static bool HasHolderComp(ThingWithComps thing)
        {
            List<ThingComp> comps = thing.AllComps;
            for (int i = 0; i < comps.Count; i++)
                if (comps[i] is IThingHolder) return true;
            return false;
        }

        private static readonly List<IThingHolder> verifyHolders = new List<IThingHolder>();

        /// <summary>Every container vanilla's walk would reach (from its own GetChildHolders) is idle.</summary>
        private static bool VanillaWalkIdle(IThingHolder holder)
        {
            verifyHolders.Clear();
            verifyHolders.Add(holder);
            holder.GetChildHolders(verifyHolders);
            bool idle = true;
            for (int i = 0; idle && i < verifyHolders.Count; i++)
                idle = OwnerIdle(verifyHolders[i].GetDirectlyHeldThings());
            verifyHolders.Clear();
            return idle;
        }
    }
}
