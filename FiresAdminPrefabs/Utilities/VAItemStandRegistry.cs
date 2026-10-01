using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // Enumerates every ItemDrop in ObjectDB that can visually attach to a
    // vanilla ItemStand (i.e. has an "attach" child transform — the same
    // path vanilla's ItemStand.SetVisualItem walks). The vanilla-friendly
    // config flips the filter to exclude items loaded from FAP external
    // bundles so vanilla clients don't see empty stands on placement.
    public static class VAItemStandRegistry
    {
        public sealed class Entry
        {
            public string Name;
            public GameObject Prefab;
            public ItemDrop ItemDrop;
            public Sprite Icon;

            // Whether the vanilla stand for that orientation would let a player attach this
            // item itself. FAP writes the attachment through the ZDO and can hold anything,
            // so this only gates what vanilla-friendly mode offers.
            public bool VanillaAcceptsVertical;
            public bool VanillaAcceptsHorizontal;
        }

        private static readonly List<Entry> _entries = new List<Entry>();
        private static int _sourceId = -1;
        private static bool _lastVanillaFriendly;

        public static IReadOnlyList<Entry> All
        {
            get { EnsureFresh(); return _entries; }
        }

        public static int Count
        {
            get { EnsureFresh(); return _entries.Count; }
        }

        public static Entry Resolve(string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return null;
            EnsureFresh();
            foreach (var e in _entries)
            {
                if (e.Name == itemName) return e;
            }
            return null;
        }

        public static void Invalidate()
        {
            _sourceId = -1;
            _entries.Clear();
        }

        private static void EnsureFresh()
        {
            var odb = ObjectDB.instance;
            if (odb == null || odb.m_items == null) return;

            var cfg = ConfigManager.Instance;
            bool vanillaFriendly = cfg?.configVanillaFriendly?.Value ?? false;
            int id = odb.GetInstanceID();
            if (id == _sourceId
                && vanillaFriendly == _lastVanillaFriendly
                && _entries.Count > 0)
            {
                return;
            }
            _sourceId = id;
            _lastVanillaFriendly = vanillaFriendly;
            _entries.Clear();

            var verticalStand = ResolveVanillaStand(VAItemStandStandinFactory.VanillaVerticalPrefabName);
            var horizontalStand = ResolveVanillaStand(VAItemStandStandinFactory.VanillaHorizontalPrefabName);

            foreach (var itemPrefab in odb.m_items)
            {
                if (itemPrefab == null) continue;
                var drop = itemPrefab.GetComponent<ItemDrop>();
                if (drop == null) continue;
                if (drop.m_itemData?.m_shared == null) continue;

                if (vanillaFriendly
                    && VAExternalBundleLoader.TryGetSourceBundle(itemPrefab.name, out _))
                {
                    continue;
                }

                if (itemPrefab.transform.Find("attach") == null) continue;

                // An inventory icon is what separates a real item from the internal ones that
                // only exist on a creature's hand — monster weapons, dummy drops. Those have no
                // icon to show and no business on a stand, and they rendered as a bare stand.
                Sprite icon = ResolveIcon(drop.m_itemData.m_shared);
                if (icon == null) continue;

                _entries.Add(new Entry
                {
                    Name = itemPrefab.name,
                    Prefab = itemPrefab,
                    ItemDrop = drop,
                    Icon = icon,
                    VanillaAcceptsVertical = StandAccepts(verticalStand, drop.m_itemData),
                    VanillaAcceptsHorizontal = StandAccepts(horizontalStand, drop.m_itemData),
                });
            }
        }

        // Variant items (capes, shields, armour) keep one sprite per variant and can leave the
        // first slot empty, so the first non-null entry is the icon, not index 0.
        private static Sprite ResolveIcon(ItemDrop.ItemData.SharedData shared)
        {
            var icons = shared?.m_icons;
            if (icons == null) return null;
            for (int i = 0; i < icons.Length; i++)
            {
                if (icons[i] != null) return icons[i];
            }
            return null;
        }

        private static ItemStand ResolveVanillaStand(string prefabName)
        {
            var scene = ZNetScene.instance;
            if (scene == null) return null;
            var prefab = scene.GetPrefab(prefabName);
            return prefab != null ? prefab.GetComponent<ItemStand>() : null;
        }

        // Vanilla ItemStand.CanAttach, minus its drop-prefab lookup: that resolves the "attach"
        // child this registry already requires. IsSupported and IsUnsupported are vanilla's own.
        private static bool StandAccepts(ItemStand stand, ItemDrop.ItemData item)
        {
            if (stand == null || item?.m_shared == null) return false;
            if (stand.IsUnsupported(item)) return false;
            return stand.IsSupported(item) || stand.m_supportedTypes.Contains(item.m_shared.m_itemType);
        }
    }
}
