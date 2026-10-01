# FiresAdminPrefabs

**Updated for Valheim 1.0.**

An admin build suite for Valheim. FiresAdminPrefabs (FAP) turns the vanilla hammer into a full world-building tool: every prefab the game knows about (props, nature, clutter, effects, creatures, items and asset-bundle content) becomes a placeable piece, organized into tabs you control, with search, blueprints, presets and clutter painting on top.

> Need help, found a bug, want a feature? **https://discord.gg/H9uKGcAujs**

## Requirements

- [FiresUnifiedCore](https://thunderstore.io/c/valheim/p/VerdantsAscent/FiresUnifiedCore/) and [YamlDotNet](https://thunderstore.io/c/valheim/p/ValheimModding/YamlDotNet/).
- On a server that runs FAP, install it on the **server and every client** at the **same version**. Players on an older version are refused at join until they update.
- **Client only:** you can also run FAP on your own client, on a server that doesn't have it. The admin-only lock doesn't apply there, and the build menu hides what other players couldn't see (bundle prefabs, clutter, baked meshes and blueprints).
- Optional: ExpandWorldPrefabs, for vanilla-safe persistence and monster/chest respawn timers.

## Who can use it

- Placing FAP content is meant for admins. `[General] AdminOnlyMode` limits placement to admins on a server.
- The build menu improvements (search, resizing, scrolling, favorites) work for anyone with the mod.
- As an admin, turn `debugmode` off to see the build menu exactly as a player does, so you can check what you are publishing without logging out.
- `[General] RegisterPiecesForNonAdmins` keeps FAP's pieces registered for non-admins while still hiding them from that player's menu. Mods that only recognise registered build pieces, such as WackysDatabase clones, then work for everyone.
- Not every player has FAP? Turn on `[General] VanillaFriendly`. The build menu then only offers what a vanilla client renders and keeps on its own: no clutter, no baked meshes or blueprints, no item stands vanilla will not accept, and only effects that survive without the mod. `[General] VanillaFriendlyEffectAllowlist` adjusts that effect list, and a `-name` entry drops one of the built-ins.

## The build menu

- **Every prefab in the hammer**, sorted into tabs: Nature, CreatorShop, Clutter, Monsters, Item Stands, FX / VFX, Blueprints, Baked Meshes and Favorites, alongside the vanilla tabs.
- **Search bar** that filters across all tabs at once. It matches names and descriptions, and "stone floor" finds `stone_floor`.
- **Resizable, scrolling grid.** Drag the `//` grip in the bottom-right corner (6-30 columns, 4-20 rows) and your size is saved. Drag the empty wood area to move the panel.
- Mouse wheel scrolls the grid, Q / E switch tabs, and arrow keys and gamepad follow your real column count.
- **Snap points** added to vanilla props that shipped with none or too few: dvergr poles, beams and walls, goblin walls and roofs, black marble floors, columns and tiles, Ashlands walls, floors and stairs, doors, gates and chests. `fap_snapdump` shows the points a piece really has.
- Works with **Infinity Hammer** (copy, zoop and `hammer_measure` behave at any grid size; its custom build menu takes priority over FAP's tab layout while it is active) and **Comfy Gizmo**. If **SearsCatalog** is installed, FAP leaves the build panel to it.
- Works with **WackysDatabase**: FAP's pieces resolve as clone sources even though FAP registers them later than WDB looks, and a clone answers FAP's own vanilla-friendly and admin rules the same way the piece it was cloned from does. FAP refuses only the prefabs that mean nothing without it — blueprint placeholders, stand-ins and baked-mesh prefabs — because a clone of one would save a build nothing can load.

## Tabs, favorites and hiding

- **Right-click a piece** to favorite it, hide it, move it to another tab (vanilla tabs included) or create a new tab. Any piece can move: vanilla, other mods' or FAP's.
- **Right-click a tab** to rename or hide it. Admins also get **Vanilla only** there, and **EWP persistence** on the FX and VFX tabs, so both settings can be flipped without leaving the game.
- Tabs are folders under `BepInEx/config/FiresAdminPrefabs/HammerTabs/`. Each tab has a `prefabs.txt` with one prefab per line, and **line order is the in-game order**. Cut and paste lines to move pieces in bulk. Changes apply on world reload.
- Your tab layout and favorites belong to you. They are never synced from the server.
- Per-piece **render distance** (up to 1500m) and **shader** overrides are in the right-click menu.

## Content

- **Monsters:** one stand-in per creature, and placing it spawns the real creature. Configure level, stars, boss flag and respawn timer, and save presets to reuse.
- **Containers:** configure a chest's contents once, save it as a preset, then place as many pre-filled chests as you like.
- **Clutter:** paint grass and ground detail with a brush (`[Clutter] PaintRadius` / `PaintDensity`), and set clutter presets world-wide.
- **Item Stands:** one tile per item, wall-mounted (V) or shelf (H), placed with the item already on it. `[HammerToggles] ItemStandHideStandVisual` hides the stand so the item floats.
- **FX / VFX:** fire, smoke, sparks, mist and magic effects as placeable set dressing. Right-click **Place with Persistence** to stop an effect from removing itself.
- **Blueprints:** drop `.blueprint` or `.vbuild` files into `HammerTabs/Blueprints/` and place them from the Blueprints tab with their saved data intact (sign text, chest contents, item stands, health and more).
- **Baked Meshes:** right-click a blueprint and choose **Bake to prefab** to combine it into one optimized mesh (needs `[Performance] EnableBlueprintMeshBake`). After a relaunch it appears in the Baked Meshes tab. Baked structures are much cheaper to render, but they can't be edited piece by piece. Pieces carrying their own saved data, such as a custom name or an ExpandWorldPrefabs hook, are placed for real instead of being merged, so they keep working. A bake stops rather than half-writing if the drive runs low, and only counts a blueprint as baked once its file is actually on disk.

## Server features (admin)

- **Hammer toggles** under `[HammerToggles]`: clip into the ground or through everything, stop placed props from snapping to the ground, force persistence, suppress random rotation, swap broken variants for intact ones, and override effect timers.
- **`[General] ServerEnforced`** forces the server's synced settings onto every client.
- **Asset bundles:** drop bundles into `BepInEx/config/FiresAdminPrefabs/Assets/` and their prefabs join the hammer. Connecting players download only the files that differ (assets, manifests, shaders, textures and baked meshes), verified before they are used. Players are asked to reconnect after new assets arrive.
- **Persistence and respawns:** with `[General] UseEwpForPersistence`, FAP writes ExpandWorldPrefabs rules so the server enforces persistence and respawn timers, and vanilla clients see it too. Use `fap_push_presets` or `[Sync] AutoPushOnPlace` to send your presets and rules to a dedicated server.

## Admin commands

All commands start with `fap_` and run in the console (F5).

| Command | What it does |
|---|---|
| `fap_assetsdir` | Show the asset bundle folder and what's in it |
| `fap_reload_icons` | Re-render every piece icon |
| `fap_bake_all` / `fap_rebake_all` | Bake, or re-bake, every blueprint |
| `fap_bake_diagnose <name>` | Explain what a bake would include, without baking |
| `fap_push_presets` | Push preset and ExpandWorldPrefabs rule files to the server |
| `fap_ewpstatus` / `fap_cleanewp` / `fap_wipeewp confirm` | Inspect, clean or reset FAP's ExpandWorldPrefabs files |
| `fap_clear_orphan_fx` | Remove leftover status-effect FX from the world |
| `fap_recategorize_clutter` / `fap_clean_stale_clutter` | Tidy clutter tabs and leftover clutter objects |
| `fap_orphan_zdo scan` / `fap_orphan_zdo purge` | Find, then remove, saved objects whose prefab no longer exists — left behind by an older build or a removed bundle |
| `fap_purge_prefab <prefab> [radius\|all] [confirm]` | Remove every copy of one prefab near you (64 m by default) or world-wide. Without `confirm` it only reports what it would remove. It can't be undone |
| `fap_clear_legacy_persistence` | Clear the old persistence tag from every object in the world, once, after updating from an old version that tagged effects on monsters and vanilla content |
| `fap_push_bundle [name]` | Send your locally baked blueprints up to the connected server (all of them, or those whose name contains `name`) |
| `fap_dump_categories` | List every build tab the hammer has, its piece count and whether FAP owns it |
| `fap_snapdump [prefab]` / `fap_snaplive on\|off` | Show the snap points a piece really has (the selected piece, or a named prefab), or log which snap pair the placement ghost picks while you build |
| `fap_fx_dump` | List every visible object near you with where it came from, to find an effect `fap_clear_orphan_fx` doesn't catch |

`fap_push_bundle`, `fap_snapdump`, `fap_snaplive` and `fap_fx_dump` need `devcommands` on.

## Credits

- The container-and-sign behaviour that lets **Missing Pieces** bundles load without that mod (`ExternalScripts/MissingPieces/CustomInteract.cs`) is adapted from [BentoG's MissingPieces](https://github.com/OrianaVenture/Valheim-MissingPieces) (WTFPL).
