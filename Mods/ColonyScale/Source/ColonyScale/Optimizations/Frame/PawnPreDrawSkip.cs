using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ColonyScale.Optimizations.Frame
{
    /// <summary>
    /// PawnRenderTree.Draw calls, for every node of every drawn pawn every frame, worker.PreDraw and then
    /// worker.GetMaterialPropertyBlock. The base PreDraw only writes Color = tint * mat.color into node.MatPropBlock,
    /// and the base GetMaterialPropertyBlock writes Color into that same block again (tint * material.color with the
    /// same material, or the statue colour) before anything reads it. Both calls are swapped: PreDraw is skipped when
    /// that write is provably overwritten, and the GetMaterialPropertyBlock helper reproduces it on the paths where
    /// the base does not overwrite.
    ///
    /// Exact (rendered frames identical):
    /// - Skipped only for non-statue draws, and only when the worker's runtime type inherits PreDraw from
    ///   PawnRenderNodeWorker and its GetMaterialPropertyBlock is the base one or NociosphereSegment's (the only vanilla
    ///   override, which calls base first). Checked per runtime type by reflection, so mod workers that override
    ///   either fall back to vanilla.
    /// - Between the skipped write and the base overwrite only GetGraphic runs (GetGraphicState, the node's
    ///   PrimaryGraphic / GraphicForState, animation state). No vanilla code other than these two worker methods
    ///   touches MatPropBlock or the protected matPropBlock field. Worker and node types from mods that declare
    ///   any of these members fall back to vanilla.
    /// - The base returns null without writing when GetGraphic is null; the helper then performs the skipped write
    ///   itself before returning (sub-workers and anything later see the vanilla block). If GetMaterialPropertyBlock
    ///   throws, the write is also performed before rethrowing. After the draw vanilla clears the block, so its
    ///   contents never carry over between draws. MaterialPropertyBlock holds one value per property: writing Color
    ///   once instead of twice leaves the same block.
    /// - Rendering only: no game state, no Rand. Main thread only (PawnRenderTree.Draw is called from rendering).
    /// Residual assumption: no Harmony patch from another mod reads node.MatPropBlock inside GetGraphic or the
    /// animation worker (patches on the methods listed in DependsOn turn this off).
    /// </summary>
    public sealed class PawnPreDrawSkip : Optimization
    {
        private const string OptId = "pawnPreDrawSkip";

        public override string Id => OptId;
        public override string Label => "Skip redundant pawn colour writes";
        public override string Description =>
            "Pawn drawing no longer sets each body part's and apparel layer's colour twice per frame; the second write " +
            "already sets the same colour. Helps with many pawns on screen.";
        public override string Category => "Frame";

        // Free Performance swaps the same PreDraw call.
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "brage.paralleltick" };

        private static readonly MethodInfo PreDrawMethod = AccessTools.Method(typeof(PawnRenderNodeWorker), nameof(PawnRenderNodeWorker.PreDraw));
        private static readonly MethodInfo GetBlockMethod = AccessTools.Method(typeof(PawnRenderNodeWorker), nameof(PawnRenderNodeWorker.GetMaterialPropertyBlock));
        private static readonly MethodInfo NociosphereGetBlock = AccessTools.Method(typeof(PawnRenderNodeWorker_NociosphereSegment), nameof(PawnRenderNodeWorker.GetMaterialPropertyBlock));
        private static readonly Assembly GameAssembly = typeof(Pawn).Assembly;

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(PawnRenderTree), nameof(PawnRenderTree.Draw)))
                .WithTranspiler(typeof(PawnPreDrawSkip), nameof(Transpiler));
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            PreDrawMethod,
            GetBlockMethod,
            NociosphereGetBlock,
            AccessTools.Method(typeof(PawnRenderNodeWorker), "GetGraphic"),
            AccessTools.Method(typeof(PawnRenderNodeWorker), "GetGraphicState"),
            AccessTools.PropertyGetter(typeof(PawnRenderNode), nameof(PawnRenderNode.MatPropBlock)),
            AccessTools.PropertyGetter(typeof(PawnRenderNode), nameof(PawnRenderNode.PrimaryGraphic)),
            AccessTools.Method(typeof(PawnRenderNode), nameof(PawnRenderNode.GraphicForState)),
            AccessTools.Method(typeof(PawnRenderNode), nameof(PawnRenderNode.TryGetAnimationGraphicState)),
        };

        public override void ClearCaches()
        {
            lock (sync) skippable = new Dictionary<TypePair, bool>(TypePairComparer.Instance);
            skippedNode = null;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.SwapCalls(
                Transpilers.SwapCalls(instructions, OptId, PreDrawMethod, AccessTools.Method(typeof(PawnPreDrawSkip), nameof(PreDraw)), 1),
                OptId, GetBlockMethod, AccessTools.Method(typeof(PawnPreDrawSkip), nameof(GetMaterialPropertyBlock)), 1);

        // The node whose PreDraw was just skipped; GetMaterialPropertyBlock for the same request follows directly.
        // Main thread only; the GetMaterialPropertyBlock helper consumes it before any code that could draw again.
        private static PawnRenderNode skippedNode;

        private static readonly Color VerifySentinel = new Color(float.NaN, float.NaN, float.NaN, float.NaN);

        /// <summary>Replaces <c>callvirt PawnRenderNodeWorker::PreDraw</c> in PawnRenderTree.Draw.</summary>
        public static void PreDraw(PawnRenderNodeWorker worker, PawnRenderNode node, Material mat, PawnDrawParms parms)
        {
            // worker.GetType(): NullReferenceException for a null worker, as the replaced callvirt.
            if (!parms.Statue && node != null && IsSkippable(worker.GetType(), node.GetType()))
            {
                skippedNode = node;
                return;
            }
            skippedNode = null;
            worker.PreDraw(node, mat, parms);
        }

        /// <summary>Replaces <c>callvirt PawnRenderNodeWorker::GetMaterialPropertyBlock</c> in PawnRenderTree.Draw.</summary>
        public static MaterialPropertyBlock GetMaterialPropertyBlock(PawnRenderNodeWorker worker, PawnRenderNode node, Material mat, PawnDrawParms parms)
        {
            if (skippedNode == null || skippedNode != node)
            {
                skippedNode = null;
                return worker.GetMaterialPropertyBlock(node, mat, parms);
            }
            skippedNode = null;
            // Verify: a value the base overwrite must replace; if it does not, the block still holds NaN.
            if (Verify.Enabled) node.MatPropBlock.SetColor(ShaderPropertyIDs.Color, VerifySentinel);
            MaterialPropertyBlock block;
            try
            {
                block = worker.GetMaterialPropertyBlock(node, mat, parms);
            }
            catch
            {
                WriteSkipped(node, mat, parms);
                throw;
            }
            if (block == null) WriteSkipped(node, mat, parms);
            else if (Verify.Enabled) Verify.Record(OptId, !float.IsNaN(node.MatPropBlock.GetColor(ShaderPropertyIDs.Color).r));
            return block;
        }

        /// <summary>The base PreDraw's write, for paths where the base GetMaterialPropertyBlock does not overwrite it.</summary>
        private static void WriteSkipped(PawnRenderNode node, Material mat, PawnDrawParms parms) =>
            node.MatPropBlock.SetColor(ShaderPropertyIDs.Color, parms.tint * mat.color);

        private readonly struct TypePair
        {
            public readonly Type Worker;
            public readonly Type Node;

            public TypePair(Type worker, Type node)
            {
                Worker = worker;
                Node = node;
            }
        }

        private sealed class TypePairComparer : IEqualityComparer<TypePair>
        {
            public static readonly TypePairComparer Instance = new TypePairComparer();

            public bool Equals(TypePair a, TypePair b) => ReferenceEquals(a.Worker, b.Worker) && ReferenceEquals(a.Node, b.Node);

            public int GetHashCode(TypePair p) => RuntimeHelpers.GetHashCode(p.Worker) * 31 + RuntimeHelpers.GetHashCode(p.Node);
        }

        private static readonly object sync = new object();
        // Copy-on-write so a reader never sees a dictionary that is being written.
        private static Dictionary<TypePair, bool> skippable = new Dictionary<TypePair, bool>(TypePairComparer.Instance);

        private static bool IsSkippable(Type workerType, Type nodeType)
        {
            var key = new TypePair(workerType, nodeType);
            var cache = skippable;
            if (cache.TryGetValue(key, out bool result)) return result;
            result = WorkerSkippable(workerType) && NodeSkippable(nodeType);
            lock (sync)
            {
                var copy = new Dictionary<TypePair, bool>(skippable, TypePairComparer.Instance) { [key] = result };
                skippable = copy;
            }
            return result;
        }

        private const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// PreDraw inherited from the base; GetMaterialPropertyBlock from the base or NociosphereSegment; mod classes
        /// in the hierarchy declare none of the members that run before the base overwrite.
        /// </summary>
        private static bool WorkerSkippable(Type type)
        {
            try
            {
                for (Type c = type; c != null; c = c.BaseType)
                {
                    if (c == typeof(PawnRenderNodeWorker)) return true;
                    bool fromMod = c.Assembly != GameAssembly;
                    foreach (MethodInfo m in c.GetMethods(Declared))
                    {
                        switch (m.Name)
                        {
                            case nameof(PawnRenderNodeWorker.PreDraw):
                                return false;
                            case nameof(PawnRenderNodeWorker.GetMaterialPropertyBlock):
                                if (c != typeof(PawnRenderNodeWorker_NociosphereSegment)) return false;
                                break;
                            case "GetGraphic":
                            case "GetGraphicState":
                                if (fromMod) return false;
                                break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A mod type whose members cannot be loaded: run vanilla.
            }
            return false;
        }

        /// <summary>Mod node classes declare none of the graphic lookups GetGraphic makes.</summary>
        private static bool NodeSkippable(Type type)
        {
            try
            {
                for (Type c = type; c != null; c = c.BaseType)
                {
                    if (c == typeof(PawnRenderNode)) return true;
                    if (c.Assembly == GameAssembly) continue;
                    foreach (MethodInfo m in c.GetMethods(Declared))
                        if (m.Name == "get_" + nameof(PawnRenderNode.PrimaryGraphic) || m.Name == nameof(PawnRenderNode.GraphicForState)) return false;
                }
            }
            catch (Exception)
            {
                // As above.
            }
            return false;
        }
    }
}
