using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // 1.1.183 (Fire 10-07: "that's the entire purpose of our stand logic - we add all kinds of items to stands that aren't there
    // usually"): vanilla draws a stand's item from the item's "attach" child. Items never meant for stands have none, or an empty
    // one (BlobVial's is a bare Transform), so they hung invisibly. After vanilla's SetVisualItem, when it drew nothing, FAP
    // draws the item's own world model at the stand's attach point instead: a render-only copy (no colliders, rigidbody, network
    // or scripts), sitting on a shelf stand (itemstandh) and centred on a wall stand (itemstand). Every client running FAP draws
    // it; a vanilla client still sees the bare stand, which is why VanillaFriendly keeps such items off the Item Stands tab.
    [HarmonyPatch]
    public static class VAItemStandFallbackModel
    {
        private const string ModelName = "FAP_StandModel";
        private static readonly AccessTools.FieldRef<ItemStand, int> s_visualHash = AccessTools.FieldRefAccess<ItemStand, int>("m_visualHash");
        private static readonly AccessTools.FieldRef<ItemStand, GameObject> s_visualItem = AccessTools.FieldRefAccess<ItemStand, GameObject>("m_visualItem");
        private static readonly Dictionary<ItemStand, (int hash, GameObject model)> _models = new Dictionary<ItemStand, (int, GameObject)>();
        private static readonly List<Component> _strip = new List<Component>();
        private static int _calls;

        private static void Prune()
        {
            var dead = new List<ItemStand>();
            foreach (var kv in _models) if (kv.Key == null) dead.Add(kv.Key);
            foreach (var k in dead) { if (_models[k].model != null) Object.Destroy(_models[k].model); _models.Remove(k); }
        }

        // True when the item has something drawable outside its "attach" child (what this fallback copies).
        public static bool HasWorldModel(GameObject itemPrefab) => WorldModelSources(itemPrefab).Count > 0;

        // 1.1.204 (Fire 10-09: Fallen Warrior gear - FW_KnifeSkollAndHati, FW_Armor*, capes - placed EMPTY stands): NPC equipment
        // keeps its "attach" model NESTED in the wearer's skinned rig (attach_skin / Armature / … / attach), so vanilla (a direct
        // child only) draws nothing, and the old fallback copied the root's other children - a skinned mesh and an inactive rig,
        // which render nothing once copied off the NPC. What draws on a stand, in order:
        //   1. a nested "attach" with a plain (not skinned) renderer: its children, as vanilla would draw a direct one;
        //   2. else the root's ACTIVE children (not "attach") that hold a plain renderer.
        // Nothing found = the item is not offered as a stand-in at all (VAItemStandRegistry skips it).
        private static readonly List<Transform> _sources = new List<Transform>();
        internal static List<Transform> WorldModelSources(GameObject itemPrefab)
        {
            _sources.Clear();
            if (itemPrefab == null) return _sources;
            Transform nested = FindNestedAttach(itemPrefab.transform);
            if (nested != null && HasPlainRenderer(nested))
            {
                foreach (Transform c in nested) if (HasPlainRenderer(c)) _sources.Add(c);
                if (_sources.Count > 0) return _sources;
            }
            foreach (Transform child in itemPrefab.transform)
                if (child.name != "attach" && child.gameObject.activeSelf && HasPlainRenderer(child)) _sources.Add(child);
            return _sources;
        }

        private static Transform FindNestedAttach(Transform root)
        {
            foreach (Transform c in root)
            {
                if (c.name == "attach") continue;   // a direct one is vanilla's own (drawn there, or empty on purpose)
                var hit = FindDeep(c, "attach");
                if (hit != null) return hit;
            }
            return null;
        }

        private static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t) { var hit = FindDeep(c, name); if (hit != null) return hit; }
            return null;
        }

        private static bool HasPlainRenderer(Transform t)
        {
            foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                if (!(r is SkinnedMeshRenderer) && !(r is ParticleSystemRenderer)) return true;
            return false;
        }

        [HarmonyPatch(typeof(ItemStand), "SetVisualItem"), HarmonyPostfix]
        private static void ItemStand_SetVisualItem_Postfix(ItemStand __instance, int itemHash)
        {
            // 1.1.185: vanilla ItemStand has no OnDestroy to hook (1.1.184 patched one and FAP failed to load); destroyed stands
            // are dropped from the table here instead, now and then.
            if (++_calls % 256 == 0) Prune();
            try
            {
                _models.TryGetValue(__instance, out var current);
                bool vanillaDrew = s_visualItem(__instance) != null && s_visualItem(__instance).GetComponentInChildren<Renderer>(true) != null;
                if (itemHash == 0 || vanillaDrew) { Clear(__instance, current.model); return; }
                if (current.model != null && current.hash == itemHash) return;   // already drawn (UpdateVisual calls this every 4 s)
                Clear(__instance, current.model);

                GameObject itemPrefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(itemHash) : null;
                if (!HasWorldModel(itemPrefab)) return;
                Transform attach = __instance.GetAttach(null);
                if (attach == null) return;

                var model = new GameObject(ModelName);
                model.transform.SetParent(attach, false);
                foreach (Transform source in WorldModelSources(itemPrefab).ToArray())
                {
                    var copy = Object.Instantiate(source.gameObject, model.transform, false);
                    StripToRender(copy);
                }
                PlaceOnStand(__instance, model);

                var drop = itemPrefab.GetComponent<ItemDrop>();
                if (drop != null) __instance.m_currentItemName = drop.m_itemData.m_shared.m_name;   // hover text / take
                _models[__instance] = (itemHash, model);
                if (VerdantsAscent.Managers.ConfigManager.Instance.configVerboseLogging?.Value == true)
                    Debug.Log($"{VaPieces.PluginName}: item stand {__instance.name}: drew {itemPrefab.name}'s world model ({model.GetComponentsInChildren<Renderer>(true).Length} renderer(s)) - it has no stand model of its own");
            }
            catch (System.Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: item stand fallback model: {ex.Message}"); }
        }

        private static void Clear(ItemStand stand, GameObject model)
        {
            if (model != null) Object.Destroy(model);
            _models.Remove(stand);
        }

        // Only what draws stays: no physics, no network, no item / game scripts on the copy.
        private static void StripToRender(GameObject copy)
        {
            _strip.Clear();
            copy.GetComponentsInChildren(true, _strip);
            foreach (var c in _strip)
            {
                if (c == null || c is Transform || c is Renderer || c is MeshFilter || c is LODGroup || c is ParticleSystem) continue;
                if (c is Collider || c is Rigidbody || c is Joint || c is MonoBehaviour) Object.DestroyImmediate(c);
            }
            foreach (var r in copy.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
            copy.SetActive(true);
        }

        // Shelf (itemstandh): the model's bottom on the attach point. Wall (itemstand): its centre on the attach point.
        private static void PlaceOnStand(ItemStand stand, GameObject model)
        {
            var renderers = model.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return;
            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            Vector3 anchor = model.transform.parent.position;
            bool shelf = (stand.m_orientationType & ItemStand.Orientation.Horizontal) != 0;
            Vector3 target = shelf ? new Vector3(b.center.x, b.min.y, b.center.z) : b.center;
            model.transform.position += anchor - target;
        }
    }
}
