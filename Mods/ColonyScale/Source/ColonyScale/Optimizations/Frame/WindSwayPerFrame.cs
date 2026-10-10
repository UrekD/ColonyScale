using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ColonyScale.Optimizations.Frame
{
    /// <summary>
    /// WindManager.WindManagerTick (current map, every tick) ends by writing the plant sway phase into every plant
    /// material (Material.SetFloat, a native call per material, hundreds of materials). Several ticks run per frame at
    /// higher speeds, and only the last value written before a frame renders is ever seen. The write loop is replaced
    /// by recording the value and the material count; the write happens once, before anything can render.
    ///
    /// Exact for game state: plantSwayHead and everything else in the tick is still computed every tick, and the
    /// materials' _SwayHead is only read by the GPU (no game code reads it back). Visually identical per rendered frame:
    /// a material drawn with Graphics.DrawMesh is read when the camera renders, after the frame's Update. Map plants
    /// are only drawn from Map.MapUpdate (section meshes and dynamic things, re-issued every frame), called from
    /// Game.UpdatePlay after TickManager.TickManagerUpdate; every other tick caller (game init, PauseOnLoad and
    /// gravship landing callbacks via LongEventHandler, dev single-tick from OnGUI) runs before the next UpdatePlay
    /// and no render in between draws map plants. Gravship capture, map screenshots and other cameras render after
    /// Update with the queued draws, so the flushes in the TickManagerUpdate postfix, the Map.MapUpdate prefix and
    /// the Game.UpdatePlay postfix all precede them. Pending (value, count) equals vanilla's last write: each write
    /// covers materials [0, Count) at that tick, the list only grows (Notify_PlantMaterialCreated is its only
    /// writer), so the last write covers a superset of the earlier ones with the last value, and materials created
    /// after it keep their initial value until the next tick, as in vanilla (MaterialPool.MatFrom creates them from
    /// the shader, not by copying another plant material). Turning the optimization off flushes what is pending.
    /// Verify mode keeps vanilla's loop and checks at each flush that the materials hold the pending value.
    ///
    /// Threading: map ticks and updates run on the main thread.
    /// </summary>
    public sealed class WindSwayPerFrame : Optimization
    {
        private const string OptId = "windSwayPerFrame";

        public override string Id => OptId;
        public override string Label => "Plant sway once per frame";
        public override string Description =>
            "The wind sway of plants is sent to the graphics card once per frame instead of once per tick. Helps most at high game speeds.";
        public override string Category => "Frame";

        // Free Performance's windsway and Performance Optimizer transpile the same method.
        public override IEnumerable<string> IncompatiblePackageIds => new[] { "brage.paralleltick", "Taranchuk.PerformanceOptimizer" };

        private static readonly MethodInfo TickMethod = AccessTools.Method(typeof(WindManager), nameof(WindManager.WindManagerTick));
        private static readonly MethodInfo CurrentMapGetter = AccessTools.PropertyGetter(typeof(Find), nameof(Find.CurrentMap));
        private static readonly MethodInfo SetFloat = AccessTools.Method(typeof(Material), nameof(Material.SetFloat), new[] { typeof(int), typeof(float) });
        private static readonly MethodInfo DeferMethod = AccessTools.Method(typeof(WindSwayPerFrame), nameof(Defer));

        private static readonly AccessTools.FieldRef<WindManager, float> plantSwayHeadRef = AccessTools.FieldRefAccess<WindManager, float>("plantSwayHead");
        private static readonly AccessTools.FieldRef<List<Material>> plantMaterialsRef =
            AccessTools.StaticFieldRefAccess<List<Material>>(AccessTools.Field(typeof(WindManager), "plantMaterials"));

        public override IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.For(TickMethod)
                .WithTranspiler(typeof(WindSwayPerFrame), nameof(Transpiler));
            // Flush points: only their position in the frame matters, not their bodies.
            yield return PatchSpec.For(AccessTools.Method(typeof(TickManager), nameof(TickManager.TickManagerUpdate)))
                .WithPostfix(typeof(WindSwayPerFrame), nameof(Flush)).AsHook();
            yield return PatchSpec.For(AccessTools.Method(typeof(Verse.Map), nameof(Verse.Map.MapUpdate)))
                .WithPrefix(typeof(WindSwayPerFrame), nameof(Flush)).AsHook();
            yield return PatchSpec.For(AccessTools.Method(typeof(Game), nameof(Game.UpdatePlay)))
                .WithPostfix(typeof(WindSwayPerFrame), nameof(Flush)).AsHook();
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            AccessTools.Method(typeof(WindManager), nameof(WindManager.Notify_PlantMaterialCreated)),
            AccessTools.Method(typeof(MaterialPool), nameof(MaterialPool.MatFrom), new[] { typeof(MaterialRequest) }),
        };

        public override void ClearCaches() => Flush();

        private static bool pending;
        private static float pendingValue;
        private static int pendingCount;

        /// <summary>
        /// Inserted where vanilla starts the write loop (current map only). Records the write; true skips the loop.
        /// </summary>
        public static bool Defer(WindManager windManager)
        {
            pendingValue = plantSwayHeadRef(windManager);
            pendingCount = plantMaterialsRef().Count;
            pending = true;
            return !Verify.Enabled;
        }

        public static void Flush()
        {
            if (pending) FlushPending();
        }

        // Kept out of Flush so ShaderPropertyIDs (Unity) is only touched when something is pending.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FlushPending()
        {
            pending = false;
            List<Material> materials = plantMaterialsRef();
            int count = Math.Min(pendingCount, materials.Count);
            int id = ShaderPropertyIDs.SwayHead;
            float value = pendingValue;
            if (Verify.Enabled)
            {
                bool same = true;
                for (int k = 0; same && k < count; k++) same = materials[k].GetFloat(id) == value;
                Verify.Record(OptId, same);
            }
            for (int k = 0; k < count; k++) materials[k].SetFloat(id, value);
        }

        /// <summary>
        /// After the `Find.CurrentMap == map` branch: `if (Defer(this)) return;` ahead of the write loop, which is the
        /// rest of the method.
        /// </summary>
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var list = instructions.ToList();
            if (list.Count(i => i.Calls(CurrentMapGetter)) != 1 || list.Count(i => i.Calls(SetFloat)) != 1)
                throw new PatternNotFoundException(OptId, "one Find.CurrentMap read and one Material.SetFloat call");
            int test = list.FindIndex(i => i.Calls(CurrentMapGetter));
            int branch = list.FindIndex(test, i => i.Branches(out _));
            int loop = branch + 1;
            int write = list.FindIndex(i => i.Calls(SetFloat));
            // Expected: call Find.CurrentMap; ldarg.0; ldfld map; bne.un END; <loop init> ... SetFloat ... END: ret
            if (branch != test + 3 || (list[branch].opcode != OpCodes.Bne_Un && list[branch].opcode != OpCodes.Bne_Un_S)
                || list[loop].labels.Count > 0 || write < loop || list[list.Count - 1].opcode != OpCodes.Ret
                || list.Skip(loop).Any(i => i.opcode == OpCodes.Ret && i != list[list.Count - 1]))
                throw new PatternNotFoundException(OptId, "current-map test followed by the material write loop at the end of WindManagerTick");

            Label runLoop = generator.DefineLabel();
            list[loop].labels.Add(runLoop);
            list.InsertRange(loop, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, DeferMethod),
                new CodeInstruction(OpCodes.Brfalse, runLoop),
                new CodeInstruction(OpCodes.Ret),
            });
            return list;
        }
    }
}
