using VerdantsAscent.Managers;
using VerdantsAscent.Patches;

namespace VerdantsAscent.Utilities
{
    // Single source of truth for whether FAP should defer persistence and respawn
    // to ExpandWorldPrefabs. Use IsActive to gate every FAP standalone path that
    // EWP could double-handle.
    public static class VAEwpSoftDep
    {
        public static bool IsActive
        {
            get
            {
                if (!VAEwpDetector.IsEwpInstalled) return false;
                var cfg = ConfigManager.Instance;
                if (cfg == null || cfg.configUseEwpForPersistence == null) return false;
                return cfg.configUseEwpForPersistence.Value;
            }
        }
    }
}
