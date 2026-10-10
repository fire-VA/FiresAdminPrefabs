using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // Right-click context menus for build menu pieces and tabs. Disk operations
    // live on VAHammerTabOrganizer; live changes refresh the build menu, while
    // un-hiding requires a world reload.
    public static class VAHammerRightClickPatches
    {
        // ──────────────────────────────────────────────────────────────
        //  Piece menu — opened by VABuildUiContextMenu (VABuildUiPatches)
        // ──────────────────────────────────────────────────────────────
        internal static void ShowPieceMenu(Piece piece)
        {
            var prefabName = piece.gameObject != null ? piece.gameObject.name : null;
            if (string.IsNullOrEmpty(prefabName)) return;
            // 1.1.203: never on another tool's menu entries (Infinity Hammer's "_IH_Menu_*" blueprint / command pieces).
            if (prefabName.StartsWith("_IH_Menu_") || !HammerCategoryUI.IsOrganizable(Player.m_localPlayer?.m_buildPieces)) return;

            bool isFavorite = VAHammerTabOrganizer.IsFavorite(prefabName);
            var items = new List<VARightClickMenu.Item>
            {
                new VARightClickMenu.Item(
                    isFavorite ? $"★ Remove from Favorites  ({prefabName})"
                               : $"★ Add to Favorites  ({prefabName})",
                    () => ToggleFavoriteAction(prefabName)),
                new VARightClickMenu.Item(
                    $"Hide  ({prefabName})",
                    () => HidePrefabAction(prefabName)),
                new VARightClickMenu.Item(
                    "Move to tab...",
                    () => OpenMoveSubmenu(prefabName)),
            };

            // "Pre-bake mesh" entry on registered blueprint stubs (only when bake
            // config is on). Runs the combine work up front so later placement hits
            // the cache and is near-instant.
            if (VABlueprintHammerIntegration.IsBlueprintStub(prefabName)
                && (ConfigManager.Instance?.configEnableBlueprintBake?.Value ?? false))
            {
                // Cache check uses the SAME key SpawnBlueprintAt uses at placement
                // (source filename, not stub prefab name) so the state reflects
                // whether placement will hit the cache.
                string cacheKey = prefabName;
                if (VABlueprintHammerIntegration.TryGetBlueprint(prefabName, out var bp) && bp != null)
                {
                    cacheKey = VABlueprintHammerIntegration.GetCacheKey(bp);
                }
                bool cached = VAFapBlueprintBakeCache.Has(cacheKey);
                if (cached)
                {
                    items.Add(new VARightClickMenu.Item(
                        "Re-bake to prefab... <color=#fa6>(rebake)</color>",
                        () => OpenReBakeConfirm(prefabName)));
                }
                else
                {
                    items.Add(new VARightClickMenu.Item(
                        "Bake to prefab...",
                        () => OpenPreBakeConfirm(prefabName)));
                }
            }

            // FX/VFX-only "Place with Persistence" toggle. Default OFF; opting in
            // makes a specific FX piece persistent before placement.
            if (IsFxOrVfxCategory(piece))
            {
                bool optedIn = VerdantsAscent.Utilities.VAFapPersistentOptIn.IsOptedIn(prefabName);
                string label = (optedIn ? "✓ " : "    ") + "Place with Persistence";
                items.Add(new VARightClickMenu.Item(
                    label,
                    () => VerdantsAscent.Utilities.VAFapPersistentOptIn.Toggle(prefabName)));
            }

            // Per-piece distance render override, picked from a preset list and
            // re-applied without a world reload.
            float currentDistance;
            string distLabel = VAFapPieceOverrides.TryGetDistance(prefabName, out currentDistance)
                ? $"Distance render override... <color=#888>({(int)currentDistance}m)</color>"
                : "Distance render override...";
            // 1.1.169 (Fire 10-06): "Create data preset" on any prefab, saved presets queued until changed.
            bool extraArmed = VAExtraDataQueue.IsArmedFor(prefabName);
            items.Add(new VARightClickMenu.Item(
                extraArmed
                    ? $"Create data preset... <color=#8f8>({VAExtraDataQueue.Describe(prefabName)})</color>"
                    : "Create data preset...",
                () => VAExtraDataUI.Open(prefabName, null)));
            if (extraArmed || VADataPresetStore.For(prefabName).Any())
                items.Add(new VARightClickMenu.Item(
                    $"Data presets for {prefabName}...",
                    () => OpenDataPresetSubmenu(prefabName)));

            items.Add(new VARightClickMenu.Item(
                distLabel,
                () => OpenDistanceOverrideSubmenu(prefabName)));

            // Per-piece shader override, applied immediately to the prefab template
            // and every live instance.
            string currentShader;
            string shaderLabel = VAFapPieceOverrides.TryGetShader(prefabName, out currentShader)
                ? $"Shader override... <color=#888>({currentShader})</color>"
                : "Shader override...";
            items.Add(new VARightClickMenu.Item(
                shaderLabel,
                () => OpenShaderOverrideSubmenu(prefabName)));

            // Monster-specific affordances on FAP monster stand-ins; the creature
            // is encoded in the stand-in's name suffix.
            if (VAMonsterStandinFactory.IsStandinName(prefabName))
            {
                string creature = VAMonsterStandinFactory.ResolveCreatureNameFromStandin(piece.gameObject);
                if (!string.IsNullOrEmpty(creature))
                {
                    items.Add(new VARightClickMenu.Item(
                        $"Configure monster ({creature})...",
                        () => VAMonsterConfigUI.Open(creature)));

                    items.Add(new VARightClickMenu.Item(
                        $"Presets for {creature}...",
                        () => OpenPresetSubmenu(creature)));
                }
            }

            // Any piece with a Container component gets the chest affordances.
            if (piece.GetComponent<Container>() != null)
            {
                items.Add(new VARightClickMenu.Item(
                    $"Configure container ({prefabName})...",
                    () => VAChestConfigUI.Open(prefabName)));

                items.Add(new VARightClickMenu.Item(
                    $"Presets for {prefabName}...",
                    () => OpenChestPresetSubmenu(prefabName)));
            }

            // 1.1.168: item stands get the same affordances (an item hung at placement, optionally decorative).
            if (piece.GetComponent<ItemStand>() != null)
            {
                items.Add(new VARightClickMenu.Item(
                    $"Configure item stand ({prefabName})...",
                    () => VAItemStandConfigUI.Open(prefabName, null)));
                items.Add(new VARightClickMenu.Item(
                    $"Item stand presets for {prefabName}...",
                    () => OpenStandPresetSubmenu(prefabName)));
            }
            // 1.1.176 (Fire 10-06): the Item Stands (H)/(V) tab entries are standins (a Piece and copied meshes, no ItemStand),
            // so the check above never matched them. They resolve to their item and the real stand; a preset made here is for
            // that stand (it applies to standin placements too: they place the real stand through SetCreator).
            else if (VAItemStandStandinFactory.TryResolve(piece.gameObject, out string standItem, out var standOrientation))
            {
                string standPrefab = standOrientation == VAItemStandStandinFactory.Orientation.Horizontal
                    ? VAItemStandStandinFactory.VanillaHorizontalPrefabName : VAItemStandStandinFactory.VanillaVerticalPrefabName;
                string armPiece = prefabName;
                items.Add(new VARightClickMenu.Item(
                    $"Create item stand preset ({standItem})...",
                    () => VAItemStandConfigUI.Open(standPrefab, null, standItem, armPiece)));
                items.Add(new VARightClickMenu.Item(
                    $"Item stand presets for {standPrefab}...",
                    () => OpenStandPresetSubmenu(standPrefab, standItem, armPiece)));
            }

            // Clutter affordances when the piece is a clutter standin or a raw
            // clutter prefab. Standin name prefix takes priority; raw clutter
            // falls through to the ClutterSystem-membership check.
            {
                string targetClutterName = null;
                if (VAClutterStandinFactory.IsStandinName(prefabName))
                {
                    string realName = VAClutterStandinFactory.ResolveRealNameFromStandin(piece.gameObject);
                    if (!string.IsNullOrEmpty(realName)) targetClutterName = realName;
                }
                else if (VAVanillaPieceManager.IsClutterPrefab_Public(piece.gameObject))
                {
                    targetClutterName = prefabName;
                }

                if (!string.IsNullOrEmpty(targetClutterName))
                {
                    string capturedClutter = targetClutterName;
                    string activeName = VAClutterPresetStore.GetActiveNameFor(capturedClutter);
                    string configLabel = string.IsNullOrEmpty(activeName)
                        ? $"Configure clutter ({capturedClutter})..."
                        : $"Configure clutter ({capturedClutter})... <color=#888>(active: {activeName})</color>";

                    items.Add(new VARightClickMenu.Item(
                        configLabel,
                        () => VAClutterConfigUI.Open(capturedClutter)));

                    items.Add(new VARightClickMenu.Item(
                        $"Presets for {capturedClutter}...",
                        () => OpenClutterPresetSubmenu(capturedClutter)));
                }
            }

            // Saved-NPC preset tiles (FiresRPGmaker hammer templates, tagged with its
            // NpcTemplateMarker component) get preset management rows. Name-based component +
            // reflection dispatch keep FAP free of any FiresRPGmaker reference.
            var npcMarker = piece.GetComponent("NpcTemplateMarker");
            if (npcMarker != null)
            {
                string templateName = null;
                try
                {
                    templateName = npcMarker.GetType()
                        .GetField("TemplateName", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(npcMarker) as string;
                }
                catch { }

                if (!string.IsNullOrEmpty(templateName))
                {
                    string capturedTemplate = templateName;
                    items.Add(new VARightClickMenu.Item(
                        "Re-render NPC icon",
                        () => InvokeNpcTemplateAction("TileRerenderIcon", capturedTemplate)));
                    items.Add(new VARightClickMenu.Item(
                        $"<color=#f66>Remove saved NPC</color>  ({capturedTemplate})",
                        () => InvokeNpcTemplateAction("TileDelete", capturedTemplate)));
                }
            }

            // Universal "Restore hidden..." entry — lists every currently-ignored
            // prefab so the admin can un-hide one. Shown on every right-click
            // because a hidden piece is gone from the hammer to click on.
            int hiddenCount = VAHammerTabOrganizer.IgnoredPrefabCount;
            string hiddenLabel = hiddenCount > 0
                ? $"Restore hidden piece... <color=#888>({hiddenCount} hidden)</color>"
                : "Restore hidden piece... <color=#888>(none hidden)</color>";
            items.Add(new VARightClickMenu.Item(
                hiddenLabel,
                () => OpenRestoreHiddenSubmenu(),
                disabled: hiddenCount == 0));

            // 1.1.200: other mods' entries (VerdantsAscent.Api.FapPieceMenu), e.g. FDM's presets.
            var external = VerdantsAscent.Api.FapPieceMenu.Collect(piece);
            if (external.Count > 0)
            {
                items.Add(new VARightClickMenu.Item("──────────", null, disabled: true));
                foreach (var e in external)
                {
                    var click = e.Value;
                    items.Add(new VARightClickMenu.Item(e.Key, click == null ? (Action)null : () =>
                    {
                        try { click(); }
                        catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: right-click entry '{e.Key}' threw: {ex.Message}"); }
                    }, disabled: click == null));
                }
            }

            VARightClickMenu.Show(items);
        }

        // Reflection dispatch into FiresRPGmaker's NpcTemplatePieces statics (TileDelete /
        // TileRerenderIcon) — FAP has no FiresRPGmaker reference; the rows only appear on
        // pieces carrying its NpcTemplateMarker, so a missing assembly can't be hit in play.
        private static void InvokeNpcTemplateAction(string method, string templateName)
        {
            try
            {
                var type = Type.GetType("FiresRPGmaker.Managers.NpcTemplatePieces, FiresRPGmaker");
                var action = type?.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
                if (action == null)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: FiresRPGmaker.{method} not found — update FiresRPGmaker");
                    return;
                }
                action.Invoke(null, new object[] { templateName });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: NPC preset action '{method}' failed: {ex.Message}");
            }
        }

        // Submenu listing every currently-hidden prefab name; click an entry to
        // unhide it. Restored pieces reappear on the next world reload (live-
        // mutating the PieceTable mid-session is race-prone).
        private static void OpenRestoreHiddenSubmenu()
        {
            var hidden = VAHammerTabOrganizer.GetHiddenPrefabs();
            var subItems = new List<VARightClickMenu.Item>();
            if (hidden.Count == 0)
            {
                subItems.Add(new VARightClickMenu.Item(
                    "(nothing hidden)", null, disabled: true));
                VARightClickMenu.Show(subItems);
                return;
            }
            foreach (var name in hidden)
            {
                string capturedName = name;
                subItems.Add(new VARightClickMenu.Item(
                    "→ " + capturedName,
                    () =>
                    {
                        if (VAHammerTabOrganizer.UnhidePrefab(capturedName))
                        {
                            var player = Player.m_localPlayer;
                            if (player != null)
                            {
                                player.Message(MessageHud.MessageType.TopLeft,
                                    $"Unhid '{capturedName}'. Reappears on next world reload.");
                            }
                        }
                    }));
            }
            VARightClickMenu.Show(subItems);
        }

        // Clutter preset submenu. Selecting a preset sets it active for the target
        // clutter prefab (persisted via clutter_active.yaml) and pushes to
        // ClutterSystem live. Clutter is re-configured globally, not re-placed.
        private static void OpenClutterPresetSubmenu(string clutterPrefabName)
        {
            var matching = new List<VAClutterPreset>();
            foreach (var entry in VAClutterPresetStore.All)
            {
                var preset = entry.Value;
                if (preset == null) continue;
                if (!string.Equals(preset.clutterPrefab, clutterPrefabName, StringComparison.Ordinal)) continue;
                matching.Add(preset);
            }

            var items = new List<VARightClickMenu.Item>();
            string activeName = VAClutterPresetStore.GetActiveNameFor(clutterPrefabName);

            if (matching.Count == 0)
            {
                items.Add(new VARightClickMenu.Item(
                    "Create new...",
                    () => VAClutterConfigUI.Open(clutterPrefabName)));
            }
            else
            {
                matching.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.Ordinal));
                foreach (var clutterPreset in matching)
                {
                    string captured = clutterPreset.name;
                    bool isActive = string.Equals(captured, activeName, StringComparison.Ordinal);
                    string display = isActive
                        ? $"<color=#88ff88>● {captured}</color> <color=#888>(active)</color>"
                        : "→ " + captured;
                    items.Add(new VARightClickMenu.Item(display, () =>
                    {
                        if (!VAClutterPresetStore.TryGet(captured, out var preset)) return;
                        VAClutterPresetStore.SetActive(clutterPrefabName, captured);
                        VAClutterConfigApplier.Apply(preset);
                        var player = Player.m_localPlayer;
                        if (player != null)
                        {
                            player.Message(MessageHud.MessageType.TopLeft,
                                $"Applied clutter preset '{captured}' to {clutterPrefabName}.");
                        }
                    }));
                }

                if (!string.IsNullOrEmpty(activeName))
                {
                    items.Add(new VARightClickMenu.Item("──────────", null, disabled: true));
                    items.Add(new VARightClickMenu.Item(
                        "Clear active (revert to vanilla)",
                        () =>
                        {
                            VAClutterPresetStore.SetActive(clutterPrefabName, null);
                            var player = Player.m_localPlayer;
                            if (player != null)
                            {
                                player.Message(MessageHud.MessageType.TopLeft,
                                    $"Cleared active preset for {clutterPrefabName}. World reload to fully revert.");
                            }
                        }));
                }

                items.Add(new VARightClickMenu.Item("──────────", null, disabled: true));
                items.Add(new VARightClickMenu.Item(
                    "Edit / Create new...",
                    () => VAClutterConfigUI.Open(clutterPrefabName)));
            }
            VARightClickMenu.Show(items);
        }

        private static void OpenDataPresetSubmenu(string prefabName)
        {
            var items = new List<VARightClickMenu.Item>();
            string queued = VAExtraDataQueue.PresetFor(prefabName);
            if (VAExtraDataQueue.IsArmedFor(prefabName))
                items.Add(new VARightClickMenu.Item("✕ No data preset (plain placements)", () =>
                {
                    VAExtraDataQueue.Clear(prefabName);
                    ShowToast($"No data preset: the next {prefabName} placements are plain");
                }));
            foreach (var p in VADataPresetStore.For(prefabName))
            {
                string captured = p.name;
                string label = (p.name == queued ? "✔ " : "→ ") + p.name + $"   <color=#999>({p.Values.Count} field(s))</color>"
                               + (p.name == queued ? "   <color=#8f8>(queued)</color>" : "");
                items.Add(new VARightClickMenu.Item(label, () =>
                {
                    VADataPresetStore.TryGet(captured, out var preset);
                    string problem = preset != null ? VADataPresetStore.Arm(preset) : "the preset is gone";
                    if (problem != null) { ShowToast($"Data preset '{captured}' not queued: {problem}"); return; }
                    ShowToast($"Queued data preset '{captured}' for every {prefabName} placement (until changed)");
                    VAHammerArmHelper.ArmAndClose(prefabName);
                }));
            }
            items.Add(new VARightClickMenu.Item("──────────", null, disabled: true));
            items.Add(new VARightClickMenu.Item("Edit / Create new...", () => VAExtraDataUI.Open(prefabName, queued)));
            VARightClickMenu.Show(items);
        }

        private static void OpenStandPresetSubmenu(string standPrefabName, string prefillItem = null, string armPiece = null)
        {
            var matching = VAItemStandPresetStore.All.Values.Where(p => p != null && p.StandPrefab == standPrefabName).OrderBy(p => p.name, StringComparer.Ordinal).ToList();
            var items = new List<VARightClickMenu.Item>();
            VAItemStandQueue.TryGetQueuedPresetName(standPrefabName, out string queued);
            if (!string.IsNullOrEmpty(queued))
                items.Add(new VARightClickMenu.Item("✕ No preset (plain item stand)", () =>
                {
                    VAItemStandQueue.Clear(standPrefabName);
                    ShowToast($"No preset: the next {standPrefabName} placements are plain");
                }));
            foreach (var p in matching)
            {
                string captured = p.name;
                string label = (p.name == queued ? "✔ " : "→ ") + p.name + $"   <color=#999>({p.Item}{(p.NoInteract ? ", decorative" : "")})</color>"
                               + (p.name == queued ? "   <color=#8f8>(queued)</color>" : "");
                items.Add(new VARightClickMenu.Item(label, () =>
                {
                    VAItemStandQueue.Enqueue(standPrefabName, captured);
                    ShowToast($"Queued item stand preset '{captured}' for every {standPrefabName} placement (until changed)");
                    VAHammerArmHelper.ArmAndClose(standPrefabName);
                }));
            }
            if (matching.Count > 0) items.Add(new VARightClickMenu.Item("──────────", null, disabled: true));
            // 1.1.177 (Fire 10-07): from an Item Stands tab entry, "Create new" starts with that entry's item (a blank form, not the queued preset).
            if (!string.IsNullOrEmpty(prefillItem))
                items.Add(new VARightClickMenu.Item($"Create new ({prefillItem})...", () => VAItemStandConfigUI.Open(standPrefabName, null, prefillItem, armPiece)));
            items.Add(new VARightClickMenu.Item(matching.Count == 0 && string.IsNullOrEmpty(prefillItem) ? "Create new..." : "Edit / Create new...", () => VAItemStandConfigUI.Open(standPrefabName, queued, prefillItem, armPiece)));
            VARightClickMenu.Show(items);
        }

        private static void OpenChestPresetSubmenu(string containerPrefabName)
        {
            var matching = new List<VAChestPreset>();
            foreach (var entry in VAChestPresetStore.All)
            {
                var chestPreset = entry.Value;
                if (chestPreset == null) continue;
                if (!string.Equals(chestPreset.ContainerPrefab, containerPrefabName, StringComparison.Ordinal)) continue;
                matching.Add(chestPreset);
            }

            var items = new List<VARightClickMenu.Item>();
            if (matching.Count == 0)
            {
                items.Add(new VARightClickMenu.Item(
                    "Create new...",
                    () => VAChestConfigUI.Open(containerPrefabName)));
            }
            else
            {
                matching.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.Ordinal));
                // 1.1.167: the queued preset stays on the hammer for every placement until changed here.
                VAChestConfigQueue.TryGetQueuedPresetName(containerPrefabName, out string queuedChest);
                if (!string.IsNullOrEmpty(queuedChest))
                    items.Add(new VARightClickMenu.Item("✕ No preset (plain container)", () =>
                    {
                        VAChestConfigQueue.Clear(containerPrefabName);
                        ShowToast($"No preset: the next {containerPrefabName} placements are plain");
                    }));
                foreach (var chestPreset in matching)
                {
                    string captured = chestPreset.name;
                    string display = (chestPreset.name == queuedChest ? "✔ " : "→ ") + chestPreset.name + (chestPreset.name == queuedChest ? "   <color=#8f8>(queued)</color>" : "");
                    items.Add(new VARightClickMenu.Item(display, () =>
                    {
                        VAChestConfigQueue.EnqueueForNextPlacement(containerPrefabName, captured);
                        VAHammerArmHelper.ArmAndClose(containerPrefabName);
                    }));
                }
                items.Add(new VARightClickMenu.Item("──────────", null, disabled: true));
                items.Add(new VARightClickMenu.Item(
                    "Edit / Create new...",
                    () => VAChestConfigUI.Open(containerPrefabName)));
            }
            VARightClickMenu.Show(items);
        }

        // Submenu listing every preset whose creature field matches creatureName,
        // each queuing the preset for the next placement of that creature. Offers
        // "Create new..." when none exist.
        private static void OpenPresetSubmenu(string creatureName)
        {
            var matching = new List<VAMonsterPreset>();
            foreach (var entry in VAMonsterPresetStore.All)
            {
                var monsterPreset = entry.Value;
                if (monsterPreset == null) continue;
                if (!string.Equals(monsterPreset.Creature, creatureName, StringComparison.Ordinal)) continue;
                matching.Add(monsterPreset);
            }
            matching.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.Ordinal));

            var items = new List<VARightClickMenu.Item>();
            if (matching.Count == 0)
            {
                items.Add(new VARightClickMenu.Item(
                    $"(no presets saved for {creatureName})",
                    null, disabled: true));
                items.Add(new VARightClickMenu.Item(
                    "Create new preset...",
                    () => VAMonsterConfigUI.Open(creatureName)));
                VARightClickMenu.Show(items);
                return;
            }

            // 1.1.167: the queued preset stays on the hammer for every placement until changed here.
            VAMonsterConfigQueue.TryGetQueuedPresetName(creatureName, out string queuedMonster);
            if (!string.IsNullOrEmpty(queuedMonster))
                items.Add(new VARightClickMenu.Item("✕ No preset (plain creature)", () =>
                {
                    VAMonsterConfigQueue.Clear(creatureName);
                    ShowToast($"No preset: the next {creatureName} placements are plain");
                }));
            foreach (var preset in matching)
            {
                string capturedName = preset.name;
                string label = BuildPresetMenuLabel(preset);
                if (preset.name == queuedMonster) label = "✔" + label.Substring(1) + "   <color=#8f8>(queued)</color>";
                items.Add(new VARightClickMenu.Item(
                    label,
                    () => QueuePresetForPlacement(creatureName, capturedName)));
            }

            items.Add(new VARightClickMenu.Item(
                "Edit / Create new...",
                () => VAMonsterConfigUI.Open(creatureName)));

            VARightClickMenu.Show(items);
        }

        private static string BuildPresetMenuLabel(VAMonsterPreset preset)
        {
            var label = new System.Text.StringBuilder();
            label.Append("→ ").Append(preset.name);
            var hints = new List<string>();
            string level = preset.TryGetIntStr("level");
            if (!string.IsNullOrEmpty(level) && level != "1") hints.Add($"★{level}");
            string boss = preset.TryGetIntStr("override_boss");
            if (boss == "1") hints.Add("boss");
            string respawn = preset.TryGetFloatStr("va_respawn_seconds");
            if (!string.IsNullOrEmpty(respawn) && respawn != "0") hints.Add($"↻{respawn}s");
            if (hints.Count > 0)
            {
                label.Append("   <color=#999>(").Append(string.Join(", ", hints.ToArray())).Append(")</color>");
            }
            return label.ToString();
        }

        private static void QueuePresetForPlacement(string creatureName, string presetName)
        {
            VAMonsterConfigQueue.EnqueueForNextPlacement(creatureName, presetName);
            ShowToast($"Queued preset '{presetName}' for every {creatureName} placement (until changed)");
        }

        // Distance presets the user picks from; the 0m entry clears the override
        // (reverts to vanilla streaming).
        private static readonly float[] _distancePresets =
        {
            0f, 250f, 500f, 750f, 1000f, 1500f,
        };

        // Tolerance for matching a stored distance back to a preset entry.
        private const float DistanceMatchToleranceM = 1f;

        private static void OpenDistanceOverrideSubmenu(string prefabName)
        {
            float current;
            VAFapPieceOverrides.TryGetDistance(prefabName, out current);

            var items = new List<VARightClickMenu.Item>();
            foreach (var presetDistance in _distancePresets)
            {
                float captured = presetDistance;
                bool active = Mathf.Abs(captured - current) < DistanceMatchToleranceM;
                string prefix = active ? "✓ " : "    ";
                string label = captured <= 0f
                    ? $"{prefix}Off (use vanilla streaming)"
                    : $"{prefix}{(int)captured}m";
                items.Add(new VARightClickMenu.Item(
                    label,
                    () => SetDistanceAction(prefabName, captured)));
            }

            VARightClickMenu.Show(items);
        }

        private static void SetDistanceAction(string prefabName, float distance)
        {
            VAFapPieceOverrides.SetDistance(prefabName, distance);
            if (distance <= 0f)
                ShowToast($"Cleared distance override for '{prefabName}'");
            else
                ShowToast($"'{prefabName}' will render up to {(int)distance}m");
        }

        private static void OpenShaderOverrideSubmenu(string prefabName)
        {
            string current;
            VAFapPieceOverrides.TryGetShader(prefabName, out current);

            var shaders = VAFapPieceShaderOverrider.ListAvailableShaders();
            var items = new List<VARightClickMenu.Item>(shaders.Count + 1);

            bool cleared = string.IsNullOrEmpty(current);
            items.Add(new VARightClickMenu.Item(
                (cleared ? "✓ " : "    ") + "(no override — use prefab default)",
                () => SetShaderAction(prefabName, null)));

            foreach (var shaderName in shaders)
            {
                string captured = shaderName;
                bool active = string.Equals(captured, current, StringComparison.Ordinal);
                string prefix = active ? "✓ " : "    ";
                items.Add(new VARightClickMenu.Item(
                    prefix + captured,
                    () => SetShaderAction(prefabName, captured)));
            }

            VARightClickMenu.Show(items);
        }

        private static void SetShaderAction(string prefabName, string shaderName)
        {
            VAFapPieceOverrides.SetShader(prefabName, shaderName);
            if (string.IsNullOrEmpty(shaderName))
                ShowToast($"Cleared shader override for '{prefabName}'");
            else
                ShowToast($"'{prefabName}' shader → {shaderName}");
        }

        // Favorite toggle — persists to HammerTabs/favorites.txt and refreshes
        // the live piece lists so the Favorites tab updates the same frame.
        private static void ToggleFavoriteAction(string prefabName)
        {
            bool nowFavorite = VAHammerTabOrganizer.ToggleFavorite(prefabName);
            // Valheim 1.0 has its own Favorites tab with its own backing list. Mirror our state onto it,
            // otherwise the star lands only in FAP's tab - which the 1.0 build menu never renders.
            VABuildUiFavorites.Mirror(prefabName, nowFavorite);
            ShowToast(nowFavorite
                ? $"★ Added '{prefabName}' to Favorites"
                : $"Removed '{prefabName}' from Favorites");
            try { VerdantsAscent.Patches.HammerCategoryUI.RebuildPieceListIfHammer(Player.m_localPlayer, "VAHammerRightClickPatches"); } catch { }
        }

        private static void OpenMoveSubmenu(string prefabName)
        {
            // Union of the LIVE registered tab set and the on-disk tab folders.
            // The old list was disk-only — on installs whose HammerTabs/ tree
            // hadn't been written yet it was EMPTY and this flow dead-ended at
            // "No tabs available" while the hammer visibly showed a dozen
            // tabs. MovePrefab creates the destination folder on demand, so
            // disk state was never actually a prerequisite. Favorites is a
            // toggle (bookmark), not a move target; Ignore/Hidden_ are
            // pseudo-tabs.
            var tabs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in VACategoryRegistry.AllNames()) tabs.Add(n);
            foreach (var n in VAHammerTabOrganizer.KnownTabNames) tabs.Add(n);
            // Vanilla categories are movable destinations too — their folders
            // (Misc, Crafting, Building*, Furniture...) map onto the real
            // vanilla tabs, so any piece can be moved INTO a vanilla tab.
            foreach (var n in VAHammerTabOrganizer.VanillaTabNames) tabs.Add(n);
            // Favorites is listed (players expect every visible tab here) but is
            // NOT a move target — it's a bookmark tab, so picking it FAVORITES
            // the piece instead of relocating it. A real move would strip the
            // piece from its home tab, and un-favoriting later would then
            // orphan it into no tab at all.
            tabs.Remove(VAHammerTabOrganizer.FavoritesTabName);
            tabs.Remove(VAHammerTabOrganizer.IgnoreFolderName);
            tabs.RemoveWhere(t =>
                t.StartsWith(VAHammerTabOrganizer.HiddenTabPrefix, StringComparison.Ordinal));

            var sorted = new List<string>(tabs);
            sorted.Sort(StringComparer.Ordinal);

            var items = new List<VARightClickMenu.Item>(sorted.Count + 2);

            bool isFavorite = VAHammerTabOrganizer.IsFavorite(prefabName);
            items.Add(new VARightClickMenu.Item(
                isFavorite
                    ? "★ Favorites  <color=#888>(already favorited — click to remove)</color>"
                    : "★ Favorites  <color=#888>(bookmark — keeps its home tab)</color>",
                () => ToggleFavoriteAction(prefabName)));
            items.Add(new VARightClickMenu.Item("──────────", null, disabled: true));

            foreach (var tab in sorted)
            {
                string targetTab = tab;
                items.Add(new VARightClickMenu.Item(
                    "→ " + targetTab,
                    () => MovePrefabAction(prefabName, targetTab)));
            }
            items.Add(new VARightClickMenu.Item(
                "New tab...",
                () => VARightClickMenu.ShowTextPrompt($"New tab for '{prefabName}'", "", newTab =>
                {
                    newTab = (newTab ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(newTab)) return;
                    MovePrefabAction(prefabName, newTab);
                })));
            VARightClickMenu.Show(items);
        }

        // True if the piece's hammer category is one of our FX-tab categories
        // (CatFX or CatVFX) — the gate for the "Place with Persistence" item.
        private static bool IsFxOrVfxCategory(Piece piece)
        {
            if (piece == null) return false;
            var category = piece.m_category;
            if (VerdantsAscent.Patches.VACategoryRegistry.TryGet(
                    VerdantsAscent.Patches.VACategoryRegistry.CatFX, out var fxCat)
                && category == fxCat) return true;
            if (VerdantsAscent.Patches.VACategoryRegistry.TryGet(
                    VerdantsAscent.Patches.VACategoryRegistry.CatVFX, out var vfxCat)
                && category == vfxCat) return true;
            return false;
        }

        private static void HidePrefabAction(string prefabName)
        {
            if (!VAHammerTabOrganizer.HidePrefab(prefabName))
            {
                ShowToast($"Failed to hide '{prefabName}' — see log");
                return;
            }
            int removed = VAVanillaPieceManager.RemovePieceFromHammerByName(prefabName);
            if (removed > 0)
                ShowToast($"Hidden '{prefabName}'");
            else
                ShowToast($"Hidden '{prefabName}' (world reload to apply)");
        }

        private static void MovePrefabAction(string prefabName, string toTab)
        {
            if (!VAHammerTabOrganizer.MovePrefab(prefabName, toTab))
            {
                ShowToast($"Failed to move '{prefabName}' to '{toTab}' — see log");
                return;
            }
            int retagged = VAVanillaPieceManager.RetagPieceCategoryByName(prefabName, toTab);
            if (retagged > 0)
                ShowToast($"Moved '{prefabName}' → {toTab}");
            else
                ShowToast($"Moved '{prefabName}' → {toTab} (world reload to apply)");
        }

        // ──────────────────────────────────────────────────────────────
        //  Tab menu — opened by VABuildUiContextMenu (VABuildUiPatches)
        // ──────────────────────────────────────────────────────────────

        // The Hud.HidePieceSelection gate that used to sit here moved to a BuildUi.Close prefix in
        // VABuildUiPatches - 1.0 closes the menu through BuildUi, so gating the old Hud path guarded
        // nothing.

        // Hammer tags only. FAP's own tabs can be renamed and hidden; every other tag
        // only lists the hidden tabs.
        internal static void ShowTagMenu(int tagId)
        {
            var player = Player.m_localPlayer;
            if (player == null || !HammerCategoryUI.IsOrganizable(player.GetBuildTool())) return;

            if (VABuildUiCategoryTags.TryGetTabName(tagId, out var tabName))
                ShowUserTabMenu(tabName);
            else
                ShowVanillaTabMenu();
        }

        private static void ShowVanillaTabMenu()
        {
            var items = new List<VARightClickMenu.Item>
            {
                new VARightClickMenu.Item("Show hidden tabs...", OpenShowHiddenSubmenu),
                new VARightClickMenu.Item("(vanilla tab — rename / hide disabled)", null, disabled: true),
            };
            AddVanillaOnlyToggle(items);
            VARightClickMenu.Show(items);
        }

        private static void ShowUserTabMenu(string tabName)
        {
            var items = new List<VARightClickMenu.Item>
            {
                new VARightClickMenu.Item(
                    $"Rename '{tabName}'...",
                    () => RenameTabAction(tabName)),
                new VARightClickMenu.Item(
                    $"Hide '{tabName}'",
                    () => HideTabAction(tabName)),
                new VARightClickMenu.Item(
                    "Show hidden tabs...",
                    OpenShowHiddenSubmenu),
            };
            AddVanillaOnlyToggle(items);
            AddEwpPersistenceToggle(items, tabName);
            AddStandOrientationChoice(items, tabName);
            VARightClickMenu.Show(items);
        }

        // 1.1.197 (Arbby via Fire): pick the item-stand orientation from the Item Stands tab, live.
        private static void AddStandOrientationChoice(List<VARightClickMenu.Item> items, string tabName)
        {
            if (!string.Equals(tabName, VACategoryRegistry.CatItemStandsV, StringComparison.Ordinal)
                && !string.Equals(tabName, VACategoryRegistry.CatItemStandsH, StringComparison.Ordinal)) return;
            var entry = ConfigManager.Instance?.configItemStandOrientation;
            if (entry == null) return;
            items.Add(new VARightClickMenu.Item("──────────", null, disabled: true));
            foreach (var mode in new[] { "Horizontal", "Vertical", "Both" })
            {
                string capture = mode;
                bool on = string.Equals(entry.Value, mode, StringComparison.OrdinalIgnoreCase);
                items.Add(new VARightClickMenu.Item((on ? "✓ " : "    ") + "Item stands: " + mode, () =>
                {
                    entry.Value = capture;   // SettingChanged swaps the stand-ins live
                    ShowToast("Item stands: " + capture);
                }));
            }
        }

        // Vanilla-only is a server-synced setting, so it is offered to admins only — a player
        // flipping it locally would just be overwritten by the next sync.
        private static void AddVanillaOnlyToggle(List<VARightClickMenu.Item> items)
        {
            var entry = ConfigManager.Instance?.configVanillaFriendly;
            if (entry == null) return;

            bool isAdmin;
            try { isAdmin = FiresCore.Sync.AdminSyncing.IsLocalAdmin(); }
            catch { isAdmin = false; }
            if (!isAdmin) return;

            items.Add(new VARightClickMenu.Item("──────────", null, disabled: true));
            items.Add(new VARightClickMenu.Item(
                (entry.Value ? "✓ " : "    ") + "Vanilla only",
                ToggleVanillaOnlyAction));
        }

        // Offered on the FX tabs, where it decides whether vanilla-only still splits modded
        // effects out: with EWP persisting them server-side they replay for every client.
        private static void AddEwpPersistenceToggle(List<VARightClickMenu.Item> items, string tabName)
        {
            if (!string.Equals(tabName, VACategoryRegistry.CatFX, StringComparison.Ordinal)
                && !string.Equals(tabName, VACategoryRegistry.CatVFX, StringComparison.Ordinal)) return;

            var entry = ConfigManager.Instance?.configUseEwpForPersistence;
            if (entry == null) return;

            bool isAdmin;
            try { isAdmin = FiresCore.Sync.AdminSyncing.IsLocalAdmin(); }
            catch { isAdmin = false; }
            if (!isAdmin) return;

            if (!VerdantsAscent.Utilities.VAEwpSoftDep.IsActive)
            {
                items.Add(new VARightClickMenu.Item(
                    "    EWP persistence  <color=#888>(Expand World Prefabs not installed)</color>", null, disabled: true));
                return;
            }

            items.Add(new VARightClickMenu.Item(
                (entry.Value ? "✓ " : "    ") + "EWP persistence",
                ToggleEwpPersistenceAction));
        }

        private static void ToggleEwpPersistenceAction()
        {
            var entry = ConfigManager.Instance?.configUseEwpForPersistence;
            if (entry == null) return;

            entry.Value = !entry.Value;
            ShowToast(entry.Value
                ? "EWP persistence ON — effects respawn server-side, so vanilla only stops splitting modded FX out."
                : "EWP persistence OFF — vanilla only again offers just the effects that persist on their own.");
        }

        // The config's SettingChanged handler re-buckets the FX tabs and refreshes the piece
        // lists, so the menu reflects the new state as soon as it reopens.
        private static void ToggleVanillaOnlyAction()
        {
            var entry = ConfigManager.Instance?.configVanillaFriendly;
            if (entry == null) return;

            entry.Value = !entry.Value;
            ShowToast(entry.Value
                ? "Vanilla only ON — modded FX, clutter and non-attachable item stands are hidden."
                : "Vanilla only OFF — the full modded hammer is back.");
        }

        private static void OpenShowHiddenSubmenu()
        {
            var hidden = VAHammerTabOrganizer.EnumerateHiddenTabs();
            if (hidden.Count == 0)
            {
                ShowToast("No hidden tabs.");
                return;
            }
            var items = new List<VARightClickMenu.Item>(hidden.Count);
            foreach (var hiddenTab in hidden)
            {
                string capture = hiddenTab;
                string displayName = hiddenTab.StartsWith(VAHammerTabOrganizer.HiddenTabPrefix, StringComparison.Ordinal)
                    ? hiddenTab.Substring(VAHammerTabOrganizer.HiddenTabPrefix.Length)
                    : hiddenTab;
                items.Add(new VARightClickMenu.Item(
                    $"Un-hide → {displayName}",
                    () => UnhideTabAction(capture)));
            }
            VARightClickMenu.Show(items);
        }

        // Prompts for a custom DISPLAY name and applies it live + persisted,
        // without moving the on-disk folder (the folder name stays the stable
        // internal key). The build menu reads the label whenever it rebuilds its tags.
        private static void RenameTabAction(string tabName)
        {
            string current = VAHammerTabOrganizer.TryGetDisplayName(tabName, out var dn) ? dn : tabName;
            VARightClickMenu.ShowTextPrompt($"Rename tab '{tabName}'", current, newName =>
            {
                newName = (newName ?? string.Empty).Trim();
                VAHammerTabOrganizer.SetDisplayName(tabName, newName);
                VABuildUiCategoryTags.RequestBuildMenuRefresh();

                ShowToast(string.IsNullOrEmpty(newName) || string.Equals(newName, tabName, StringComparison.Ordinal)
                    ? $"Tab '{tabName}' label reset to its folder name."
                    : $"Renamed '{tabName}' → '{newName}'.");
            });
        }

        private static void HideTabAction(string tabName)
        {
            if (VAHammerTabOrganizer.HideTab(tabName))
                ShowToast($"Hidden tab '{tabName}'");
            else
                ShowToast($"Failed to hide tab '{tabName}' — see log");
        }

        private static void UnhideTabAction(string hiddenTabName)
        {
            if (VAHammerTabOrganizer.UnhideTab(hiddenTabName))
                ShowToast($"Un-hid '{hiddenTabName}' (world reload to apply)");
            else
                ShowToast($"Failed to un-hide '{hiddenTabName}' — see log");
        }

        // ──────────────────────────────────────────────────────────────
        //  Pre-bake blueprint mesh — right-click action
        // ──────────────────────────────────────────────────────────────

        // Wipes the existing cache entry (in-memory + disk + badge-tracking) for
        // this blueprint, then runs the normal pre-bake flow. Used when an earlier
        // bake produced a bad result and the admin wants a fresh attempt.
        private static void OpenReBakeConfirm(string stubName)
        {
            if (string.IsNullOrEmpty(stubName)) return;

            if (!VABlueprintHammerIntegration.TryGetBlueprint(stubName, out var bp) || bp == null)
            {
                ShowToast($"Blueprint '{stubName}' not registered — can't re-bake.");
                return;
            }

            string cacheKey = VABlueprintHammerIntegration.GetCacheKey(bp);

            bool removed = VAFapBlueprintBakeCache.Remove(cacheKey);
            Utilities.VABakeBundle.Unpublish(cacheKey);
            ShowToast(removed
                ? $"Re-bake: cleared old cache for '{cacheKey}', baking fresh..."
                : $"Re-bake: no cache to clear for '{cacheKey}', baking fresh...");

            // Forget the badged-sprite tracking so the next bake repaints the badge.
            try
            {
                var scene = ZNetScene.instance;
                if (scene != null && scene.m_prefabs != null)
                {
                    for (int i = 0; i < scene.m_prefabs.Count; i++)
                    {
                        var prefab = scene.m_prefabs[i];
                        if (prefab != null && prefab.name == stubName)
                        {
                            var piece = prefab.GetComponent<Piece>();
                            if (piece != null) VABakeCacheIconBadge.ForgetStub(piece);
                            break;
                        }
                    }
                }
            }
            catch { /* badge cleanup is cosmetic */ }

            if (VaPieces.Instance == null)
            {
                ShowToast("Re-bake unavailable right now (plugin not initialized).");
                return;
            }
            int pieceCount = bp.Objects != null ? bp.Objects.Count : 0;
            string sizeWarning = pieceCount > LargeBlueprintPieceThreshold
                ? " — this is a LARGE blueprint, expect minutes."
                : (pieceCount > MediumBlueprintPieceThreshold ? " — may take a moment." : "");
            ShowToast($"Re-baking '{cacheKey}' ({pieceCount} pieces){sizeWarning} Game stays responsive.");
            VaPieces.Instance.StartCoroutine(PreBakeCoroutine(cacheKey, bp));
        }

        private static void OpenPreBakeConfirm(string stubName)
        {
            if (string.IsNullOrEmpty(stubName)) return;

            if (!VABlueprintHammerIntegration.TryGetBlueprint(stubName, out var bp) || bp == null)
            {
                ShowToast($"Blueprint '{stubName}' not registered — can't pre-bake.");
                return;
            }

            // Cache key MUST match the key SpawnBlueprintAt uses at placement
            // (source filename, else blueprint name) or placement cache-misses and
            // falls through to a full live-bake — the "froze on placement" bug.
            string cacheKey = VABlueprintHammerIntegration.GetCacheKey(bp);

            if (VAFapBlueprintBakeCache.Has(cacheKey))
            {
                ShowToast($"'{cacheKey}' is already cached — placements will be instant.");
                return;
            }

            if (VaPieces.Instance == null)
            {
                ShowToast("Pre-bake unavailable right now (plugin not initialized).");
                return;
            }

            int pieceCount = bp.Objects != null ? bp.Objects.Count : 0;
            string sizeWarning = pieceCount > LargeBlueprintPieceThreshold
                ? " — this is a LARGE blueprint, expect minutes."
                : (pieceCount > MediumBlueprintPieceThreshold ? " — may take a moment." : "");
            ShowToast($"Baking '{cacheKey}' ({pieceCount} pieces){sizeWarning} Game stays responsive.");

            VaPieces.Instance.StartCoroutine(PreBakeCoroutine(cacheKey, bp));
        }

        // Minimum interval between throttled progress messages.
        private const long PreBakeProgressIntervalMs = 5000L;

        // Piece-count thresholds that drive the bake size-warning text.
        private const int LargeBlueprintPieceThreshold = 10000;
        private const int MediumBlueprintPieceThreshold = 1000;

        // Bundle-write wait: toast cadence, and a cap of about 15 minutes at 60fps.
        private const int BundleFlushToastEveryFrames = 60;
        private const int BundleFlushSafetyCapFrames = 60 * 900;

        // Free space a sweep refuses to run below. A bundle is written to .tmp and then moved, so
        // the floor holds two copies of the largest bake seen here (314MB) with room for the zone
        // caches other mods write while it runs.
        private const long MinimumFreeDiskBytes = 2L * 1024 * 1024 * 1024;

        // Names listed before the sweep note falls back to a count.
        private const int MaxNamesInRebakeNote = 8;

        private static System.Collections.IEnumerator PreBakeCoroutine(string cacheKey, VABlueprintLoader.Blueprint bp)
        {
            var scene = ZNetScene.instance;
            if (scene == null) yield break;

            // Progress throttle; bypassed for the explicit start/end notifications.
            var lastMsgWatch = System.Diagnostics.Stopwatch.StartNew();
            void EmitThrottled(string msg)
            {
                if (string.IsNullOrEmpty(msg)) return;
                if (lastMsgWatch.ElapsedMilliseconds >= PreBakeProgressIntervalMs)
                {
                    ShowToast($"Bake '{cacheKey}': {msg}");
                    lastMsgWatch.Restart();
                }
            }

            int totalPieces = bp.Objects.Count;
            ShowToast($"Bake '{cacheKey}': starting ({totalPieces} pieces, prefab-only)");

            // Prefab-only bake — samples each prefab's MeshFilters directly with no
            // spawning/ZNetView, so RemoveObjects can't eat pieces mid-bake. LOD is
            // always preserved.
            yield return VaPieces.Instance.StartCoroutine(
                Utilities.VAFapBlueprintMeshBaker.BakeFromBlueprintPrefabsCoroutine(
                    bp, scene, cacheKey, EmitThrottled));

            // Final notification (bypasses throttle). The new prefab only becomes
            // available on the Baked Meshes tab after a relaunch (RegisterAll runs
            // at world load).
            ShowToast(VAFapBlueprintBakeCache.Has(cacheKey)
                ? $"Bake complete: '{cacheKey}'. Relaunch the game to use the new prefab in the Baked Meshes tab."
                : $"Bake FAILED for '{cacheKey}' — check log.");
        }

        // Bakes every registered blueprint into a fresh bundle, replacing any existing bake. Runs
        // one blueprint at a time so megablueprint allocations don't stack, using the same
        // prefab-only pipeline as the right-click re-bake.
        public static System.Collections.IEnumerator RebakeAllCoroutine()
        {
            var bakeable = CollectBakeableBlueprints(out int emptyBlueprints);
            int total = bakeable.Count;
            if (total == 0)
            {
                ShowToast("Re-bake all: no registered blueprints found.");
                yield break;
            }

            int staleTemps = Utilities.VABakeBundle.SweepStaleTempFiles();
            int orphanManifests = Utilities.VABakeBundle.RemoveManifestsWithoutBundles();
            if (staleTemps > 0 || orphanManifests > 0)
                EmitRebakeNote($"cleared {staleTemps} unfinished write(s) and {orphanManifests} manifest(s) with no bundle before starting.", isWarning: false);

            long freeBytes = Utilities.VABakeBundle.FreeBytesForBundles();
            if (IsBelowDiskFloor(freeBytes))
            {
                string abort = $"Re-bake all aborted: only {Sync.VAFapAssetSync.FormatBytes(freeBytes)} free where bundles are written, "
                             + $"{Sync.VAFapAssetSync.FormatBytes(MinimumFreeDiskBytes)} needed. Free space and run it again.";
                EmitRebakeNote(abort, isWarning: true);
                ShowToast(abort);
                yield break;
            }

            ShowToast($"Re-bake all: baking {total} blueprint(s) into fresh bundles...");

            var sweepStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var bakedKeys = new List<string>();
            int ok = 0, fail = 0, notAttempted = 0;
            for (int i = 0; i < total; i++)
            {
                long remaining = Utilities.VABakeBundle.FreeBytesForBundles();
                if (IsBelowDiskFloor(remaining))
                {
                    notAttempted = total - i;
                    string stop = $"Re-bake all stopped at {i + 1}/{total}: free space fell to {Sync.VAFapAssetSync.FormatBytes(remaining)}. "
                                + $"{notAttempted} blueprint(s) not attempted; the bundles already written are intact.";
                    EmitRebakeNote(stop, isWarning: true);
                    ShowToast(stop);
                    break;
                }

                var (cacheKey, blueprint) = bakeable[i];
                Utilities.VAFapLoadSummary.EmitRebakeStart(i + 1, total, cacheKey, blueprint.Objects.Count);
                ShowToast($"Re-bake all ({i + 1}/{total}): '{cacheKey}'...");

                VAFapBlueprintBakeCache.Remove(cacheKey);
                Utilities.VABakeBundle.Unpublish(cacheKey);
                yield return VaPieces.Instance.StartCoroutine(PreBakeCoroutine(cacheKey, blueprint));

                if (VAFapBlueprintBakeCache.Has(cacheKey)) { ok++; bakedKeys.Add(cacheKey); }
                else fail++;
            }

            yield return VaPieces.Instance.StartCoroutine(WaitForPendingBundleWrites("Re-bake all"));

            // The in-memory cache says a blueprint baked; only the file says it was kept. Bundles
            // are written on worker threads, so this is the first point where that is knowable.
            var lost = new List<string>();
            foreach (string cacheKey in bakedKeys)
                if (!Utilities.VABakeBundle.BundleLandedOnDisk(cacheKey)) lost.Add(cacheKey);
            if (lost.Count > 0)
            {
                ok -= lost.Count;
                fail += lost.Count;
                EmitRebakeNote($"{lost.Count} bake(s) never reached disk and were rolled back: {FormatNames(lost)}", isWarning: true);
            }

            Utilities.VAFapLoadSummary.EmitRebakeAllDone(ok, fail, emptyBlueprints, total, (float)sweepStopwatch.Elapsed.TotalSeconds);
            ShowToast(notAttempted > 0
                ? $"Re-bake all ended early: {ok}/{total} ok, {notAttempted} not attempted. Relaunch to use the new prefabs."
                : $"Re-bake all complete: {ok}/{total} ok. Relaunch to use the new prefabs.");
        }

        private static string FormatNames(List<string> names)
        {
            int shown = Math.Min(names.Count, MaxNamesInRebakeNote);
            string joined = string.Join(", ", names.GetRange(0, shown).ToArray());
            return names.Count > shown ? $"{joined} (+{names.Count - shown} more)" : joined;
        }

        // Unknown free space (a drive we can't read) is not a reason to refuse to bake.
        private static bool IsBelowDiskFloor(long freeBytes) =>
            freeBytes >= 0 && freeBytes < MinimumFreeDiskBytes;

        // Sweep-level notes go out on FAP's BepInEx source, the channel the rebake summaries
        // already use, so they survive alongside the per-blueprint boxes.
        private static void EmitRebakeNote(string message, bool isWarning)
        {
            var log = VaPieces.Log;
            if (log == null)
            {
                if (isWarning) Debug.LogWarning($"{VaPieces.PluginName}: [Rebake] {message}");
                else Debug.Log($"{VaPieces.PluginName}: [Rebake] {message}");
                return;
            }
            if (isWarning) log.LogWarning($"[Rebake] {message}");
            else log.LogInfo($"[Rebake] {message}");
        }

        // Every registered blueprint that has pieces, paired with its bake cache key, once per key.
        // Keys are compared ignoring case because bundle file names collide that way on Windows.
        private static List<(string CacheKey, VABlueprintLoader.Blueprint Blueprint)> CollectBakeableBlueprints(out int emptyBlueprints)
        {
            emptyBlueprints = 0;
            var bakeable = new List<(string CacheKey, VABlueprintLoader.Blueprint Blueprint)>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var stubName in VABlueprintHammerIntegration.RegisteredBlueprintPrefabNames)
            {
                if (!VABlueprintHammerIntegration.TryGetBlueprint(stubName, out var blueprint) || blueprint == null) continue;
                if (blueprint.Objects == null || blueprint.Objects.Count == 0) { emptyBlueprints++; continue; }
                string cacheKey = VABlueprintHammerIntegration.GetCacheKey(blueprint);
                if (string.IsNullOrEmpty(cacheKey) || !seenKeys.Add(cacheKey)) continue;
                bakeable.Add((cacheKey, blueprint));
            }
            return bakeable;
        }

        // 1.1.165: bakes on disk in an older format are re-baked once, automatically, the first time a client spawns (the
        // same per-blueprint path as fap_rebake_all; the new bundle is used from the next launch, as with fap_rebake_all).
        // A dedicated server has no graphics device, so it only keeps them queued.
        private static bool s_staleRebakeStarted;

        [HarmonyLib.HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        public static class Player_OnSpawned_RebakeStale_Patch
        {
            [HarmonyLib.HarmonyPostfix]
            public static void Postfix(Player __instance)
            {
                if (s_staleRebakeStarted || __instance != Player.m_localPlayer || Utilities.VABakeBundle.StaleBakes.Count == 0) return;
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null || VaPieces.Instance == null) return;
                s_staleRebakeStarted = true;
                VaPieces.Instance.StartCoroutine(RebakeStaleCoroutine());
            }
        }

        private static System.Collections.IEnumerator RebakeStaleCoroutine()
        {
            yield return new WaitForSeconds(10f);   // let the world settle first
            var stale = CollectBakeableBlueprints(out _).Where(b => Utilities.VABakeBundle.StaleBakes.Contains(b.CacheKey)).ToList();
            if (stale.Count == 0)
            {
                VaPieces.Log?.LogDebug($"[Rebake] {Utilities.VABakeBundle.StaleBakes.Count} old-format bake(s) have no registered blueprint to re-bake from: {string.Join(", ", Utilities.VABakeBundle.StaleBakes)}");
                yield break;
            }
            if (IsBelowDiskFloor(Utilities.VABakeBundle.FreeBytesForBundles()))
            {
                EmitRebakeNote($"{stale.Count} old-format bake(s) not re-baked: too little free disk space.", isWarning: true);
                yield break;
            }
            int ok = 0;
            foreach (var (cacheKey, blueprint) in stale)
            {
                VAFapBlueprintBakeCache.Remove(cacheKey);
                Utilities.VABakeBundle.Unpublish(cacheKey);
                yield return VaPieces.Instance.StartCoroutine(PreBakeCoroutine(cacheKey, blueprint));
                if (VAFapBlueprintBakeCache.Has(cacheKey)) { ok++; Utilities.VABakeBundle.StaleBakes.Remove(cacheKey); }
            }
            yield return VaPieces.Instance.StartCoroutine(WaitForPendingBundleWrites("Re-bake of old bakes"));
            VaPieces.Log?.LogDebug($"[Rebake] re-baked {ok} of {stale.Count} old-format bake(s) automatically ({string.Join(", ", stale.Select(s => s.CacheKey))}); they load from the next launch");
            if (ok < stale.Count) EmitRebakeNote($"{stale.Count - ok} old-format bake(s) failed to re-bake: {string.Join(", ", Utilities.VABakeBundle.StaleBakes)}", isWarning: true);
        }

        // Publish writes bundles on worker threads, and a restart mid-write leaves a .tmp that
        // never loads, so a sweep only reports done once every write has landed.
        private static System.Collections.IEnumerator WaitForPendingBundleWrites(string sweepName)
        {
            for (int framesWaited = 0; Utilities.VABakeBundle.PendingBundleWrites > 0 && framesWaited <= BundleFlushSafetyCapFrames; framesWaited++)
            {
                if (framesWaited % BundleFlushToastEveryFrames == 0)
                    ShowToast($"{sweepName}: flushing {Utilities.VABakeBundle.PendingBundleWrites} bundle write(s) to disk — DO NOT close the game yet…");
                yield return null;
            }
        }

        // fap_bake_all driver — walks every registered blueprint, runs the
        // read-only diagnostic, bakes each serially, and writes one report to
        // config/FiresAdminPrefabs/Diagnostics/bake_all_report.txt surfacing
        // zero-geometry pieces, unresolved materials, and churn-risk materials.
        public static System.Collections.IEnumerator BakeAllWithDiagnosticCoroutine()
        {
            var scene = ZNetScene.instance;
            if (scene == null) { ShowToast("Bake-all: ZNetScene not ready — run after spawning in."); yield break; }

            var bakeable = CollectBakeableBlueprints(out _);
            int total = bakeable.Count;
            if (total == 0) { ShowToast("Bake-all: no registered blueprints found."); yield break; }

            ShowToast($"Bake-all: baking + diagnosing {total} blueprint(s). This can take many minutes — watch the message hud.");

            var report = new System.Text.StringBuilder();
            var globalMissing = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var globalChurn = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var globalZero = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int sumPieces = 0, sumParts = 0; long sumTris = 0; int baked = 0, bakeFail = 0;
            var sweepStopwatch = System.Diagnostics.Stopwatch.StartNew();

            report.AppendLine("============================================================");
            report.AppendLine(" FAP BAKE-ALL DIAGNOSTIC");
            report.AppendLine($" blueprints: {total}");
            report.AppendLine("============================================================");
            report.AppendLine();

            for (int i = 0; i < total; i++)
            {
                var (cacheKey, blueprint) = bakeable[i];
                Utilities.VAFapLoadSummary.EmitRebakeStart(i + 1, total, cacheKey, blueprint.Objects.Count);
                ShowToast($"Bake-all ({i + 1}/{total}): '{cacheKey}'...");

                report.AppendLine($"--- [{i + 1}/{total}] {cacheKey} ---");

                VAFapBlueprintMeshBaker.DiagnoseSummary diagnostic = default;
                try { diagnostic = VAFapBlueprintMeshBaker.DiagnoseBlueprintBake(blueprint, scene, null, line => report.AppendLine("  " + line)); }
                catch (Exception ex) { report.AppendLine($"  [diagnostic threw: {ex.Message}]"); }

                AccumulateCounts(globalMissing, diagnostic.MissingMaterials);
                AccumulateCounts(globalChurn, diagnostic.ChurnRiskMaterials);
                AccumulateCounts(globalZero, diagnostic.ZeroGeometry);
                sumPieces += diagnostic.Pieces;

                VAFapBlueprintBakeCache.Remove(cacheKey);
                Utilities.VABakeBundle.Unpublish(cacheKey);
                yield return VaPieces.Instance.StartCoroutine(PreBakeCoroutine(cacheKey, blueprint));

                if (VAFapBlueprintBakeCache.TryGet(cacheKey, out var cached) && cached != null)
                {
                    int parts = cached.Parts != null ? cached.Parts.Count : 0;
                    int liveChildren = cached.LiveChildren != null ? cached.LiveChildren.Count : 0;
                    report.AppendLine($"  BAKED: {parts} part(s), {cached.TotalTris:N0} tris, {liveChildren} live child(ren).");
                    sumParts += parts; sumTris += cached.TotalTris; baked++;
                }
                else { report.AppendLine("  BAKED: (no cache entry — bake produced nothing)"); bakeFail++; }

                report.AppendLine();
                yield return null;
            }

            yield return VaPieces.Instance.StartCoroutine(WaitForPendingBundleWrites("Bake-all"));

            sweepStopwatch.Stop();

            report.AppendLine("============================================================");
            report.AppendLine(" MASTER SUMMARY");
            report.AppendLine("============================================================");
            report.AppendLine($" blueprints: {total} | baked ok: {baked} | bake issues: {bakeFail}");
            report.AppendLine($" total pieces: {sumPieces:N0} | total parts: {sumParts:N0} | total tris: {sumTris:N0}");
            report.AppendLine($" wall-clock: {sweepStopwatch.Elapsed.TotalSeconds:F1}s");
            report.AppendLine();
            AppendCountedSection(report, "MATERIALS THAT DON'T RESOLVE (substituted -> wrong colours; fix these)", globalMissing);
            AppendCountedSection(report, "INSTANCE-SUFFIXED MATERIALS (cross-session churn risk - HD/shader-swap)", globalChurn);
            AppendCountedSection(report, "ZERO-GEOMETRY PREFABS (recovered or still-missing - see per-bp causes)", globalZero);

            string path = "(unset)";
            try
            {
                string dir = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "FiresAdminPrefabs", "Diagnostics");
                System.IO.Directory.CreateDirectory(dir);
                path = System.IO.Path.Combine(dir, "bake_all_report.txt");
                System.IO.File.WriteAllText(path, report.ToString());
            }
            catch (Exception ex)
            {
                ShowToast($"Bake-all: report write FAILED: {ex.Message}");
                Debug.LogWarning($"{VaPieces.PluginName}: fap_bake_all report write failed: {ex.Message}");
                yield break;
            }

            Utilities.VAFapLoadSummary.EmitRebakeAllDone(baked, bakeFail, 0, total, (float)sweepStopwatch.Elapsed.TotalSeconds);
            ShowToast($"Bake-all complete: {baked}/{total} baked. Report -> {path}. Relaunch to load new bundles.");
            Debug.Log($"{VaPieces.PluginName}: fap_bake_all — report written to {path} ({report.Length} chars). Missing mats: {globalMissing.Count}, churn-risk: {globalChurn.Count}, zero-geometry prefabs: {globalZero.Count}.");
        }

        private static void AccumulateCounts(SortedDictionary<string, int> agg, List<string> items)
        {
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (string.IsNullOrEmpty(item)) continue;
                agg.TryGetValue(item, out var count);
                agg[item] = count + 1;
            }
        }

        private static void AppendCountedSection(System.Text.StringBuilder sb, string title, SortedDictionary<string, int> agg)
        {
            sb.AppendLine($" {title}: {agg.Count}");
            foreach (var entry in agg)
                sb.AppendLine($"    {entry.Key}  (in {entry.Value} blueprint{(entry.Value == 1 ? "" : "s")})");
            sb.AppendLine();
        }

        // ──────────────────────────────────────────────────────────────
        //  Helpers
        // ──────────────────────────────────────────────────────────────

        private static void ShowToast(string msg)
        {
            var player = Player.m_localPlayer;
            if (player != null) player.Message(MessageHud.MessageType.Center, msg);
            else Debug.Log($"{VaPieces.PluginName}: {msg}");
        }
    }
}
