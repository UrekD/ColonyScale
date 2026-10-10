using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyScale.Optimizations.Frame
{
    /// <summary>
    /// OverlayDrawer.DrawAllOverlays first walks every overlay handle on the map (one per forbiddable item and per
    /// powered or breakdownable building, thousands late game) and calls Fogged() on each before DrawOverlay finds
    /// that most have no overlay at all; then every collected overlay is rendered, on screen or not. The Fogged call
    /// in that first loop is swapped for a test that also skips (a) handles with no overlay types and (b) things
    /// whose only overlay is the forbidden marker and lies entirely off screen. The second loop is untouched.
    ///
    /// Exact: (a) DrawOverlay(t, None) does nothing, Fogged only reads, and OverlayTypes is a cached OR of the
    /// handle list, so reading it before the fog test changes nothing. (b) A culled thing has no entry yet in
    /// overlaysToDraw, its types are exactly Forbidden (one plane05 ForbiddenMat quad) or exactly ForbiddenBig (one
    /// plane10 quad), its type does not override Thing.DrawPos (so DrawPos is TrueCenter: inside OccupiedRect, or for
    /// items at most 0.5 + 0.24 x items-in-cell from the cell centre), and that quad, attachment and z offsets
    /// included (each well under a cell), lies outside the camera's view by a 3-cell margin. Draw order of what is
    /// still drawn is unchanged: overlaysToDraw only grows during the frame, so it enumerates in insertion order
    /// minus the culled entries; DrawBatch orders its batches by the first mesh/material seen, and the first
    /// non-fogged thing of each culled kind is never culled, so each batch keeps its first emitter and its place;
    /// within a batch, instances keep their order and all sit at the same altitude. Skipped when gravship capture
    /// renders, or when the camera is not the straight-down orthographic map camera.
    /// </summary>
    public sealed class OverlayCulling : Optimization
    {
        private const string OptId = "overlayCulling";

        /// <summary>Cells of slack around the view beyond the overlay geometry bounds below.</summary>
        private const float ViewMargin = 3f;

        public override string Id => OptId;
        public override string Label => "Skip hidden overlay icons";
        public override string Description =>
            "Forbidden markers and other overlay icons are no longer checked every frame for things that have no icon, and forbidden " +
            "markers of off-screen items are not drawn. Helps on large maps with many items.";
        public override string Category => "Frame";

        private static readonly MethodInfo Fogged = AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.Fogged), new[] { typeof(Thing) });
        private static readonly MethodInfo SkipHandleMethod = AccessTools.Method(typeof(OverlayCulling), nameof(SkipHandle));
        private static readonly MethodInfo DrawPosGetter = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.DrawPos));

        private static AccessTools.FieldRef<OverlayDrawer, Dictionary<Thing, OverlayTypes>> overlaysToDrawRef;

        public override IEnumerable<PatchSpec> GetPatches()
        {
            overlaysToDrawRef = AccessTools.FieldRefAccess<OverlayDrawer, Dictionary<Thing, OverlayTypes>>("overlaysToDraw");
            yield return PatchSpec.For(AccessTools.Method(typeof(OverlayDrawer), nameof(OverlayDrawer.DrawAllOverlays)))
                .WithPrefix(typeof(OverlayCulling), nameof(Begin))
                .WithTranspiler(typeof(OverlayCulling), nameof(Transpiler));
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            AccessTools.Method(typeof(OverlayDrawer), nameof(OverlayDrawer.DrawOverlay)),
            AccessTools.Method(typeof(OverlayDrawer), "RenderForbiddenOverlay"),
            AccessTools.Method(typeof(OverlayDrawer), "RenderForbiddenBigOverlay"),
            AccessTools.Method(typeof(OverlayDrawer), nameof(OverlayDrawer.GetOverlaysHandle)),
            AccessTools.Method(typeof(OverlayDrawer), nameof(OverlayDrawer.DisposeHandle)),
            AccessTools.PropertyGetter(typeof(ThingOverlaysHandle), nameof(ThingOverlaysHandle.OverlayTypes)),
            Fogged,
            DrawPosGetter,
            AccessTools.Method(typeof(GenThing), nameof(GenThing.TrueCenter), new[] { typeof(Thing) }),
            AccessTools.Method(typeof(GenThing), nameof(GenThing.TrueCenter), new[] { typeof(IntVec3), typeof(Rot4), typeof(IntVec2), typeof(float) }),
            AccessTools.Method(typeof(GenThing), "ItemCenterAt"),
            AccessTools.Method(typeof(GenGeo), nameof(GenGeo.RegularPolygonVertexPosition)),
            AccessTools.Method(typeof(DrawBatch), nameof(DrawBatch.DrawMesh), new[] { typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int), typeof(bool) }),
            AccessTools.Method(typeof(DrawBatch), nameof(DrawBatch.Flush)),
            AccessTools.Method(typeof(DrawBatch), "GetBatchDataForInsertion"),
        };

        public override void ClearCaches()
        {
            drawPosOverridden.Clear();
            pending = null;
            cull = false;
        }

        /// <summary>
        /// Replaces <c>ldloca kvp; call get_Key; call GridsUtility::Fogged(Thing)</c> in the first loop with
        /// <c>ldloca kvp; call get_Key; ldarg.0; ldloca kvp; call get_Value; call SkipHandle</c>.
        /// </summary>
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getKey = AccessTools.PropertyGetter(typeof(KeyValuePair<Thing, ThingOverlaysHandle>), "Key");
            var getValue = AccessTools.PropertyGetter(typeof(KeyValuePair<Thing, ThingOverlaysHandle>), "Value");
            var list = instructions.ToList();
            int found = 0;
            for (int i = 2; i < list.Count; i++)
            {
                if (!list[i].Calls(Fogged)) continue;
                found++;
                var load = list[i - 2];
                if (!list[i - 1].Calls(getKey) || (load.opcode != OpCodes.Ldloca && load.opcode != OpCodes.Ldloca_S) || list[i].labels.Count != 0)
                    throw new PatternNotFoundException(OptId, "ldloca kvp; call get_Key; call Fogged");
                list[i].opcode = OpCodes.Call;
                list[i].operand = SkipHandleMethod;
                list.InsertRange(i, new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(load.opcode, load.operand),
                    new CodeInstruction(OpCodes.Call, getValue),
                });
                i += 3;
            }
            if (found != 1) throw new PatternNotFoundException(OptId, $"expected 1 call to GridsUtility.Fogged, found {found}");
            return list;
        }

        // Per DrawAllOverlays call; it runs on the main thread and does not re-enter.
        private static bool cull;
        private static float minX, maxX, minZ, maxZ;
        private static Dictionary<Thing, OverlayTypes> pending;
        private static bool keptForbidden, keptForbiddenBig;

        public static void Begin(OverlayDrawer __instance)
        {
            pending = overlaysToDrawRef(__instance);
            keptForbidden = keptForbiddenBig = false;
            cull = CameraView.TryGet(ViewMargin, out minX, out maxX, out minZ, out maxZ);
        }

        /// <summary>True where vanilla would skip the handle (fogged) or where skipping it is provably invisible.</summary>
        public static bool SkipHandle(Thing thing, OverlayDrawer drawer, ThingOverlaysHandle handle)
        {
            OverlayTypes types = handle.OverlayTypes;
            if (types == OverlayTypes.None) return true;
            if (thing.Fogged()) return true;
            if (!cull) return false;

            if (types == OverlayTypes.Forbidden)
            {
                if (!keptForbidden) { keptForbidden = true; return false; }
            }
            else if (types == OverlayTypes.ForbiddenBig)
            {
                if (!keptForbiddenBig) { keptForbiddenBig = true; return false; }
            }
            else return false;

            if (!OffScreen(thing) || pending.ContainsKey(thing)) return false;
            if (Verify.Enabled) VerifyOffScreen(thing, types);
            return true;
        }

        private static bool OffScreen(Thing thing)
        {
            if (!thing.Spawned || DrawPosOverridden(thing.GetType())) return false;
            // Forbidden quads reach at most 1 cell past the centre bounds below (attachment shift 0.5 + half quad <= 0.5);
            // ViewMargin covers that with slack.
            float extra = 0f;
            if (thing.def.category == ThingCategory.Item)
                extra = 0.5f + 0.25f * thing.Map.thingGrid.ThingsListAtFast(thing.Position).Count;
            CellRect r = thing.OccupiedRect();
            return r.maxX + 1 + extra < minX || r.minX - extra > maxX || r.maxZ + 1 + extra < minZ || r.minZ - extra > maxZ;
        }

        /// <summary>Per runtime type: whether Thing.DrawPos is overridden. Main thread only.</summary>
        private static readonly Dictionary<Type, bool> drawPosOverridden = new Dictionary<Type, bool>();

        private static bool DrawPosOverridden(Type type)
        {
            if (drawPosOverridden.TryGetValue(type, out bool result)) return result;
            result = false;
            for (Type t = type; t != null && t != typeof(Thing); t = t.BaseType)
            {
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != DrawPosGetter.Name || m.GetParameters().Length != 0) continue;
                    var baseDef = m.GetBaseDefinition();
                    if (baseDef.DeclaringType == typeof(Thing) && baseDef.Name == DrawPosGetter.Name) result = true;
                }
            }
            drawPosOverridden[type] = result;
            return result;
        }

        /// <summary>Verify mode: the quad vanilla would have drawn for a culled thing really is outside the camera view.</summary>
        private static void VerifyOffScreen(Thing thing, OverlayTypes types)
        {
            if (!CameraView.TryGet(0f, out float x0, out float x1, out float z0, out float z1)) return;
            Vector3 c = thing.DrawPos;
            bool attachment = (thing.def.entityDefToBuild as ThingDef)?.building?.isAttachment ?? thing.def.building?.isAttachment ?? false;
            if (attachment) c += (thing.Rotation.AsVector2 * 0.5f).ToVector3();
            float half = 0.5f;
            if (types == OverlayTypes.Forbidden)
            {
                c.z -= thing.RotatedSize.z == 1 ? 0.3f : thing.RotatedSize.z * 0.3f;
                half = 0.25f;
            }
            Verify.Record(OptId, c.x + half < x0 || c.x - half > x1 || c.z + half < z0 || c.z - half > z1);
        }
    }
}
