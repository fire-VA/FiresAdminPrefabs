using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VerdantsAscent.Utilities
{
    // Parser/serializer for the multi-line loot textbox shared by the
    // monster and chest preset UIs.
    //
    // One item per line; fields are comma-separated:
    //   prefab, stack-min;stack-max, chance, quality, variant
    // Only `prefab` is required. Blank lines and lines starting with `#`
    // are skipped so admins can leave inline comments. Round-trip omits
    // trailing defaults so unchanged entries don't bloat with empty commas.
    public static class VALootTextFormat
    {
        private const float DefaultChance = 1f;
        private const float ChanceEqualityEpsilon = 0.0001f;

        public const string HintText =
            "Format: prefab, stack-min;stack-max, chance, quality, variant\n" +
            "Only prefab is required. Examples:\n" +
            "  Iron, 1;3, 0.5         — 1-3 iron, 50% chance\n" +
            "  Coal, 5;10             — 5-10 coal, always present (chance defaults to 1.0)\n" +
            "  SwordIron, 1, 0.1, 2   — one quality-2 iron sword, 10% chance\n" +
            "When 'Pick N from pool' is empty, each line rolls its own chance " +
            "independently. When set, EWP picks N items from the pool weighted " +
            "by each chance value.";

        public static List<VAItemEntry> Parse(string text)
        {
            var result = new List<VAItemEntry>();
            if (string.IsNullOrEmpty(text)) return result;
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("#")) continue;

                // Admins commonly type whitespace-only between fields when
                // the value already contains a semicolon range. Vanilla
                // prefab names never contain spaces, so falling back to
                // whitespace-split when no comma is present is safe.
                string[] parts = line.Split(',');
                if (parts.Length == 1)
                {
                    parts = line.Split(
                        new[] { ' ', '\t' },
                        StringSplitOptions.RemoveEmptyEntries);
                }

                var item = new VAItemEntry { prefab = parts[0].Trim() };
                if (string.IsNullOrEmpty(item.prefab)) continue;

                if (parts.Length > 1)
                {
                    var stack = parts[1].Trim();
                    if (!string.IsNullOrEmpty(stack)) item.stack = stack;
                }
                if (parts.Length > 2)
                {
                    var chanceTxt = parts[2].Trim();
                    if (!string.IsNullOrEmpty(chanceTxt)
                        && float.TryParse(chanceTxt, NumberStyles.Float, CultureInfo.InvariantCulture, out var c))
                    {
                        item.chance = c;
                    }
                }
                if (parts.Length > 3)
                {
                    var quality = parts[3].Trim();
                    if (!string.IsNullOrEmpty(quality)) item.quality = quality;
                }
                if (parts.Length > 4)
                {
                    var variant = parts[4].Trim();
                    if (!string.IsNullOrEmpty(variant)) item.variant = variant;
                }
                result.Add(item);
            }
            return result;
        }

        public static string Serialize(List<VAItemEntry> items)
        {
            if (items == null || items.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            foreach (var it in items)
            {
                if (it == null || string.IsNullOrEmpty(it.prefab)) continue;
                sb.Append(it.prefab);

                bool hasStack = !string.IsNullOrEmpty(it.stack);
                bool hasChance = Math.Abs(it.chance - DefaultChance) > ChanceEqualityEpsilon;
                bool hasQuality = !string.IsNullOrEmpty(it.quality);
                bool hasVariant = !string.IsNullOrEmpty(it.variant);
                int emitUpTo = 0;
                if (hasStack) emitUpTo = 1;
                if (hasChance) emitUpTo = 2;
                if (hasQuality) emitUpTo = 3;
                if (hasVariant) emitUpTo = 4;

                if (emitUpTo >= 1) sb.Append(", ").Append(it.stack ?? string.Empty);
                if (emitUpTo >= 2) sb.Append(", ").Append(it.chance.ToString("0.###", CultureInfo.InvariantCulture));
                if (emitUpTo >= 3) sb.Append(", ").Append(it.quality ?? string.Empty);
                if (emitUpTo >= 4) sb.Append(", ").Append(it.variant ?? string.Empty);
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
