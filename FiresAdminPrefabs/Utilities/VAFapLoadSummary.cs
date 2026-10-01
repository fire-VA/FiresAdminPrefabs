using System;
using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // VAFapLoadSummary — emoji-headed mini-banners that replace per-item
    // load logging with a single visual summary per batch. Each banner is
    // a 3-5 line mini-box drawn with box-drawing characters and tagged
    // [LoadSummary] so the shared FiresLogColorPatch can color it. Uses
    // Debug.Log (not direct console writes) because each box is single-
    // color, so the summary also lands in the log file + server stdout.
    public static class VAFapLoadSummary
    {
        // Class tag — routed to a color by FiresLogColorPatch's keyword rules.
        private const string Tag = "[LoadSummary]";

        // Whether per-item logs should be emitted. Returns false (silent) when
        // ConfigManager isn't ready yet, which is the safe default.
        public static bool VerboseEnabled
        {
            get
            {
                try
                {
                    var cfg = ConfigManager.Instance;
                    if (cfg != null && cfg.configVerboseLogging != null)
                        return cfg.configVerboseLogging.Value;
                }
                catch { }
                return false;
            }
        }

        // 📦 BUNDLES — external asset-bundle loader completion banner.
        public static void EmitBundles(int accepted, int rejected, int prefabs)
        {
            EmitMiniBox("📦 BUNDLES", new[]
            {
                $"{accepted} loaded · {rejected} skip",
                $"{prefabs} prefabs ready",
            });
        }

        // 📐 BLUEPRINTS — hammer-blueprint integration completion banner.
        // Aggregates parse and ghost-prewarm stats into one visual unit.
        public static void EmitBlueprints(int parsed, int failed, int baked)
        {
            EmitMiniBox("📐 BLUEPRINTS", new[]
            {
                $"{parsed} parsed",
                $"{baked} ghosts baked",
                $"{failed} failed",
            });
        }

        // 🖼 ICONS DONE — icon render-queue drain outcome, aggregated across
        // every drain wave into one debounced banner. attempted is the true
        // denominator (rendered + noMesh skips + unexpected); waves is how many
        // drain cycles folded into this banner.
        // saved/saveFailed are the PNGs that actually reached IconCache. A render that never
        // lands is re-done every world load, and the box used to report only the render, so the
        // two numbers disagreeing was invisible.
        // The save queue drains slower than the render queue, so pending is reported too — this
        // box fires when rendering finishes, not when the last PNG has landed.
        public static void EmitIconDrain(int rendered, int attempted, int noMesh, int unexpected, int waves, float seconds,
                                         int saved, int saveFailed, int pendingSaves)
        {
            var lines = new List<string>
            {
                $"{attempted} drained · {waves}w",
                $"{rendered} rendered",
                $"{noMesh} skip (no mesh)",
            };
            if (unexpected > 0) lines.Add($"⚠ {unexpected} unexpected");
            if (rendered > 0 || saved > 0 || saveFailed > 0 || pendingSaves > 0)
            {
                if (saveFailed > 0) lines.Add($"⚠ {saved} cached · {saveFailed} failed");
                else if (pendingSaves > 0) lines.Add($"{saved} cached · {pendingSaves} pending");
                else lines.Add($"{saved} cached to disk");
            }
            lines.Add($"in {seconds:F1}s");
            EmitMiniBox("🖼 ICONS DONE", lines.ToArray());
        }

        // 📡 FAP SYNC — client-join asset-sync completion banner, fired once
        // when the server's TransferDone arrives. bytesLabel is pre-formatted
        // by the caller (e.g. "3.2 MB").
        public static void EmitSync(int checkedFiles, int pulled, string bytesLabel, float seconds)
        {
            EmitMiniBox("📡 FAP SYNC", new[]
            {
                $"{checkedFiles} file(s) checked",
                $"{pulled} pulled · {bytesLabel}",
                $"took {seconds:F1}s",
            });
        }

        // 🎭 STAND-INS — registry stand-in completion. Single banner
        // covering all three stand-in categories (items, monsters,
        // clutter) since they all run during the same load phase.
        public static void EmitStandins(int items, int monsters, int clutter)
        {
            EmitMiniBox("🎭 STAND-INS", new[]
            {
                $"{items} items",
                $"{monsters} monsters",
                $"{clutter} clutter",
            });
        }

        // 🧱 BAKED — blueprint mesh-combine completion banner, fired once
        // per bake. bakeSeconds excludes async disk publish; liveChildren
        // counts pieces left live (interactives, signs, vegetation, etc.)
        // and not combined into the mesh.
        public static void EmitBake(string blueprintName, int pieces, int materials, int tris, float bakeSeconds, int liveChildren)
        {
            string trisLabel = FormatTriCount(tris);
            string nameLine = string.IsNullOrEmpty(blueprintName)
                ? "(unnamed blueprint)"
                : $"'{blueprintName}'";
            EmitMiniBox("🧱 BAKED", new[]
            {
                nameLine,
                $"{pieces} pieces · {materials} mats",
                $"{trisLabel} combined",
                $"{FormatBakeSeconds(bakeSeconds)} · {liveChildren} live child",
            });
        }

        // 🔥 REBAKING — per-bake progress banner from RebakeAllCoroutine.
        // Uses the [Rebake] tag so FiresLogColorPatch routes it to a
        // different color than the [LoadSummary] banners.
        public static void EmitRebakeStart(int step, int total, string cacheKey, int pieceHint)
        {
            string title = $"🔥 REBAKING {step}/{total}";
            string nameLine = string.IsNullOrEmpty(cacheKey)
                ? "(unnamed)"
                : $"'{cacheKey}'";
            EmitMiniBoxWithTag(RebakeTag, title, new[]
            {
                nameLine,
                $"{pieceHint} source pieces",
            });
        }

        // 🔥 REBAKE DONE — end-of-sweep summary banner from
        // RebakeAllCoroutine. Uses the [Rebake] tag for distinct coloring.
        public static void EmitRebakeAllDone(int ok, int failed, int skipped, int total, float wallSeconds)
        {
            EmitMiniBoxWithTag(RebakeTag, "🔥 REBAKE DONE", new[]
            {
                $"{ok}/{total} ok",
                $"{failed} failed · {skipped} skipped",
                $"took {FormatBakeSeconds(wallSeconds)}",
                "relaunch to use",
            });
        }

        // Distinct tag for rebake-progress banners, routed by
        // FiresLogColorPatch's "Rebake" keyword rule to a separate color.
        private const string RebakeTag = "[Rebake]";

        // Formats a bake wall-clock time: seconds with one decimal under a
        // minute, mm:ss above it.
        private static string FormatBakeSeconds(float seconds)
        {
            if (seconds < 0f) return "0.0s";
            if (seconds < 60f) return $"{seconds:F1}s";
            int mins = (int)(seconds / 60f);
            int secs = (int)(seconds - mins * 60f);
            return $"{mins}m{secs:D2}s";
        }

        // 🔨 PLACING — fires at the START of every blueprint placement,
        // before pieces spawn / bake runs; pairs with the BAKED banner.
        // bakeStatus is "cached", "live bake", or "no bake".
        public static void EmitPlacing(
            string blueprintName,
            int pieces,
            int veggies,
            int missingPrefabs,
            string healthLabel,
            string bakeStatus,
            string groupId)
        {
            string nameLine = string.IsNullOrEmpty(blueprintName)
                ? "(unnamed blueprint)"
                : $"'{blueprintName}'";
            string countsLine = veggies > 0
                ? $"{pieces} pieces · {veggies} veg"
                : $"{pieces} pieces";
            if (missingPrefabs > 0)
                countsLine += $" · {missingPrefabs} missing";
            string groupTail = !string.IsNullOrEmpty(groupId) && groupId.Length >= 6
                ? groupId.Substring(groupId.Length - 6)
                : "?";
            EmitMiniBox("🔨 PLACING", new[]
            {
                nameLine,
                countsLine,
                healthLabel ?? "health: ?",
                $"bake: {bakeStatus} · g:{groupTail}",
            });
        }

        // 🧱 BUNDLES BAKED — VABakeBundle.RegisterAll completion banner,
        // emitted once the whole scan finishes. tookS is the scan duration.
        // mainThreadMs is the slice of tookS that actually ran on the main thread; the rest is
        // waiting on the manifest reader, which costs the frame nothing. They diverge hard, and
        // only the first number is the one that starves vanilla's async loads.
        public static void EmitBundlesBaked(int ok, int bad, float tookS, float mainThreadMs)
        {
            EmitMiniBox("🧱 BUNDLES BAKED", new[]
            {
                $"{ok} prefab(s) ready",
                $"{bad} failed",
                $"took {tookS:F1}s",
                $"main thread {mainThreadMs:F0}ms",
            });
        }

        // 🎨 MATERIAL RECOVERY — a baked blueprint built with material names that no longer
        // resolve. Substituted slots reuse the part's dominant material; invisible parts had
        // nothing to fall back on and stay hidden until the blueprint is re-baked.
        public static void EmitMaterialRecovery(string blueprintName, int partsSubstituted, int slotsPatched, int partsInvisible)
        {
            var lines = new System.Collections.Generic.List<string>(4) { $"'{blueprintName}'" };
            if (partsSubstituted > 0)
            {
                lines.Add($"{slotsPatched} slot(s) substituted");
                lines.Add($"in {partsSubstituted} part(s)");
            }
            if (partsInvisible > 0) lines.Add($"{partsInvisible} part(s) invisible");
            EmitMiniBox("🎨 MATERIAL RECOVERY", lines.ToArray());
        }

        // ⚠️ VANILLA NOISE — single-banner summary of vanilla "Double
        // ZNetview" warns and "Destroyed invalid predab" logs that FAP
        // intercepted and suppressed this session (both are harmless
        // vanilla noise, not FAP bugs). Fires on a cooldown.
        public static void EmitVanillaNoise(int doubleZNet, int orphanZdos, string topPrefabNote)
        {
            var lines = new System.Collections.Generic.List<string>(4);
            if (doubleZNet > 0) lines.Add($"{doubleZNet} dbl-ZNetView");
            if (orphanZdos > 0) lines.Add($"{orphanZdos} orphan ZDOs");
            if (!string.IsNullOrEmpty(topPrefabNote)) lines.Add($"top: {topPrefabNote}");
            lines.Add("(not a FAP bug)");
            EmitMiniBox("⚠️ VANILLA NOISE", lines.ToArray());
        }

        // 🔧 BUNDLE REPAIRS — what the loader had to fix on third-party bundle
        // prefabs so they behave in game. Counts only: the "why" belongs in the
        // code at each repair site, not re-printed to the console every launch.
        //
        // Singleton strips: prefabs shipped with EnvMan/Minimap/GlobalWind baked
        // in null the engine's singletons when cloned, and vanilla Update methods
        // then NRE every frame. EffectArea heals: vanilla EffectArea.Awake needs a
        // sibling Collider and NREs without one. Both mean the bundle wants a
        // re-export; neither is worth a paragraph per launch.
        public static void EmitBundleRepairs(int singletonStrips, int effectAreaHeals, int bundles)
        {
            if (singletonStrips <= 0 && effectAreaHeals <= 0) return;

            var lines = new System.Collections.Generic.List<string>(4);
            if (singletonStrips > 0) lines.Add($"{singletonStrips} singleton strip(s)");
            if (effectAreaHeals > 0) lines.Add($"{effectAreaHeals} EffectArea heal(s)");
            lines.Add($"across {bundles} bundle(s)");
            lines.Add("authors: re-export");
            EmitMiniBox("🔧 BUNDLE REPAIRS", lines.ToArray());
        }

        // 🛑 MISSING SCRIPTS — per-bundle summary of prefabs rejected for
        // missing scripts. totalSlots is the sum of broken slots across them.
        public static void EmitMissingScripts(string bundleName, int rejected, int totalSlots)
        {
            EmitMiniBox("🛑 MISSING SCRIPTS", new[]
            {
                $"'{bundleName}'",
                $"{rejected} prefab(s) rejected",
                $"{totalSlots} broken slot(s)",
            });
        }

        // Formats a triangle count for the bake banner. < 1000 → "N
        // tris"; < 1M → "12.3K tris"; >= 1M → "1.14M tris". Keeps the
        // banner body line short enough for the box even for megablueprints.
        private static string FormatTriCount(int tris)
        {
            if (tris < 0) return "0 tris";
            if (tris < 1_000) return $"{tris} tris";
            if (tris < 1_000_000) return $"{tris / 1000.0:F1}K tris";
            return $"{tris / 1_000_000.0:F2}M tris";
        }

        // Renders a generic mini-box. Core owns the width and the framing.
        private static void EmitMiniBox(string title, string[] lines)
            => EmitMiniBoxWithTag(Tag, title, lines);

        // Tag-parameterized variant, letting specific emitters pick a
        // non-default tag for distinct FiresLogColorPatch coloring.
        // Core builds the box; we emit it. The width model — cells per text element, wide BMP
        // pictographs, the VS16 rule and the BannerEmojiColumns override — lives in
        // FiresCore.Logging.LoadSummary exactly once, so a terminal that disagrees is one config
        // line from fixed across every Fires mod instead of per renderer.
        //
        // We deliberately do NOT call LoadSummary's own Emit methods: those log through CORE's
        // BepInEx source, and FiresLogColorPatch colours by source name while the console prefixes
        // by it, so every FAP box would print as FiresUnifiedCore. Format there, emit here.
        //
        // What this replaced: a private copy that counted a bare astral emoji as two cells and any
        // BMP pictograph (⚙, ⚠) as one, and truncated body lines by UTF-16 code units. The title
        // arithmetic happened to agree with the terminal for the emoji FAP uses; the BMP case would
        // have drawn the top border a cell short the moment one was used.
        private static void EmitMiniBoxWithTag(string tag, string title, string[] lines)
        {
            try
            {
                foreach (var line in FiresCore.Logging.LoadSummary.FormatMiniBox(title, lines))
                    EmitLine(tag, line);
            }
            catch (Exception ex)
            {
                try { EmitLine(tag, $"(summary render failed: {ex.Message}) {title}: {string.Join(", ", lines ?? new string[0])}"); }
                catch { }
            }
        }

        // Routes a summary line through FAP's BepInEx log source rather than
        // Debug.Log. Going through Unity's Debug.Log makes the line ALSO echo to
        // stdout — a raw, un-prefixed white duplicate in the BepInEx console — on
        // top of BepInEx's own formatted capture. Emitting from the BepInEx source
        // prints it exactly once; FiresLogColorPatch colours it by the Fires source
        // name. Falls back to Debug.Log only before the source is wired (pre-Awake),
        // keeping the [PluginName] tag so the fallback still colours + attributes.
        private static void EmitLine(string tag, string line)
        {
            var log = VaPieces.Log;
            if (log != null) log.LogInfo($"{tag} {line}");
            else Debug.Log($"[{VaPieces.PluginName}] {tag} {line}");
        }
    }
}
