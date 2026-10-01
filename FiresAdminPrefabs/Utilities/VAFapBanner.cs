using System;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // VAFapBanner — emits a colored ASCII logo banner once on mod load.
    // Uses direct console-stream writes (not Debug.Log) because a single
    // banner line carries MULTIPLE colors, which the shared
    // FiresLogColorPatch one-color-per-line path can't produce.
    // Falls back to plain Debug.Log when reflection into ConsoleManager fails.
    public static class VAFapBanner
    {
        // One contiguous run of same-colored text within a line.
        private readonly struct Segment
        {
            public readonly string Text;
            public readonly ConsoleColor Color;
            public Segment(string text, ConsoleColor color) { Text = text; Color = color; }
        }

        // Lazy-init reflection cache, populated by EnsureReflection.
        private static bool s_reflectionResolved;
        private static Func<object> s_consoleStreamGetter;
        private static Action<ConsoleColor> s_setConsoleColor;

        // Color shortcuts for readability in the segment table.
        private const ConsoleColor Frame   = ConsoleColor.DarkGray;
        private const ConsoleColor Done1   = ConsoleColor.Green;       // 1st loaded box
        private const ConsoleColor Done2   = ConsoleColor.Yellow;      // 2nd loaded box
        private const ConsoleColor Active  = ConsoleColor.DarkYellow;  // currently-loading box
        private const ConsoleColor Empty   = ConsoleColor.DarkGray;    // unfilled portion of loading box
        private const ConsoleColor TipHot  = ConsoleColor.Yellow;      // top flame tip (bright)
        private const ConsoleColor TipMid  = ConsoleColor.DarkYellow;  // middle flame row (orange)
        private const ConsoleColor TipBase = ConsoleColor.Red;         // flame base (hot core)
        private const ConsoleColor Brand   = ConsoleColor.Cyan;        // FAP big text
        private const ConsoleColor Tagline = ConsoleColor.Magenta;     // matches Fires default log color

        // Initial capacity for the per-line segment buffer in the big banner.
        private const int BigBannerLineBufferCapacity = 128;

        // Banner content as a 2D segment grid. Each inner array is one
        // line; an empty inner array is a blank line.
        private static readonly Segment[][] s_lines =
        {
            // 5-line flame crown, gradient top-to-bottom Yellow → DarkYellow → Red.
            new[] { new Segment("              🔥",            TipHot) },
            new[] { new Segment("           🔥 🔥 🔥",          TipHot) },
            new[] { new Segment("            🔥🔥🔥",           TipMid) },
            new[] { new Segment("          🔥🔥🔥🔥🔥",         TipMid) },
            new[] { new Segment("            🔥🔥🔥",           TipBase) },

            // Frame top.
            new[] { new Segment("   ╔══════════════════════╗", Frame) },

            // Box tops — three ┌───┐ headers in gradient frame colors.
            new[]
            {
                new Segment("   ║  ",  Frame),
                new Segment("┌───┐",   Done1),
                new Segment("  ",      Frame),
                new Segment("┌───┐",   Done2),
                new Segment("  ",      Frame),
                new Segment("┌───┐",   Active),
                new Segment(" ║",      Frame),
            },

            // Fill row — first two boxes full, third half-filled (█░░).
            new[]
            {
                new Segment("   ║  ",  Frame),
                new Segment("│███│",   Done1),
                new Segment("  ",      Frame),
                new Segment("│███│",   Done2),
                new Segment("  ",      Frame),
                new Segment("│",       Active),
                new Segment("█",       Active),
                new Segment("░░",      Empty),
                new Segment("│",       Active),
                new Segment(" ║",      Frame),
            },

            // Box bottoms — matching └───┘ closers in the same gradient.
            new[]
            {
                new Segment("   ║  ",  Frame),
                new Segment("└───┘",   Done1),
                new Segment("  ",      Frame),
                new Segment("└───┘",   Done2),
                new Segment("  ",      Frame),
                new Segment("└───┘",   Active),
                new Segment(" ║",      Frame),
            },

            // Frame bottom.
            new[] { new Segment("   ╚══════════════════════╝", Frame) },

            // Brand line + tagline (blank line between).
            new[] { new Segment("              FAP",           Brand) },
            new Segment[0],
            new[] { new Segment("   Fires Admin Prefabs Loaded", Tagline) },
        };

        // Big ASCII-art logo banner: per-line Zone tags say what each row
        // depicts; ColorForChar then picks a per-character shade so one
        // line can carry multiple colors. Fires at the START of Awake.
        private enum Zone
        {
            Blank,
            FlameTip,       // small flame tips at top — bright Yellow
            FlameMid,       // spreading flame body — DarkYellow + Red
            FlameBody,      // densest hot core — Red + DarkRed
            FlameWrap,      // flame wrapping down around the gear sides
            GearInterior,   // dark shield interior with the 3 blue cubes
            GearOutline,    // gear ring at bottom — UNIFORM DarkGray
            GearBase,       // bottom band of the gear
            Margin,         // tiny `@` left-margin artifact rows
            BrandTop,       // FAP letters — top half (Cyan)
            BrandShadow,    // FAP letters — DarkCyan shadow row
            BrandPeak,      // FAP letters — White chrome peak row
            BrandBottom,    // FAP letters — bottom half (Cyan/DarkCyan)
        }

        private static readonly (string text, Zone zone)[] s_bigLines = new (string, Zone)[]
        {
            ("", Zone.Blank),

            // Small flame tips at the top.
            ("                                      @@@%@@@",                                               Zone.FlameTip),
            ("                                      @%%%%%%@@",                                             Zone.FlameTip),
            ("                                       @%%%%%%%%@@",                                          Zone.FlameTip),
            ("                                       %@%%%##%%%%%%@@",                                      Zone.FlameTip),

            // Spreading flame body (hot core emerging).
            ("                                        @%%%%=-+*#%%%%%@@",                                   Zone.FlameMid),
            ("                                       @%%%%%#--=+***%%%%%%@",                                Zone.FlameMid),
            ("                                    @@%%%%%%%%----+****#%%%%%@@",                             Zone.FlameMid),
            ("                        @@%@%%%%%%%%%%%%%%%%%#-----++++++#%%%%%%%%@@@@@@@",                   Zone.FlameMid),
            ("                     @@%%%%%%%%%%%%%%%%%%%%%%+-----=++++++*%%%%%%%%%%%%%%%%%@",               Zone.FlameMid),

            // Wide hot flame body (densest core).
            ("                   @%%%%%%%%################+=======+++++++*###########%%%%%%%%@",            Zone.FlameBody),
            ("                 @%%%%%%###################+========+++++++*%##############%%%%%%@",          Zone.FlameBody),
            ("               @%%%%%%###################*==========++++++++%%###############%%%%%@@",        Zone.FlameBody),
            ("               %%%%%############+=*#####+===========+++++++*%%####++*##########%%%%@",        Zone.FlameBody),
            ("               %%%%%#########*=--=*###*============++++++++*%%####==**#########%%%%%@",       Zone.FlameBody),
            ("               %%%%#########=-----*%#+================++++++%%%##+--+***########%%%%%",       Zone.FlameBody),
            ("               %%%%#######*-------++=======================+++==----+****#%####%%%%%%",       Zone.FlameBody),

            // Flame wrapping down around the gear sides.
            ("               %%%%%%%%%%+---------==========================------=+++***#%%%%%%%%%%",       Zone.FlameWrap),
            ("               %%%%%%%%%*-----------========*#########*+=====-----=+++++++*%%%%%%%%%%@",      Zone.FlameWrap),
            ("               %%%%%%%%#=------------======*%%%%%%%%%%#+========-=+++++++++#%%%%%%%%%%",      Zone.FlameWrap),
            ("              %%%%%%%%%+------------======+#%%%%%%%%%%%#+=======+++++++++++#%%%%%%%%%%",      Zone.FlameWrap),
            ("              %%%%%%%%%+-------+*+++==+*##%%%%#+==+*#%%%%%#*++++***++++++++*%%%%%%%%%%",      Zone.FlameWrap),
            ("              %%%%%%%%%+------*%%%%%%%%%%%%%+========+*%%%%%%%%%%%%#*++++++#%%%%%%%%%%",      Zone.FlameWrap),
            ("              %%%%%%%%%+----=#%%%%%%%%%%%%%%*+++++****#%%%%%%%%%%%%%%*+++++%%%%%%%%%%%",      Zone.FlameWrap),
            ("              %%%%%%%%%#---+#%%%%%%%%%%%%%%%*++++*****#%%%%%%%%%%%%%%%#+++#%%%%%%%%%%%",      Zone.FlameWrap),

            // Dark gear face with the 3 blue prefab cubes.
            ("              %%%%%%%%%%#:-+%%%%%%%%%%%%#+*#**#*+**#*+##++#%%%%%%%%%%%#++*%%%%%%%%%%%%",      Zone.GearInterior),
            ("              %%%%%%%%%%%#----*%%%%%#+=====+==*%@@@%+=++=====*#%%%%%*===*%%%%%%%%%%%%%",      Zone.GearInterior),
            ("              %%%%%%%%%%%%%+--=#%%%%*+++===+***#%%%#+++===++**#%%%%#==+%%%%%%%%%%%%%%%",      Zone.GearInterior),
            ("              %%%%%%%%%%%%%%%*=#%%%%*++++*******###*+++++*****#%%%%#+#%%%%%%%%%%%%%%%%",      Zone.GearInterior),
            ("              %%%%%%%%%%%%%%##%%%%%%%#*+++***#%%%%%%#*+++***##%%%%%%%#%%%%%%%%%%%%%%%",       Zone.GearInterior),

            // Gear ring — uniform DarkGray.
            ("               %%%%%%%%%%%%%%%%%%%%%%%%@%#%%%%%%%%%%%%@%#%%%%%%%%%%%%%%#%%%%%%%%%%%%%",       Zone.GearOutline),
            ("               %%%%%%%%%%%%@%%%%%%%%%%%%%@@%%%%%%%%%%%%%@@%%%%%%%%%%%%%@%%%%%%%%%%%%",        Zone.GearOutline),
            ("               %%%%%%%%%%%%%@@%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%@@%%%%%%%%%%%%",         Zone.GearOutline),
            ("                %%%%%%%%%%%%%@@%%%%%@@@%%%%%%%%%%%%%%%%%%%%%@@@%%%%%@@%%%%%%%%%%%%",          Zone.GearOutline),
            ("                 %%%%%%%%%%%%%%@@@@@@@@@@@@%%%%%%%%%%%%%@@@@@@@@@@@@@%%%%%%%%%%%%",           Zone.GearOutline),
            ("                   %%%%%%%%%%%%%%%%%%%%%@@@%%%%%%%%%%%%@@@@%%%%%%%%%%%%%%%%%%%%%",            Zone.GearOutline),
            ("                      %%%%%%%%#######################################%%%%%%%%",               Zone.GearBase),
            ("                            %%%%%%##############################%%%%%%%%%",                   Zone.GearBase),

            // Margin @ artifacts on the left.
            ("@",                                                                                            Zone.Margin),
            ("@",                                                                                            Zone.Margin),

            // Blank separator before FAP letters.
            ("", Zone.Blank),
            ("", Zone.Blank),

            // FAP letters — Cyan with a white chrome peak row.
            ("                             %#*+++===++*%   #*++#%     %#********##%",                       Zone.BrandTop),
            ("                            #-..::::::::=%  +=-====*    %++++++++++++*%",                     Zone.BrandShadow),
            ("                            #:.:=======+*% +==*##+==*   %%%%%%%%%##++*%",                     Zone.BrandTop),
            ("                           %#....::::::.=#===*   %*==*% %+=====+++++*%%",                     Zone.BrandPeak),
            ("                           %#..-#       #+==#%    %*+=*#%+++#%%%%%%%",                        Zone.BrandBottom),
            ("                            #==*%      %*+*#       %#**#%**#%",                                Zone.BrandShadow),
            ("", Zone.Blank),
        };

        // Picks the console color for a single character from the line's
        // Zone plus the character's bucket. `%` is its own (body) bucket so
        // a line can carry both a background tone and brighter detail chars.
        private static ConsoleColor ColorForChar(Zone zone, char c)
        {
            int bucket = CharBucket(c);

            switch (zone)
            {
                case Zone.FlameTip:
                    if (bucket == 0) return ConsoleColor.Yellow;
                    if (bucket == 1) return ConsoleColor.Yellow;
                    if (bucket == 2) return ConsoleColor.DarkYellow;
                    return ConsoleColor.Red;

                case Zone.FlameMid:
                    if (bucket == 0) return ConsoleColor.Red;
                    if (bucket == 1) return ConsoleColor.Yellow;
                    if (bucket == 2) return ConsoleColor.DarkYellow;
                    return ConsoleColor.DarkRed;

                case Zone.FlameBody:
                    if (bucket == 0) return ConsoleColor.DarkRed;
                    if (bucket == 1) return ConsoleColor.Yellow;
                    if (bucket == 2) return ConsoleColor.Red;
                    return ConsoleColor.DarkRed;

                case Zone.FlameWrap:
                    if (bucket == 0) return ConsoleColor.Red;
                    if (bucket == 1) return ConsoleColor.Yellow;
                    if (bucket == 2) return ConsoleColor.DarkYellow;
                    return ConsoleColor.DarkRed;

                case Zone.GearInterior:
                    if (bucket == 0) return ConsoleColor.Red;
                    if (bucket == 1) return ConsoleColor.Blue;
                    if (bucket == 2) return ConsoleColor.Blue;
                    return ConsoleColor.DarkRed;

                case Zone.GearOutline:
                    return ConsoleColor.DarkGray;

                case Zone.GearBase:
                    return ConsoleColor.DarkGray;

                case Zone.Margin:
                    return ConsoleColor.DarkGray;

                case Zone.BrandTop:
                case Zone.BrandBottom:
                    if (bucket == 0) return ConsoleColor.Cyan;
                    if (bucket == 1) return ConsoleColor.Cyan;
                    if (bucket == 2) return ConsoleColor.DarkCyan;
                    return ConsoleColor.DarkCyan;

                case Zone.BrandShadow:
                    return ConsoleColor.DarkCyan;

                case Zone.BrandPeak:
                    if (bucket == 0) return ConsoleColor.White;
                    if (bucket == 1) return ConsoleColor.White;
                    if (bucket == 2) return ConsoleColor.Cyan;
                    return ConsoleColor.DarkCyan;

                default:
                    return ConsoleColor.Gray;
            }
        }

        // Maps a character to a shading bucket: 0 = body (`%`),
        // 1 = highlight (`@#`), 2 = shade (`*+=`), 3 = shadow (`-:.`).
        // `%` is its own bucket so it can be painted as a background tone.
        private static int CharBucket(char c)
        {
            switch (c)
            {
                case '%':                          return 0;
                case '@': case '#':                return 1;
                case '*': case '+': case '=':      return 2;
                case '-': case ':': case '.':      return 3;
                default:                           return 0;
            }
        }

        // Compact "loaded" banner. Fires at the END of mod load from
        // Ascend.WaitForZNetReady. Tolerant of duplicate calls.
        public static void Print()
        {
            try { Debug.Log($"[{VaPieces.PluginName}] Fires Admin Prefabs Loaded."); }
            catch { }

            try
            {
                if (!TryWriteColored())
                    WritePlainFallback();
            }
            catch
            {
                try { WritePlainFallback(); } catch { }
            }
        }

        // Big ASCII-art "loading" banner. Fires at the START of mod load
        // from Ascend.Awake, before the smaller "loaded" banner at the end.
        public static void PrintBig()
        {
            try { Debug.Log($"[{VaPieces.PluginName}] Fires Admin Prefabs Loading..."); }
            catch { }

            try
            {
                if (!TryWriteColoredBig())
                    WritePlainFallbackBig();
            }
            catch
            {
                try { WritePlainFallbackBig(); } catch { }
            }
        }

        // Returns true if the colored banner rendered to the console;
        // false if reflection failed (caller falls back to plain Debug.Log).
        private static bool TryWriteColored()
        {
            EnsureReflection();
            if (s_consoleStreamGetter == null || s_setConsoleColor == null)
                return false;

            var stream = s_consoleStreamGetter() as TextWriter;
            if (stream == null) return false;

            try
            {
                s_setConsoleColor(ConsoleColor.Gray);
                stream.WriteLine();

                foreach (var line in s_lines)
                {
                    foreach (var segment in line)
                    {
                        s_setConsoleColor(segment.Color);
                        stream.Write(segment.Text);
                    }
                    stream.WriteLine();
                }
            }
            finally
            {
                try { s_setConsoleColor(ConsoleColor.Gray); } catch { }
            }
            return true;
        }

        // Reflection-free fallback — emits each line via Debug.Log with the
        // standard tag. Color is lost but the banner shape still renders.
        private static void WritePlainFallback()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var line in s_lines)
                {
                    sb.Clear();
                    foreach (var segment in line) sb.Append(segment.Text);
                    Debug.Log($"[{VaPieces.PluginName}] {sb}");
                }
            }
            catch { }
        }

        // Big-banner colored write — per-character Zone coloring. Buckets
        // consecutive same-color chars into one SetConsoleColor + Write call
        // so a single ASCII line can carry multiple colors.
        private static bool TryWriteColoredBig()
        {
            EnsureReflection();
            if (s_consoleStreamGetter == null || s_setConsoleColor == null)
                return false;

            var stream = s_consoleStreamGetter() as TextWriter;
            if (stream == null) return false;

            try
            {
                s_setConsoleColor(ConsoleColor.Gray);
                stream.WriteLine();

                var sb = new StringBuilder(BigBannerLineBufferCapacity);
                foreach (var line in s_bigLines)
                {
                    string text = line.text;
                    Zone zone = line.zone;

                    if (string.IsNullOrEmpty(text))
                    {
                        stream.WriteLine();
                        continue;
                    }

                    ConsoleColor pendingColor = ConsoleColor.Gray;
                    sb.Length = 0;

                    for (int i = 0; i < text.Length; i++)
                    {
                        char character = text[i];
                        ConsoleColor color = ColorForChar(zone, character);

                        if (sb.Length == 0)
                        {
                            pendingColor = color;
                        }
                        else if (color != pendingColor)
                        {
                            s_setConsoleColor(pendingColor);
                            stream.Write(sb.ToString());
                            sb.Length = 0;
                            pendingColor = color;
                        }
                        sb.Append(character);
                    }

                    if (sb.Length > 0)
                    {
                        s_setConsoleColor(pendingColor);
                        stream.Write(sb.ToString());
                    }
                    stream.WriteLine();
                }
            }
            finally
            {
                try { s_setConsoleColor(ConsoleColor.Gray); } catch { }
            }
            return true;
        }

        // Plain-fallback path for the big banner — emits each line via
        // Debug.Log so the file log captures the art without colors.
        private static void WritePlainFallbackBig()
        {
            try
            {
                foreach (var line in s_bigLines)
                    Debug.Log($"[{VaPieces.PluginName}] {line.text}");
            }
            catch { }
        }

        // Resolves BepInEx.ConsoleManager.ConsoleStream and SetConsoleColor
        // via reflection (both are `internal` in BepInEx 5.x) and caches them
        // as delegates. On failure the delegates stay null and callers fall
        // back to plain Debug.Log.
        private static void EnsureReflection()
        {
            if (s_reflectionResolved) return;
            s_reflectionResolved = true;
            try
            {
                var bepinExAssembly = typeof(BepInEx.Logging.ConsoleLogListener).Assembly;
                var consoleManagerType = bepinExAssembly.GetType("BepInEx.ConsoleManager", throwOnError: false);
                if (consoleManagerType == null) return;

                var streamProp = consoleManagerType.GetProperty("ConsoleStream",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (streamProp != null)
                {
                    var getMethod = streamProp.GetGetMethod(nonPublic: true);
                    if (getMethod != null)
                    {
                        s_consoleStreamGetter = (Func<object>)Delegate.CreateDelegate(
                            typeof(Func<object>), getMethod);
                    }
                }

                var setColorMethod = consoleManagerType.GetMethod("SetConsoleColor",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(ConsoleColor) },
                    modifiers: null);
                if (setColorMethod != null)
                {
                    s_setConsoleColor = (Action<ConsoleColor>)Delegate.CreateDelegate(
                        typeof(Action<ConsoleColor>), setColorMethod);
                }
            }
            catch
            {
            }
        }
    }
}
