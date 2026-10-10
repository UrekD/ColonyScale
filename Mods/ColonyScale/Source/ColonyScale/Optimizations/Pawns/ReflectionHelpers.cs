using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ColonyScale.Optimizations.Pawns
{
    /// <summary>Override detection used to classify game and mod subclasses at runtime.</summary>
    internal static class OverrideScan
    {
        private const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// True if any class from <paramref name="type"/> (inclusive) up to <paramref name="root"/> (exclusive), other than
        /// <paramref name="except"/>, declares an instance method named <paramref name="name"/>. Any signature counts, so
        /// overloads and hiding methods are reported too. Also true when the hierarchy cannot be read or never reaches
        /// <paramref name="root"/>: callers treat "true" as "unknown code may run" and fall back to vanilla.
        /// </summary>
        public static bool Overrides(Type type, Type root, string name, Type except = null)
        {
            try
            {
                for (Type c = type; c != null; c = c.BaseType)
                {
                    if (c == root) return false;
                    if (c == except) continue;
                    foreach (MethodInfo m in c.GetMethods(Declared))
                        if (m.Name == name) return true;
                }
            }
            catch (Exception)
            {
                // A mod type whose members cannot be loaded: assume the worst.
            }
            return true;
        }
    }

    /// <summary>List&lt;T&gt;._version: incremented by every mutation (Add, Insert, Remove*, Clear, indexer set, Sort, ...).</summary>
    internal static class ListVersion<T>
    {
        private static readonly AccessTools.FieldRef<List<T>, int> version = AccessTools.FieldRefAccess<List<T>, int>("_version");

        public static int Of(List<T> list) => version(list);
    }
}
