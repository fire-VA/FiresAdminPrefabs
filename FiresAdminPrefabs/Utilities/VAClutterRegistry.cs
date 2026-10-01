using System;
using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // Name → ClutterSystem.Clutter, read straight from ClutterSystem.instance.m_clutter
    // with no filtering and no mutation of the vanilla entries. The cache is keyed to
    // the ClutterSystem instance, so a mod that rebuilds m_clutter mid-session (Expand
    // World Data, Better Continents) triggers a rebuild on the next access.
    //
    // It's the source of truth for what can be painted: the stand-in factory mints tabs
    // from it, the paint patch resolves stand-ins back through it, and RestoreFromZDO
    // finds prefabs through it when replaying saved entries.
    public static class VAClutterRegistry
    {
        private static readonly Dictionary<string, ClutterSystem.Clutter> _byName
            = new Dictionary<string, ClutterSystem.Clutter>(StringComparer.Ordinal);
        private static int _sourceId = -1;

        public static IReadOnlyDictionary<string, ClutterSystem.Clutter> All
        {
            get { EnsureFresh(); return _byName; }
        }

        public static int Count
        {
            get { EnsureFresh(); return _byName.Count; }
        }

        public static bool TryGet(string name, out ClutterSystem.Clutter data)
        {
            EnsureFresh();
            return _byName.TryGetValue(name, out data);
        }

        public static GameObject ResolvePrefab(string name)
        {
            return TryGet(name, out var d) ? d?.m_prefab : null;
        }

        // Force rebuild on next access. Called when bundle clutter is
        // injected into m_clutter late (VAExternalBundleLoader's
        // RegisterClutterFromManifests runs in two postfixes) or when
        // VAVanillaPieceManager.InvalidateClutterCache fires.
        public static void Invalidate()
        {
            _sourceId = -1;
            _byName.Clear();
        }

        private static void EnsureFresh()
        {
            var cs = ClutterSystem.instance;
            if (cs == null || cs.m_clutter == null) return;

            // RPGmaker-pattern: re-inject any of our tracked clutter that
            // EWD or another m_clutter-rebuilding mod wiped between our
            // initial drain and now. Runs every call (cheap — name-set
            // intersection over ~20-200 entries); when something was
            // re-injected, force a cache rebuild below by also bumping
            // _sourceId past the equality check.
            int reInjected = VAExternalBundleLoader.EnsureRegistered();

            int id = cs.GetInstanceID();
            if (id == _sourceId && _byName.Count > 0 && reInjected == 0) return;

            _sourceId = id;
            _byName.Clear();
            foreach (var c in cs.m_clutter)
            {
                if (c?.m_prefab == null) continue;
                var n = c.m_prefab.name;
                if (string.IsNullOrEmpty(n)) continue;
                if (!_byName.ContainsKey(n)) _byName[n] = c;

                // Bundle clutter shader repair — swap broken bundle
                // shaders to a runtime-compatible vanilla grass shader
                // so painted instances render with proper alpha cutout +
                // wave animation. Vanilla clutter is skipped inside
                // the helper (its shaders are already in Valheim's
                // runtime cache). Idempotent per-prefab.
                VAClutterShaderRepair.RepairIfBundleClutter(c.m_prefab);
            }
        }
    }
}
