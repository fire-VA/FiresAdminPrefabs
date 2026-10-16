using System;

namespace VerdantsAscent.Utilities
{
    // 1.1.266 (Fire 10-15: no HarmonyX "Could not find" warnings): AccessTools.TypeByName warns on every miss, and many of FAP's
    // lookups are optional mods (Infinity Hammer, Jotunn, FDM) or version-dependent types. Finds a type by full name
    // ("Ns.Outer+Inner" works) across the loaded assemblies without logging; callers keep their own null handling.
    internal static class QuietTypes
    {
        internal static Type Find(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(fullName, false); if (t != null) return t; } catch { }
            }
            return null;
        }
    }
}
