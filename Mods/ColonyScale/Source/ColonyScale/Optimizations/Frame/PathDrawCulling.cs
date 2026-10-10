using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ColonyScale.Optimizations.Frame
{
    /// <summary>
    /// PawnPath.DrawPath draws every remaining segment of a selected pawn's path every frame, one Graphics.DrawMesh
    /// each, however far off screen the path runs. Its two GenDraw.DrawLineBetween(a, b) calls are swapped for a
    /// helper that skips segments lying entirely outside the camera view and otherwise calls the original.
    ///
    /// Exact: DrawLineBetween(a, b) draws LineMatWhite on a plane10 quad scaled to width 0.2 and length |a-b|,
    /// centred between a and b and rotated along a-b, so the quad stays within the a/b bounding box widened by 0.1.
    /// A segment is skipped only if that box is more than 2 cells outside the camera's view, so the draw would
    /// have been frustum-culled by Unity and never sorted or rendered; each segment is a separate draw, so the
    /// remaining ones are submitted in the same order. Skipped during gravship capture or with a non-standard camera.
    /// </summary>
    public sealed class PathDrawCulling : Optimization
    {
        private const string OptId = "pathDrawCulling";

        public override string Id => OptId;
        public override string Label => "Skip off-screen path lines";
        public override string Description =>
            "The movement path of selected pawns is only drawn where it is on screen. Helps when many pawns with long paths are selected.";
        public override string Category => "Frame";

        private static readonly MethodInfo DrawLine = AccessTools.Method(typeof(GenDraw), nameof(GenDraw.DrawLineBetween), new[] { typeof(Vector3), typeof(Vector3) });
        private static readonly MethodInfo DrawSegmentMethod = AccessTools.Method(typeof(PathDrawCulling), nameof(DrawSegment));

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(PawnPath), nameof(PawnPath.DrawPath)))
                .WithPrefix(typeof(PathDrawCulling), nameof(Begin))
                .WithTranspiler(typeof(PathDrawCulling), nameof(Transpiler));
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            DrawLine,
            AccessTools.Method(typeof(GenDraw), nameof(GenDraw.DrawLineBetween), new[] { typeof(Vector3), typeof(Vector3), typeof(Material), typeof(float) }),
            AccessTools.Method(typeof(MeshMakerPlanes), nameof(MeshMakerPlanes.NewPlaneMesh), new[] { typeof(float) }),
        };

        public override void ClearCaches() => cull = false;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.SwapCalls(instructions, OptId, DrawLine, DrawSegmentMethod, 2);

        // Set per DrawPath call; it runs on the main thread and does not re-enter. A gravship capture can start
        // while the path is drawn in a frame, so DrawSegment re-reads that flag per segment.
        private static bool cull;
        private static float minX, maxX, minZ, maxZ;

        public static void Begin() => cull = CameraView.TryGet(2f, out minX, out maxX, out minZ, out maxZ);

        /// <summary>Replaces <c>call GenDraw::DrawLineBetween(Vector3, Vector3)</c> in PawnPath.DrawPath.</summary>
        public static void DrawSegment(Vector3 a, Vector3 b)
        {
            if (cull && !WorldComponent_GravshipController.GravshipRenderInProgess
                && (Mathf.Max(a.x, b.x) < minX || Mathf.Min(a.x, b.x) > maxX || Mathf.Max(a.z, b.z) < minZ || Mathf.Min(a.z, b.z) > maxZ))
                return;
            GenDraw.DrawLineBetween(a, b);
        }
    }
}
