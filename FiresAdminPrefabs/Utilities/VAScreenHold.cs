using System.Collections.Generic;

namespace VerdantsAscent.Utilities
{
    // 1.1.301 (Fire 10-21: "neither the edit sound or edit light ui's properly lock the camera and unlock the mouse. It should be as
    // if we were still holding shift alt when the ui is open until we close it"): the hold-Shift+Alt context menu does that through
    // Core's InputBlock.Block(true) (free cursor + pinned camera, Core UI/InputBlock.cs). The editors it opens take the same block
    // and keep it while open: Hold() every frame re-takes it if the context menu released it on its own close, Release() gives it
    // back only when FAP took it and no FAP editor is still open. Nothing here depends on the Unified override.
    internal static class VAScreenHold
    {
        private static readonly HashSet<object> s_owners = new HashSet<object>();
        private static bool s_ours;

        internal static void Hold(object owner)
        {
            if (owner == null) return;
            s_owners.Add(owner);
            if (FiresCore.UI.InputBlock.IsBlocked) return;
            FiresCore.UI.InputBlock.Block(true);
            s_ours = true;
        }

        internal static void Release(object owner)
        {
            if (owner == null || !s_owners.Remove(owner) || s_owners.Count > 0) return;
            if (s_ours && FiresCore.UI.InputBlock.IsBlocked) FiresCore.UI.InputBlock.Block(false);
            s_ours = false;
        }
    }
}
