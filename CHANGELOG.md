## 1.1.445
Requires Valheim 1.0.30 or newer.
- Works with Valheim 1.0.30.
- Less memory at world load: piece textures load only when a piece needs them.
- Less background work for item stands and piece icons; dedicated servers skip icon work.

## 1.1.434

### New
- The 34 FiresGlass windows now come with this mod (they used to be in Fires Tossin' Shade, which is retired). Windows you already placed stay where they are.
- Pieces that shared another's picture get their own hammer icon; ashwood and black marble variants get icons.

### Faster
- Setup runs under the loading screen and the settings file is written once (start 1.5 s -> 0.65 s); piece bundles get a local cache.
- Less per-frame work for piece sounds, item stands and look-ahead loading. Diagnostics are fully inactive unless switched on.

### Changed
- Distant pieces are marked only when placed. Far views of baked blueprints now come from Fires Easy Bake Meshes.
- Removed: the Horizon Cull, Shadow Caster Cull and Vegetation LOD settings (now in Fires Easy Bake Meshes).

### Fixed
- The farm mushroom pickable gives its item like the wild one.

## 1.1.411
- A dungeon entrance is logged once, only when it really spawns, and a refused empty entrance now says so.

### New
- Snap points on the building pieces line up the same way as in the popular build-pieces pack, so builds made with either one fit together. Pieces that pack gives no snap points get one point at their centre.
- Dungeons tab: one entry per dungeon, which places the whole dungeon from its blueprint. Dungeon entrances are listed inside the Dungeons sort, and the ghost preview shows the real entrance. They are admin only (the server checks), and an admin can remove them with the hammer.
- The Entrances and Locations lists are sorted by biome. A right-click menu offers "Fully generated" or "Empty".
- Blueprints can be kept in subfolders.
- 14 world loot chests and 2 Morkhalla debris pieces are now on the Hammer, each with its own name. Right-click in the build menu to choose "Place as storage" or "Place as vanilla". The choice is remembered per piece.
- Optional vanilla spawner per enemy entry. The settings for this are off by default.
- Single player always gets the full admin build menu and settings.
- New setting "Prewarm" under Lazy Bundles, OFF by default. It loads bundles ahead of time, so the first piece from a bundle doesn't stutter, but it uses more memory. The description states the memory cost.
- Castle tower pieces are back. Their detailed close-up model loads within 500 m and unloads past 600 m.

### Changed
- Dedicated servers: the "Dedi Skip Render Meshes" default is now `*`, so a server keeps colliders but skips the visual meshes it never draws. This only applies to NEW config files. An existing config keeps its value.
- Copied materials in the building bundles now take the matching game material whole. This fixes white or flat lanterns, bridge stone and arches.
- The location list is built the first time an admin opens the hammer, then cached until the game or mod list changes.
- Fewer log lines at the default level: repeating and per-item lines moved to Debug.

### Fixed
- Wet drops and other status effects no longer get left behind after a creature dies or you log back in. FAP no longer marks shared game effects as saved; each placement is saved on its own. Effects already left behind are cleared when the world loads.
- An empty dungeon entrance no longer floods the log every frame.
- An error that left the Entrances and Locations lists empty.
- Guide points with a missing raven no longer throw an error.
- The "could not find EasyBakeMeshes" warning when that mod loads later or isn't installed.
- The Berserker mushroom pickable gives the right item.

### Performance
- Less work each time an object is created: bundle files are indexed once at load instead of checked on disk for every object, per-prefab answers are cached, and piece and object setup runs through one hook each instead of 13.
- Distant building-pack prefabs are handed to EasyBakeMeshes for far drawing when it is installed (EasyBakeMeshes 1.2.210 or newer).

## 1.1.353
- Work in progress: placing dungeons from the hammer (whole or empty entrance) is not working as intended yet, and some of the new features (spawners and nests on the hammer, event zones with presets, spawner boxes) may still have bugs to work out. (dungeons)
- The Hammer has a Dungeons tab: admins can place a whole vanilla dungeon or just its empty entrance. If the world refuses the spot, it now says nothing spawned instead of reporting success. (building)
- Spawners, nests and event zones placed from the Hammer show a box you can hover over and remove while the Hammer is in hand ("Show spawners with hammer out", on by default). World spawners are not touched. (building)
- Spike traps from the Ashlands and Dvergr stake walls now hurt creatures the way vanilla sharpened stakes do ("SpikeTriggerLayerFix", on by default). (building)
- Hoe and cultivator menus show all their pieces again, and build menus with a single page list everything in one tab. (building)
- Build-menu favourites and recent pieces are saved properly, including on a dedicated server. (building)
- "New piece faces away from you" moved here from Core, with your old value. (building)
- Creatures and NPCs that have no player skin no longer render wrongly in their equipment. Piece icons get proper margins. (visuals)
- Blueprints and piece presets (lights, rendering, monsters, NPC templates) can be listed, saved, applied and deleted from the new World Studio, as one shared store with the in-game tools. (admin)

## 1.1.328
- Sound pieces get a small speaker icon, so build-planning mods list them correctly.
- Fixed item stands set by world edit commands showing nothing.
- Snap points match the original building mod, so pieces line up as before.
- Fixes for a player-reported building issue.

## 1.1.316
- Bug fixes and improvements.

## 1.1.302
- New sound editor: right-click a placed piece and pick "Edit sound..." to change any of its sounds. The change is saved on that one piece and everyone hears it.
- Placed lights now open from the same right-click menu ("Edit light..."). A piece with both can switch between the two editors, which now share one look.
- New Radius Swapper (Misc tab): swaps every copy of one piece for another inside a circle. Save your swaps as presets.
- "Copy ID" on a piece's menu in the build menu copies its name, ready to paste into a swap.
- All radius tools: mouse wheel sets the radius, Shift + wheel sets the height. Clutter Paint uses Shift + wheel to scale the brush, and its preview shows the real size.
- Un-hiding a piece or tab, and a new bake, show up right away without reloading.
- Fixed build menu icons turning grey away from a workbench when building costs are switched off.

## 1.1.281
- Cleaner settings screen: the basics show first, the rest is under Show Advanced (your saved values are kept).
- Turning on Expand World Prefabs persistence now switches FAP's own persistence off.
- Locked settings say why they are locked.
- Persistence switches now follow the server's settings.

## 1.1.275
- You can move and resize the build menu; Reset puts it back.
- Fixed build menu errors with mods that add their own build tabs (such as Infinity Hammer).
- Dev pieces show a warning badge, and you can hide them.
- New brush that makes placed effects stay for good.
- More options for placed lights, plus better effect icons.
- Less memory used while downloading assets, and item stand fixes.

## 1.1.212
- Fixed Expand World Prefabs failing to start alongside FAP.

## 1.1.211
- Works with Infinity Hammer: FAP pieces place under either hammer, and the blueprint menu no longer freezes.
- Item stand fixes, and some rocks show their proper look.

## 1.1.208
- Updated for Valheim 1.0, with faster joins and lighter servers.
- You can now play with FAP on your own client, on servers that don't have it.
- Vanilla build pieces get extra snap points (no separate snap mod needed).
- The Chaos Hammer menu works again, and nature pieces from Balrond mods show properly.

## 1.1.59
- Fixes and improvements.

## 1.1.57
- Fixes and improvements.

## 1.1.13
- First release.
