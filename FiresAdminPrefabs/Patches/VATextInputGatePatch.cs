using HarmonyLib;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // Reports FAP's preset config UIs as an open text input. Vanilla's chat, hotbar
    // and every other system that respects TextInput.IsVisible then stay quiet
    // without a patch each — otherwise Enter in a preset textbox also opened chat.
    [HarmonyPatch(typeof(TextInput), nameof(TextInput.IsVisible))]
    public static class TextInput_IsVisible_VAConfigUI_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result)
        {
            if (__result) return;
            if (VAMonsterConfigUI.IsOpen || VAChestConfigUI.IsOpen)
                __result = true;
        }
    }
}
