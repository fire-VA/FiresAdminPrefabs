using System;
using UnityEngine;
using VerdantsAscent.Managers;
using Splatform;

namespace VerdantsAscent.Utilities
{
    // Places a vanilla ItemStand at a world position with an item already
    // attached. Writes the item directly to the stand's ZDO using the same
    // keys and types ItemStand.UpdateVisual reads on every tick (since Valheim
    // 1.0 the item is its prefab-name hash, not the name), then fires an
    // RPC_UpdateVisual broadcast so the item visual renders immediately
    // instead of waiting for the 4s InvokeRepeating cycle.
    public static class VAItemStandSpawner
    {
        private static readonly int K_Item    = "item".GetStableHashCode();
        private static readonly int K_Variant = "variant".GetStableHashCode();
        private static readonly int K_Quality = "quality".GetStableHashCode();
        private const int DefaultVariant = 0;
        private const int DefaultQuality = 1;

        // Infinity-Hammer-style "float without stability" recipe — same
        // pattern FAP's VABakeParentPrefab applies to bake-parent shells.
        // See VABakeParentPrefab.EnsureRegistered for the canonical site.
        //
        // The vanilla item_stand prefab ships with m_health=200,
        // m_supports=true. Walls / pillars adjacent to a stand placement
        // get probed for support, and if the player removes the support
        // later, the stand decays via WearNTear.UpdateSupport. The user
        // wants the FAP item-stand tab to drop indestructible / no-support
        // items so they float vanilla-friendly in midair.
        private const float IndestructibleSentinelHealth = -1f;
        private const float MaxSupportSentinel = 1e9f;

        public static int SpawnAt(string itemName,
                                  VAItemStandStandinFactory.Orientation orientation,
                                  Vector3 pos,
                                  Quaternion rot,
                                  Player placer)
        {
            if (string.IsNullOrEmpty(itemName)) return 0;
            var scene = ZNetScene.instance;
            if (scene == null) return 0;

            string standPrefabName = orientation == VAItemStandStandinFactory.Orientation.Vertical
                ? VAItemStandStandinFactory.VanillaVerticalPrefabName
                : VAItemStandStandinFactory.VanillaHorizontalPrefabName;
            var standPrefab = scene.GetPrefab(standPrefabName);
            if (standPrefab == null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAItemStandSpawner — vanilla prefab '{standPrefabName}' not found in ZNetScene.");
                return 0;
            }

            var itemEntry = VAItemStandRegistry.Resolve(itemName);
            if (itemEntry == null || itemEntry.Prefab == null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAItemStandSpawner — item '{itemName}' not in registry (vanilla-friendly filter may exclude it).");
                return 0;
            }

            GameObject clone;
            try
            {
                using (VAUndoBridge.Scope())
                    clone = UnityEngine.Object.Instantiate(standPrefab, pos, rot);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAItemStandSpawner Instantiate failed: {ex.Message}");
                return 0;
            }

            var clonePiece = clone.GetComponent<Piece>();
            if (clonePiece != null && placer != null)
            {
                try { clonePiece.SetCreator(placer.GetPlayerID(), PlatformManager.DistributionPlatform.LocalUser.PlatformUserID); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAItemStandSpawner SetCreator threw: {ex.Message}");
                }
            }

            ApplyInfinityFloat(clone);

            var cloneNview = clone.GetComponent<ZNetView>();
            if (cloneNview == null || !cloneNview.IsValid())
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAItemStandSpawner — placed stand has no valid ZNetView; visual won't render.");
                return 1;
            }
            var zdo = cloneNview.GetZDO();
            if (zdo == null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAItemStandSpawner — placed stand has no ZDO; visual won't render.");
                return 1;
            }

            zdo.Set(K_Item, itemEntry.Prefab.name.GetStableHashCode());
            zdo.Set(K_Variant, DefaultVariant);
            zdo.Set(K_Quality, DefaultQuality);

            // Persist the -1 health to the ZDO so the indestructible /
            // no-support-decay state survives reload. Vanilla
            // WearNTear.ApplyDamage early-returns when the ZDO's s_health
            // <= 0 (assembly_valheim/WearNTear.cs L707-708), so -1f is
            // the canonical immune sentinel. s_support maxed-out keeps
            // the support-color visual at "green" so the stand never
            // shows the stability tint.
            try
            {
                zdo.Set(ZDOVars.s_health, IndestructibleSentinelHealth);
                zdo.Set(ZDOVars.s_support, MaxSupportSentinel);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAItemStandSpawner — failed to write indestructible ZDO keys: {ex.Message}");
            }

            try
            {
                cloneNview.InvokeRPC(ZNetView.Everybody, "RPC_UpdateVisual");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAItemStandSpawner InvokeRPC RPC_UpdateVisual failed: {ex.Message}");
            }

            if (ConfigManager.Instance.configVerboseLogging.Value)
                Debug.Log($"{VaPieces.PluginName}: Placed {standPrefabName} at {pos} with item={itemName} (orientation={orientation}).");
            // 1.1.197: one passive Debug line per placed stand (evidence only, no behaviour change).
            VaPieces.Log?.LogDebug($"[FAP] item stand placed: {standPrefabName} {zdo.m_uid} at {pos:F1} item={itemName} orientation={orientation}");
            return 1;
        }

        // Mutate the spawned vanilla item_stand's WearNTear to the IH
        // indestructible recipe: -1 health (immune via ApplyDamage
        // early-return), no support / roof / ash / snow wear (the support
        // and roof flags read backwards, so false is what turns them off),
        // doesn't act as a support anchor for other pieces. These fields
        // revert to the vanilla prefab on reload; the -1 health SpawnAt
        // writes to the ZDO is what keeps a reloaded stand immune.
        private static void ApplyInfinityFloat(GameObject stand)
        {
            if (stand == null) return;

            var wnt = stand.GetComponent<WearNTear>();
            if (wnt == null) return;

            wnt.m_health           = IndestructibleSentinelHealth;
            wnt.m_noSupportWear    = false;
            wnt.m_noRoofWear       = false;
            wnt.m_ashDamageImmune  = true;
            wnt.m_snowDamageImmune = true;
            wnt.m_supports         = false;
        }
    }
}
