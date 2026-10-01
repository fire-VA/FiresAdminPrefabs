using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Estimates the rendered height of a hint string for a given font size and width,
    // for the monster and chest preset UIs. Counting only explicit newlines under-sized
    // any long single-line hint that Unity wrapped onto several visual lines, so it bled
    // into the next row. This counts wrapped lines too. The constants are approximations
    // tuned for the UIs' default font and deliberately err toward extra padding over a
    // clipped last line.
    public static class VAHintHeightEstimator
    {
        // Average glyph width as a fraction of font size. LegacyRuntime
        // at fontSize=11 measures ~6px wide per character on average
        // across mixed alphanumeric/punctuation content, so 0.55 is the
        // conservative-rounded ratio (errs slightly wide = fewer chars
        // per line = more wrapped lines = taller row = extra padding
        // instead of overflow).
        private const float AvgCharWidthRatio = 0.55f;

        // Line height as a fraction of font size. Unity's Text uses
        // ~1.0 line height by default but adds a small leading; 1.3
        // covers the case where consecutive lines have descenders +
        // ascenders that need clearance.
        private const float LineHeightRatio = 1.3f;

        // Estimate the rendered height of `hint` when wrapped to
        // `availableWidth` pixels at `fontSize` points.
        public static float Estimate(string hint, float availableWidth, int fontSize)
        {
            if (string.IsNullOrEmpty(hint)) return 0f;
            if (availableWidth <= 1f) availableWidth = 200f;   // sane fallback if layout isn't ready

            float avgCharWidth = fontSize * AvgCharWidthRatio;
            float lineHeight = fontSize * LineHeightRatio;
            int charsPerLine = Mathf.Max(1, Mathf.FloorToInt(availableWidth / avgCharWidth));

            int totalLines = 0;
            // Split on explicit newlines first; each segment may then
            // wrap to multiple visual lines.
            var explicitLines = hint.Split('\n');
            for (int i = 0; i < explicitLines.Length; i++)
            {
                int len = explicitLines[i].Length;
                if (len == 0)
                {
                    totalLines += 1;   // a blank line still takes vertical space
                    continue;
                }
                // ceil(len / charsPerLine) — every wrap adds a visual row.
                int wrapped = (len + charsPerLine - 1) / charsPerLine;
                totalLines += wrapped;
            }
            return totalLines * lineHeight;
        }
    }
}
