using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ColonyScale
{
    /// <summary>
    /// Verify mode: optimizations that support it compute their fast answer, still run vanilla,
    /// and record whether both agree. Used by the headless harness (`verify=true`) and dev mode.
    /// </summary>
    public static class Verify
    {
        public static bool Enabled;

        private static readonly Dictionary<string, long[]> counts = new Dictionary<string, long[]>();

        public static void Record(string optimizationId, bool matched)
        {
            if (!counts.TryGetValue(optimizationId, out var c)) counts[optimizationId] = c = new long[2];
            c[matched ? 0 : 1]++;
            if (!matched && c[1] <= 5) CSLog.Warning($"verify mismatch in {optimizationId} (#{c[1]})");
        }

        public static long TotalMismatches => counts.Values.Sum(c => c[1]);

        public static void Reset() => counts.Clear();

        public static string Report()
        {
            var sb = new StringBuilder("verify results (checks / mismatches):\n");
            foreach (var kv in counts.OrderBy(k => k.Key))
                sb.Append("  ").Append(kv.Key).Append(": ").Append(kv.Value[0] + kv.Value[1]).Append(" / ").Append(kv.Value[1]).Append('\n');
            return sb.ToString();
        }
    }
}
