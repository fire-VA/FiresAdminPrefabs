using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;

namespace VerdantsAscent.Managers
{
    // 1.1.279 (Fire 10-20 config cleanup, via [lead]): how FAP's settings show in the settings UI, in ONE table. Nothing here
    // changes a cfg key or value: each entry gets a ConfigurationManagerAttributes tag (read by Core's settings UI and by
    // ConfigurationManager) with its section, plain name, one-line description and visibility:
    //   Shown   - Build menu (no geometry), Hammer toggles, every keybind, Persistence
    //   Hidden  - window geometry (saved, dragged with the handles, never shown), bookkeeping, retired features
    //   Advanced- everything else
    // Persistence greys out live: the EWP kinds while "Use EWP persistence" is off, "Use FAP persistence" while EWP is on or
    // "Use vanilla friendly" is set. Core re-reads ReadOnly every frame (CfgTags.IsReadOnlyNow), and a server lock still wins.
    internal static class FapConfigUi
    {
        private enum Show { Shown, Advanced, Hidden }

        private sealed class Rule
        {
            public Show Show; public string Category, Name, Desc; public int? Order; public Func<bool> Grey;
            public Rule(Show show, string category = null, string name = null, string desc = null, int? order = null, Func<bool> grey = null)
            { Show = show; Category = category; Name = name; Desc = desc; Order = order; Grey = grey; }
        }

        // Read by name (ConfigurationManagerAttributes) by Core's CfgTags and by ConfigurationManager.
        internal sealed class ConfigurationManagerAttributes
        {
            public bool? Browsable;
            public bool? IsAdvanced;
            public int? Order;
            public string Category;
            public string DispName;
            public string Description;
            internal Func<bool> Grey;
            internal object Prior;   // ServerSync's own attribute object: its ReadOnly is the server lock
            internal Func<string> Why;
            /// <summary>1.1.281: the reason text for a dependency grey-out (Core shows it instead of "Locked by the server" once it reads
            /// it, add-only ask to [generator]); null while the server lock is what makes the row read-only, or when nothing greys it.</summary>
            public string ReadOnlyReason
            {
                get
                {
                    try
                    {
                        if (Prior != null)
                        {
                            var t = Prior.GetType();
                            object v = (object)t.GetField("ReadOnly")?.GetValue(Prior) ?? t.GetProperty("ReadOnly")?.GetValue(Prior, null);
                            if (v as bool? ?? false) return null;
                        }
                        return Grey != null && Grey() ? Why?.Invoke() : null;
                    }
                    catch { return null; }
                }
            }
            public bool? ReadOnly
            {
                get
                {
                    bool locked = false;
                    try
                    {
                        if (Prior != null)
                        {
                            var t = Prior.GetType();
                            object v = (object)t.GetField("ReadOnly")?.GetValue(Prior) ?? t.GetProperty("ReadOnly")?.GetValue(Prior, null);
                            locked = v as bool? ?? false;
                        }
                    }
                    catch { }
                    bool grey = false;
                    try { grey = Grey != null && Grey(); } catch { }
                    return locked || grey;
                }
            }
        }

        private const string BuildMenu = "Build menu", Hammer = "Hammer toggles", Persist = "Persistence";
        private static readonly HashSet<string> GeometrySections = new HashSet<string>(StringComparer.Ordinal)
            { "ChestConfigUI", "ClutterConfigUI", "DataPresetUI", "ItemStandPresetUI", "LightPresetUI", "MonsterConfigUI" };

        private static bool EwpOff() => !(ConfigManager.Instance?.configUseEwpForPersistence?.Value ?? false);
        private static bool FapGrey() => !EwpOff() || (ConfigManager.Instance?.configVanillaFriendly?.Value ?? false);
        private static string EwpOffWhy() => "Needs Use EWP persistence on";
        private static string FapWhy() => !EwpOff() ? "Off while EWP persistence is on" : "Off while vanilla friendly is on";

        private static Dictionary<string, Rule> s_rules;
        private static Dictionary<string, Rule> Rules => s_rules ?? (s_rules = new Dictionary<string, Rule>(StringComparer.Ordinal)
        {
            // ---- Build menu (shown; the window size / position / zoom are hidden - players drag the handles)
            ["Build Menu|Hide dev prefabs"] = new Rule(Show.Shown, BuildMenu, "Hide dev / test prefabs", "Leave dev and test prefabs out of the build menu."),
            ["BuildMenu|SortVanillaFirst"] = new Rule(Show.Shown, BuildMenu, "Vanilla pieces first", "Every tab lists vanilla pieces before modded ones."),
            ["BuildMenu|HideAdminGatedPieces"] = new Rule(Show.Shown, BuildMenu, "Hide admin-only pieces", "Non-admins don't see pieces that need the cheat sword."),
            ["BuildMenu|TabDisplayNames"] = new Rule(Show.Hidden),   // written by right-click > Rename
            ["BuildMenu|WindowWidth"] = new Rule(Show.Hidden), ["BuildMenu|WindowHeight"] = new Rule(Show.Hidden),
            ["BuildMenu|WindowPosX"] = new Rule(Show.Hidden), ["BuildMenu|WindowPosY"] = new Rule(Show.Hidden),
            ["BuildMenu|WindowScale"] = new Rule(Show.Hidden),
            ["BuildMenu|ContextMenuKey"] = new Rule(Show.Shown, BuildMenu, "Piece / tab menu key", "Opens the menu for the piece or tab under the cursor."),
            ["BuildMenu|SnapDefaultKey"] = new Rule(Show.Shown, BuildMenu, "Set default snap point", "Makes the current snap point this piece's default."),
            ["General|ConfigWindowScale"] = new Rule(Show.Hidden),

            // ---- Hammer toggles (shown)
            ["HammerToggles|ForceClipGround"] = new Rule(Show.Shown, Hammer, "Clip into the ground", "Added pieces can sink into the ground."),
            ["HammerToggles|ForceClipEverything"] = new Rule(Show.Shown, Hammer, "Clip through pieces", "Added pieces can go through other pieces."),
            ["HammerToggles|DisableStaticPhysics"] = new Rule(Show.Shown, Hammer, "Stay where placed", "Placed pieces don't snap back to the ground later."),
            ["HammerToggles|ForcePersistence"] = new Rule(Show.Shown, Hammer, "Keep effects saved", "Placed effects stay in the world after you leave the area."),
            ["HammerToggles|SwapFracOnPlace"] = new Rule(Show.Shown, Hammer, "Whole rocks, not fragments", "A fractured rock places as the whole rock."),
            ["HammerToggles|SuppressRandomRotation"] = new Rule(Show.Shown, Hammer, "Keep my rotation", "No random spin on pieces like webs and decals."),
            ["HammerToggles|ItemStandHideStandVisual"] = new Rule(Show.Shown, Hammer, "Floating item stands", "Item stands with an item hide the stand itself."),
            ["HammerToggles|EffectsOverrideTTL"] = new Rule(Show.Shown, Hammer, "Effects never end", "Placed effects loop forever instead of fading out."),

            // ---- Persistence (shown, one section)
            ["General|UseEwpForPersistence"] = new Rule(Show.Shown, Persist, "Use EWP persistence", "Expand World Prefabs handles everything that respawns. Pick the kinds below.", 100),
            ["Persistence|EWP effects"] = new Rule(Show.Shown, Persist, "EWP: effects and timed pieces", "EWP brings back placed effects and self-destroying pieces.", 90, EwpOff),
            ["Persistence|EWP chests"] = new Rule(Show.Shown, Persist, "EWP: chests", "EWP refills respawning chests.", 89, EwpOff),
            ["Persistence|EWP monsters"] = new Rule(Show.Shown, Persist, "EWP: monsters", "EWP respawns placed monsters.", 88, EwpOff),
            ["ItemStands|UseEwpForRefill"] = new Rule(Show.Shown, Persist, "EWP: item stands", "EWP puts items back on refilling item stands.", 87, EwpOff),
            ["Persistence|FAP respawner"] = new Rule(Show.Shown, Persist, "Use FAP persistence", "FAP persistence is only visible to other players using FAP", 50, FapGrey),
            ["General|VanillaFriendly"] = new Rule(Show.Shown, Persist, "Use vanilla friendly", "Only place things players without mods can see.", 40),
            ["General|CustomPersistentPrefabs"] = new Rule(Show.Advanced, Persist, "Extra respawning prefabs", "More prefab names that respawn, comma-separated."),

            // ---- General (advanced)
            ["General|VerboseLogging"] = new Rule(Show.Advanced, null, "Verbose logging", "Writes extra detail to the log."),
            ["General|ServerEnforced"] = new Rule(Show.Advanced, null, "Server decides", "The server's settings win over each player's."),
            ["General|AdminOnlyMode"] = new Rule(Show.Advanced, null, "Admins only", "Only admins get the expanded hammer."),
            ["General|RegisterPiecesForNonAdmins"] = new Rule(Show.Advanced, null, "Register pieces for everyone", "Non-admins get the pieces registered but not shown (for mods like WackysDatabase)."),
            ["General|WackysDatabaseCompat"] = new Rule(Show.Advanced, null, "WackysDatabase support", "Lets WackysDatabase clone FAP pieces safely."),
            ["General|VanillaFriendlyEffectAllowlist"] = new Rule(Show.Advanced, null, "Vanilla-safe effects", "Extra effects vanilla friendly still offers, comma-separated."),
            ["General|SortHammerByMod"] = new Rule(Show.Advanced, null, "A tab per bundle", "Bundle prefabs get their own hammer tab each."),
            ["General|BundleNatureShader"] = new Rule(Show.Advanced, null, "Nature shader", "Shader used to fix plants and trees from bundles."),
            ["General|BundleNatureShaderAllOf"] = new Rule(Show.Advanced, null, "Nature shader: whole bundles", "Bundles whose every prefab gets the nature shader, comma-separated."),
            ["General|AutoPushOnPlace"] = new Rule(Show.Advanced, null, "Auto-send presets to the server", "Placing or saving a preset sends it to the server."),
            ["General|Show unlock messages for admin pieces"] = new Rule(Show.Advanced, null, "Unlock messages for added pieces", "Show a popup for every piece FAP adds."),

            // ---- retired / bookkeeping (hidden; logic kept with empty defaults)
            ["Compatibility|RemovedPrefabPlaceholders"] = new Rule(Show.Hidden),

            // ---- Advanced
            ["Compatibility|KeepDeadScriptSlotBundles"] = new Rule(Show.Advanced, null, "Bundles with missing scripts", "Load these bundles even with missing scripts, comma-separated."),
            ["Compatibility|InfinityHammerKeepAreaGrabScale"] = new Rule(Show.Advanced, null, "Keep size on area grab", "Infinity Hammer area grabs keep each object's size."),
            ["Performance|DediSkipRenderMeshes"] = new Rule(Show.Advanced, null, "Server: drop meshes", "Dedicated server drops these prefabs' meshes, comma-separated."),
            ["Performance|ClientLazyRenderMeshes"] = new Rule(Show.Advanced, null, "Load meshes when needed", "These prefabs' meshes load the first time one is near, comma-separated."),
            ["Performance|BundleCullDistance"] = new Rule(Show.Advanced, null, "Bundle draw distance", "Bundle pieces stop drawing past this distance (m)."),
            ["Performance|BundleImpostors"] = new Rule(Show.Advanced, null, "Far bundle pieces as cards", "Far bundle pieces draw as cheap flat cards."),
            ["Performance|BundleImpostorDistance"] = new Rule(Show.Advanced, null, "Card distance", "Distance (m) where the cards take over."),
            ["Performance|BundleImpostorCullDistance"] = new Rule(Show.Advanced, null, "Card draw distance", "Cards stop drawing past this distance (m)."),
            ["Performance|EnableBlueprintMeshBake"] = new Rule(Show.Advanced, null, "Bake blueprints", "Merges big blueprints into one mesh for speed."),
            ["Performance|BakeExcludeTransparentPieces"] = new Rule(Show.Advanced, null, "Don't bake glass", "Transparent pieces stay out of baked meshes."),
            ["Performance|BakedLodLowDetailDistance"] = new Rule(Show.Advanced, null, "Baked low-detail distance", "Baked blueprints switch to low detail past this (m)."),
            ["Performance|BakedLodCullDistance"] = new Rule(Show.Advanced, null, "Baked draw distance", "Baked blueprints stop drawing past this (m); 0 = never."),
            ["Performance|BakedLodShadowProxy"] = new Rule(Show.Advanced, null, "Baked shadows", "Baked blueprints keep a cheap shadow."),
            ["Performance|GhostCacheBudgetMB"] = new Rule(Show.Advanced, null, "Ghost cache size (MB)", "Disk space for blueprint ghost caches."),
            ["Performance|GhostCacheWarnPerBlueprintMB"] = new Rule(Show.Advanced, null, "Ghost cache warning (MB)", "Warn when one blueprint's cache is bigger than this."),
            ["Performance|BundleLoadingBudget"] = new Rule(Show.Advanced, null, "Bundle loading speed", "Higher loads bundles faster but can stutter."),
            ["Performance|InjectionSingleWalk"] = new Rule(Show.Advanced, null, "Fast prefab checks", "Checks each bundle prefab in one pass."),
            ["Performance|DediUnloadTextures"] = new Rule(Show.Advanced, null, "Server: drop textures", "Dedicated server frees bundle textures it never draws."),
            ["Performance|ClientNonReadableRenderMeshes"] = new Rule(Show.Advanced, null, "Lean meshes", "Frees the CPU copy of bundle meshes."),
            ["LazyBundles|PreloadAll"] = new Rule(Show.Advanced, null, "Load all bundles at start", "Loads every bundle at game start (more memory, no waits)."),
            ["LazyBundles|LookAheadZones"] = new Rule(Show.Advanced, null, "Preload range (zones)", "Pieces this many zones away load ahead of time."),
            ["Diagnostics|BuildMenuNativeTrace"] = new Rule(Show.Advanced, null, "Build menu trace", "Logs build menu steps, for crash hunting."),
            ["Diagnostics|Texture library at spawn"] = new Rule(Show.Advanced, null, "Texture library at spawn", "Spawns the texture library for testing."),
            ["Clutter|PaintRadius"] = new Rule(Show.Advanced, null, "Clutter brush size (m)", "Size of the clutter paint brush."),
            ["Clutter|PaintDensity"] = new Rule(Show.Advanced, null, "Clutter brush density", "How thick the clutter brush paints."),
            ["FxClear|Radius"] = new Rule(Show.Advanced, null, "FX clear size (m)", "Size of the FX clear brush."),
            ["FxClear|WatchdogDurationSec"] = new Rule(Show.Advanced, null, "FX clear watch time (s)", "Keeps clearing respawned FX for this long."),
            ["FxClear|RemoveOrphanStatusFxOnLoad"] = new Rule(Show.Advanced, null, "Remove left-behind status FX on load", "Removes saved wet/frost/burning FX a creature left behind. Placed FX are kept."),
            ["FxClear|WatchdogTickIntervalSec"] = new Rule(Show.Advanced, null, "FX clear check rate (s)", "Time between FX clear re-checks."),
            ["ItemStands|StandOrientation"] = new Rule(Show.Advanced, null, "Item stand tab", "Which item stands the hammer shows: Horizontal, Vertical or Both."),
        });

        private static Rule RuleFor(ConfigDefinition d)
        {
            if (GeometrySections.Contains(d.Section)) return new Rule(Show.Hidden);
            if (Rules.TryGetValue(d.Section + "|" + d.Key, out var r)) return r;
            // a keybind's Ctrl / Shift / Alt rows follow their key (no rename, so Core still pairs them)
            if (d.Section == "BuildMenu" || d.Section == "Build Menu") return new Rule(Show.Shown, BuildMenu);
            if (d.Section == "HammerToggles") return new Rule(Show.Shown, Hammer);
            if (d.Section == "Persistence") return new Rule(Show.Shown, Persist);
            return new Rule(Show.Advanced);
        }

        private static readonly FieldInfo DescriptionField = typeof(ConfigEntryBase).GetField("<Description>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>Tags every entry of FAP's cfg not tagged yet. Safe to call again (UIs bind their entries late).</summary>
        internal static void Apply(ConfigFile config)
        {
            if (config == null || DescriptionField == null) return;
            int tagged = 0;
            foreach (var entry in config.Keys.Select(k => config[k]).ToList())
            {
                var old = entry.Description;
                var tags = old?.Tags ?? Array.Empty<object>();
                if (tags.Any(t => t is ConfigurationManagerAttributes)) continue;
                var rule = RuleFor(entry.Definition);
                var attr = new ConfigurationManagerAttributes
                {
                    Browsable = rule.Show != Show.Hidden,
                    IsAdvanced = rule.Show == Show.Advanced,
                    Order = rule.Order,
                    Category = rule.Category,
                    DispName = rule.Name,
                    Description = rule.Desc,
                    Grey = rule.Grey,
                    Why = rule.Grey == null ? null : rule.Grey == (Func<bool>)FapGrey ? (Func<string>)FapWhy : EwpOffWhy,
                    Prior = tags.LastOrDefault(t => t != null && t.GetType().Name == nameof(ConfigurationManagerAttributes)),
                };
                var list = tags.ToList(); list.Add(attr);
                DescriptionField.SetValue(entry, new ConfigDescription(rule.Desc ?? old?.Description ?? "", old?.AcceptableValues, list.ToArray()));
                tagged++;
            }
            if (tagged > 0) VerdantsAscent.VaPieces.Log?.LogDebug($"FiresAdminPrefabs: settings UI tags applied to {tagged} entr{(tagged == 1 ? "y" : "ies")}.");
        }
    }

    // 1.1.279: one-time settings migrations are recorded in <BepInEx>\cache\FiresAdminPrefabs\migrations.txt, not in a cfg
    // section ([Internal] AppliedMigrations moves there once and leaves the cfg).
    internal sealed class FapMigrations
    {
        private readonly string _path;
        private string _value;
        private FapMigrations(string path, string value) { _path = path; _value = value ?? ""; }

        internal string Value
        {
            get => _value;
            set
            {
                _value = value ?? "";
                try { Directory.CreateDirectory(Path.GetDirectoryName(_path)); File.WriteAllText(_path, _value); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning("FiresAdminPrefabs: could not save migrations: " + ex.Message); }
            }
        }

        internal bool Done(string id) => ("," + _value + ",").Contains("," + id + ",");
        internal void Mark(string id) { if (!Done(id)) Value = string.IsNullOrEmpty(_value) ? id : _value + "," + id; }

        internal static FapMigrations Load(ConfigFile config)
        {
            string path = Path.Combine(Paths.BepInExRootPath, "cache", "FiresAdminPrefabs", "migrations.txt");
            string value = "";
            try { if (File.Exists(path)) value = File.ReadAllText(path).Trim(); } catch { }
            var m = new FapMigrations(path, value);
            try
            {
                var orphansProp = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (orphansProp?.GetValue(config) is Dictionary<ConfigDefinition, string> orphans)
                {
                    var def = new ConfigDefinition("Internal", "AppliedMigrations");
                    if (orphans.TryGetValue(def, out var old))
                    {
                        foreach (var id in (old ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)) m.Mark(id);
                        orphans.Remove(def);
                        config.Save();
                        UnityEngine.Debug.Log("FiresAdminPrefabs: moved [Internal] AppliedMigrations out of the cfg into cache\\FiresAdminPrefabs\\migrations.txt.");
                    }
                }
            }
            catch { }
            return m;
        }
    }
}
