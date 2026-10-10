using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace ColonyScale.Optimizations
{
    /// <summary>
    /// The points where def-derived caches must be dropped, shared by every optimization that keeps one.
    ///
    /// Def hot reload (dev mode) keeps every existing def object and its index but overwrites its other fields
    /// (LoadedModManager.LoadAllActiveMods(hotReload: true) copies them in a callback that HotReloadDefs forces before
    /// PlayDataLoader.ResetStaticDataPost, which also runs at startup and after a language change reload). A new
    /// Game is constructed for every new game and every load, which covers defs changed from the main menu (mod
    /// settings). Defs changed by a mod in the middle of a game are not detected; optimizations that cache def data
    /// say so in their correctness comment.
    ///
    /// The hooked bodies are irrelevant to every optimization's exactness (only the moment matters), so the targets
    /// are hook-only: not fingerprinted.
    ///
    /// Each optimization passes its own static reset method (a postfix; it should just call ClearCaches' body), so
    /// unpatching one optimization never removes another's hook.
    /// </summary>
    public static class CacheResets
    {
        public static IEnumerable<PatchSpec> Patches(Type owner, string resetMethod)
        {
            yield return PatchSpec.For(AccessTools.Method(typeof(PlayDataLoader), "ResetStaticDataPost"))
                .WithPostfix(owner, resetMethod).AsHook();
            yield return PatchSpec.For(AccessTools.Constructor(typeof(Game), Type.EmptyTypes))
                .WithPostfix(owner, resetMethod).AsHook();
        }
    }
}
