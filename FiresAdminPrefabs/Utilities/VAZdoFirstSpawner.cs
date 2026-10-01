using System;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Spawns a networked prefab the way the world loader does: create the ZDO, write
    // the blueprint's saved data into it, and only then instantiate the GameObject
    // bound to that ZDO through ZNetView.m_initZDO.
    //
    // Object.Instantiate can't do this — ZNetView.Awake would create a fresh default
    // ZDO, so everything read during Awake or the first tick sees defaults instead of
    // the blueprint's values: WearNTear health and support, Structure Tweaks' fall/wear
    // overrides, vanilla's HasFields overrides, m_syncInitialScale, sign text,
    // container inventories. The symptom that forced this was floating blueprints saved
    // with infinite health collapsing on placement, because ST only grants its 1e19
    // support when s_health is already above that threshold at the first UpdateSupport.
    //
    // Mirrors WorldEditCommands' DataHelper.Init and vanilla ZNetScene.CreateObject:
    // CreateNewZDO, write the blob, then rotation/persistent/distant/type/prefab/
    // revision, then any caller mutations — all still pre-Awake — and finally assign
    // m_initZDO and Instantiate.
    //
    // An unconsumed init-ZDO is destroyed rather than just nulled as vanilla does, so a
    // failed spawn can't leak an orphan into the world save.
    public static class VAZdoFirstSpawner
    {
        public static GameObject Instantiate(
            GameObject prefab,
            Vector3 pos,
            Quaternion rot,
            VAZdoDataBlob blob,
            string extraInfo,
            Action<ZDO> mutate,
            out bool dataApplied)
        {
            dataApplied = false;
            if (prefab == null) return null;

            var nview = prefab.GetComponent<ZNetView>();
            if (nview == null || ZDOMan.instance == null || ZNetView.m_forceDisableInit)
            {
                // Non-networked prefab (or no net session) — plain spawn,
                // best-effort post-hoc writes so group ids still land.
                var plain = UnityEngine.Object.Instantiate(prefab, pos, rot);
                var lateView = plain != null ? plain.GetComponent<ZNetView>() : null;
                var lateZdo = (lateView != null && lateView.IsValid()) ? lateView.GetZDO() : null;
                if (lateZdo != null)
                {
                    if (blob != null)
                    {
                        blob.WriteTo(lateZdo);
                        blob.ApplyHashConnection(lateZdo);
                        dataApplied = true;
                    }
                    mutate?.Invoke(lateZdo);
                }
                return plain;
            }

            int prefabHash = prefab.name.GetStableHashCode();
            var zdo = ZDOMan.instance.CreateNewZDO(pos, prefabHash);
            zdo.Persistent = nview.m_persistent;
            zdo.Distant = nview.m_distant;
            zdo.Type = nview.m_type;
            zdo.m_prefab = prefabHash;

            if (blob != null)
            {
                blob.WriteTo(zdo);
                blob.ApplyHashConnection(zdo);
                if (blob.Persistent.HasValue) zdo.Persistent = blob.Persistent.Value;
                if (blob.Distant.HasValue) zdo.Distant = blob.Distant.Value;
                if (blob.Priority.HasValue) zdo.Type = blob.Priority.Value;
                dataApplied = true;
            }
            else if (!string.IsNullOrEmpty(extraInfo))
            {
                // Legacy PlanBuild blueprints carry sign/stand payloads in the
                // ExtraInfo column instead of a data blob (pre-IH format).
                ApplyLegacyExtraInfo(prefab, zdo, extraInfo);
            }

            zdo.m_rotation = rot.eulerAngles;
            zdo.DataRevision = 0U;
            zdo.IncreaseDataRevision();

            mutate?.Invoke(zdo);

            bool prevUseInit = ZNetView.m_useInitZDO;
            ZNetView.m_useInitZDO = true;
            ZNetView.m_initZDO = zdo;
            try
            {
                return UnityEngine.Object.Instantiate(prefab, pos, rot);
            }
            finally
            {
                if (ZNetView.m_initZDO != null)
                {
                    // Awake never claimed it (root ZNetView missing/disabled or
                    // Awake threw) — destroy so no orphan ZDO persists.
                    try { ZDOMan.instance.DestroyZDO(ZNetView.m_initZDO); } catch { }
                    ZNetView.m_initZDO = null;
                    dataApplied = false;
                }
                ZNetView.m_useInitZDO = prevUseInit;
            }
        }

        // Port of InfinityHammer ObjectSelection.ReadExtraInfo, writing the
        // decoded values straight onto the pre-Awake ZDO.
        private static void ApplyLegacyExtraInfo(GameObject prefab, ZDO zdo, string extraInfo)
        {
            try
            {
                if (extraInfo.Length >= 2 && extraInfo.StartsWith("\"", StringComparison.Ordinal)
                    && extraInfo.EndsWith("\"", StringComparison.Ordinal))
                {
                    extraInfo = System.Text.RegularExpressions.Regex.Unescape(
                        extraInfo.Substring(1, extraInfo.Length - 2));
                }

                if (prefab.GetComponent<global::Sign>() != null)
                    zdo.Set(ZDOVars.s_text, extraInfo);
                if (prefab.GetComponent<TeleportWorld>() != null)
                    zdo.Set(ZDOVars.s_tag, extraInfo);
                if (prefab.GetComponent<Tameable>() != null)
                    zdo.Set(ZDOVars.s_tamedName, extraInfo);
                if (prefab.GetComponent<ItemDrop>() != null && prefab.GetComponent<Fish>() == null)
                    zdo.Set(ZDOVars.s_piece, 1);

                if (prefab.GetComponent<ItemStand>() != null)
                {
                    var parts = extraInfo.Split(':');
                    if (parts[0].Length > 0)
                        zdo.Set(ZDOVars.s_item, parts[0].GetStableHashCode());
                    zdo.Set(ZDOVars.s_variant, IntAt(parts, 1, 0));
                    zdo.Set(ZDOVars.s_quality, IntAt(parts, 2, 1));
                    if (parts.Length > 3)
                        zdo.Set(ZDOVars.s_type, IntAt(parts, 3, 0));
                }

                var armorStand = prefab.GetComponent<ArmorStand>();
                if (armorStand != null)
                {
                    var parts = extraInfo.Split(':');
                    zdo.Set(ZDOVars.s_pose, IntAt(parts, 0, 0));
                    int slotCount = armorStand.m_slots != null ? armorStand.m_slots.Count : 0;
                    for (int i = 0; i < slotCount; i++)
                    {
                        string item = parts.Length > i * 2 + 2 ? parts[i * 2 + 2] : string.Empty;
                        if (item.Length == 0) continue;
                        zdo.Set($"{i}_item".GetStableHashCode(), item.GetStableHashCode());
                        zdo.Set($"{i}_variant".GetStableHashCode(), IntAt(parts, i * 2 + 3, 0));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAZdoFirstSpawner legacy ExtraInfo apply threw on '{prefab.name}': {ex.Message}");
            }
        }

        private static int IntAt(string[] parts, int index, int fallback)
        {
            if (parts == null || index < 0 || index >= parts.Length) return fallback;
            return int.TryParse(parts[index], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : fallback;
        }
    }
}
