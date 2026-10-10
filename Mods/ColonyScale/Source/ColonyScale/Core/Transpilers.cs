using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ColonyScale
{
    public static class Transpilers
    {
        /// <summary>
        /// Replaces every call to <paramref name="from"/> with a call to static <paramref name="to"/> (same stack
        /// shape: instance methods map to a static whose first parameter is the instance). Throws if the number
        /// of replaced calls differs from <paramref name="expected"/>, so a changed game method fails loudly.
        /// </summary>
        public static IEnumerable<CodeInstruction> SwapCalls(IEnumerable<CodeInstruction> instructions, string optimizationId,
            MethodInfo from, MethodInfo to, int expected)
        {
            var list = instructions.ToList();
            int swapped = 0;
            foreach (var ins in list)
            {
                if (ins.Calls(from))
                {
                    ins.opcode = System.Reflection.Emit.OpCodes.Call;
                    ins.operand = to;
                    swapped++;
                }
            }
            if (swapped != expected)
                throw new PatternNotFoundException(optimizationId, $"expected {expected} call(s) to {from.DeclaringType?.Name}.{from.Name}, found {swapped}");
            return list;
        }
    }
}
