# FiresAdminPrefabs

**Requires Valheim 1.0.30 or newer.**

## What's new in 1.1.434

- The FiresGlass windows are part of this mod now; windows you placed stay.
- Own icons for pieces that shared a picture; faster loading.
- Culling and vegetation LOD settings moved to Fires Easy Bake Meshes.

**Updated for Valheim 1.0.**

An admin build suite for Valheim. FiresAdminPrefabs (FAP) turns the hammer into a full world-building tool: every prefab in the game (props, nature, clutter, effects, creatures, items, dungeons and asset-bundle content) becomes a placeable piece, sorted into tabs you control.

> Need help, found a bug, want a feature? **https://discord.gg/H9uKGcAujs**

## What's new in 1.1.411

Since 1.1.353, placing whole dungeons works properly, building pieces line up with the popular build-pieces pack, a
long-standing bug that left status effects behind is fixed, and FAP does less work every time something appears in
the world.

### Biggest changes

- **Whole dungeons from the hammer.** The Dungeons tab has one entry per dungeon and places the whole dungeon from its
  blueprint. Dungeon entrances are listed inside the Dungeons sort, and the ghost preview shows the real entrance.
  Only admins can place them (the server checks), and an admin can remove them with the hammer.
- **Entrances and Locations sorted by biome.** Their right-click menu lets you choose "Fully generated" or "Empty".
- **Snap points match the popular build-pieces pack**, so builds made with either one fit together. Pieces that pack
  gives no snap points get one point at their centre.
- **World loot chests on the hammer.** 14 world loot chests and 2 Morkhalla debris pieces, each with its own name.
  Right-click one in the build menu to choose "Place as storage" or "Place as vanilla"; your choice is remembered for
  that piece.
- **Castle tower pieces are back.** Their detailed close-up model loads within 500 m and unloads past 600 m.
- **Blueprints can be kept in subfolders.**
- **Single player** always gets the full admin build menu and settings.

### Fixed

- Wet and other status effects no longer get stuck on you after a creature dies or after you log back in. Effects
  already left behind in your world are cleared when it loads.
- Lanterns, bridge stone and arches from the building bundles no longer look white or flat.
- An error that left the Entrances and Locations lists empty.
- An empty dungeon entrance no longer fills the log every frame.
- Guide points with a missing raven no longer cause an error.
- The "could not find EasyBakeMeshes" warning when that mod loads later or isn't installed.
- The Berserker mushroom gives the right item when picked.

### Smoother play

- Less work each time an object appears in the world, so loading into busy areas is lighter.
- The location list is built the first time an admin opens the hammer, then kept until the game or your mod list
  changes.
- With Fires Easy Bake Meshes 1.2.210 or newer installed, distant pieces from the building packs are handed to it to
  draw far away.
- Fewer log lines at the default level.

### Settings that are new or changed

- **New: `[LazyBundles] Prewarm`** (off by default). Loads bundles ahead of time so the first piece from a bundle
  doesn't stutter, but uses more memory. The setting's description says how much.
- **New: an optional vanilla spawner for each enemy entry.** Its settings are off by default.
- **Changed for dedicated servers: `[Performance] DediSkipRenderMeshes` now defaults to `*`.** A dedicated server
  keeps the solid shapes it needs but skips loading the visual models it never draws, which saves memory. This only
  applies to new config files; an existing config keeps its value. Server admins who want the saving can set it to
  `*` themselves.

### On update

- Nothing to reset. Update the server and every client to 1.1.411 together, as always.

## What you get

- **Every prefab in the hammer**, in tabs: Nature, CreatorShop, Clutter, Monsters, Item Stands, FX / VFX, Dungeons, Blueprints, Baked Meshes and Favorites, next to the vanilla tabs.
- **A better build menu** for everyone with the mod: a search bar across all tabs, a resizable scrolling grid, favorites, and your own tabs.
- **Whole dungeons and entrances** (admins): place a full dungeon, or just its entrance, fully generated or empty.
- **Monsters and chests**: place a creature with its level, stars and respawn timer, or a chest that is already filled. Save both as presets. World loot chests can be placed as storage or as the vanilla chest.
- **Clutter painting**: paint grass and ground detail with a brush.
- **Item stands**: one tile per item, placed with the item already on it.
- **Effects**: fire, smoke, sparks, mist and magic as placeable set dressing.
- **Blueprints and baked meshes**: place `.blueprint` / `.vbuild` files with their saved data (subfolders work), or bake one into a single fast-to-draw piece.
- **Light and sound editors** for placed pieces.
- **Radius Swapper**: swap every copy of one piece for another in an area.
- Snap points for building pieces that line up with the popular build-pieces pack, and extra snap points for vanilla pieces that had too few.

## How to use

- **Build menu:** Q / E switch tabs, the mouse wheel scrolls. Drag the `//` grip in the bottom-right corner to resize, and drag the empty wood area to move it.
- **Shift + right-click a piece** in the build menu to favorite it, hide it, move it to another tab, make a new tab, or "Copy ID" (copies its name). The key is `[BuildMenu] ContextMenuKey`. Some pieces offer more here, such as "Place as storage" / "Place as vanilla" on loot chests, or "Fully generated" / "Empty" on entrances and locations.
- **Shift + right-click a tab** to rename or hide it.
- **Admins: open the right-click menu on a placed piece** in the world for "Edit light..." or "Edit sound...". Changes are saved on that piece and everyone sees and hears them. A piece with both can switch between the two editors.
- **Radius tools** (clutter brushes, clear brushes, Radius Swapper): mouse wheel = radius, Shift + wheel = height. Clutter Paint uses Shift + wheel to scale the brush instead.
- **Radius Swapper** (Misc tab): click, pick the piece to swap, pick what to swap it to, confirm, then click in the world. Esc cancels. Its right-click menu has "Setup swap preset".
- **Baking:** right-click a blueprint and choose **Bake to prefab** (turn on `[Performance] EnableBlueprintMeshBake` first). It shows up in Baked Meshes straight away. For other players, run `fap_push_bundle`, then relaunch the client and the server.
- **Your own tabs** are folders under `BepInEx/config/FiresAdminPrefabs/HammerTabs/`. Each has a `prefabs.txt`, one prefab per line, in the order they show in game. Your tabs and favorites are never synced from the server.
- **Asset bundles:** drop bundles into `BepInEx/config/FiresAdminPrefabs/Assets/` and their prefabs join the hammer. Joining players download only the files that changed, and are asked to reconnect after new assets arrive.

## Settings an admin might change

- `[General] AdminOnlyMode` - only admins can place FAP content on a server.
- `[General] VanillaFriendly` - the build menu only offers what players without FAP can see.
- `[General] ServerEnforced` - forces the server's settings onto every client.
- `[General] UseEwpForPersistence` - lets ExpandWorldPrefabs handle persistence and respawn timers, so vanilla players see them too.
- `[HammerToggles]` - place through the ground or other objects, stop props snapping to the ground, stop random rotation, and more.
- `[Performance] DediSkipRenderMeshes` - on a dedicated server, skip the visual models of these prefabs (`*` = all) to save memory. Solid shapes are kept.
- `[LazyBundles] Prewarm` - load bundles ahead of time: no first-use stutter, more memory. Off by default.
- Turn `debugmode` off to see the build menu exactly as a player does.

## Admin commands

Run in the console (F5).

| Command | What it does |
|---|---|
| `fap_assetsdir` | Show the asset bundle folder and what's in it |
| `fap_reload_icons` | Re-draw every piece icon |
| `fap_bake_all` / `fap_rebake_all` | Bake, or re-bake, every blueprint |
| `fap_push_bundle [name]` | Send your baked blueprints to the server |
| `fap_push_presets` | Send your chest and monster presets to the server |
| `fap_clear_orphan_fx` | Remove leftover effects from the world |
| `fap_orphan_zdo scan` / `purge` | Find, then remove, objects whose prefab no longer exists |
| `fap_purge_prefab <prefab> [radius\|all] [confirm]` | Remove every copy of one prefab near you or world-wide. Without `confirm` it only reports. Can't be undone |

## Install

- On a server that runs FAP, install it on the **server and every client** at the **same version**. Players on an older version are refused until they update.
- You can also run FAP on just your own client, on a server that doesn't have it. The build menu then hides what other players couldn't see.

## Requires

- [FiresUnifiedCore](https://thunderstore.io/c/valheim/p/VerdantsAscent/FiresUnifiedCore/)
- [YamlDotNet](https://thunderstore.io/c/valheim/p/ValheimModding/YamlDotNet/)
- Optional: ExpandWorldPrefabs, for persistence and respawn timers vanilla players also see.
- Optional: Fires Easy Bake Meshes 1.2.210 or newer, to draw distant building-pack pieces.

## Good to know

- Works with Infinity Hammer and WackysDatabase.
- Baked meshes are much cheaper to draw but can't be edited piece by piece.

## Credits

- The container-and-sign behaviour that lets **Missing Pieces** bundles load without that mod is adapted from [BentoG's MissingPieces](https://github.com/OrianaVenture/Valheim-MissingPieces) (WTFPL).
