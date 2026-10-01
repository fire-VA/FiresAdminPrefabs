using System.Globalization;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Parsers for the two numeric specs EWP's item tables use, shared by the monster
    // and chest preset appliers: a stack size and itemAmount's pick-N-from-M count,
    // both written as "3" or as a "2;4" range.
    //
    // Ranges roll here rather than being returned as a pair, because both callers want
    // a fresh roll per death / per chest fill.
    public static class VAItemSpec
    {
        public const char RangeSeparator = ';';

        // "3" -> 3, "2;4" -> a roll in [2,4], unset or malformed -> null, which both
        // callers read as "no pick-N, roll every entry against its own chance".
        public static int? RollCount(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return null;
            if (TryParseIntRange(spec, out int min, out int max))
                return Random.Range(min, max + 1);
            if (spec.IndexOf(RangeSeparator) > 0) return null;
            return int.TryParse(spec.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int single)
                ? new int?(single)
                : null;
        }

        // "3" -> 3, "2;4" -> a roll in [2,4], unset or malformed -> 1.
        public static int RollStack(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return 1;
            if (TryParseIntRange(spec, out int min, out int max))
                return Random.Range(min, max + 1);
            return int.TryParse(spec.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int single) ? single : 1;
        }

        public static bool TryParseIntRange(string spec, out int min, out int max)
        {
            min = 0;
            max = 0;
            if (string.IsNullOrEmpty(spec)) return false;
            int separator = spec.IndexOf(RangeSeparator);
            if (separator <= 0) return false;
            if (!int.TryParse(spec.Substring(0, separator).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out min)) return false;
            if (!int.TryParse(spec.Substring(separator + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out max)) return false;
            if (max < min) max = min;
            return true;
        }
    }
}
