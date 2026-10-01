using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Patches
{
    // Skips ClutterSystem.LateUpdate during world teardown so the natural clutter
    // regeneration loop doesn't Instantiate ZNetView'd prefabs and NRE inside
    // ZDOMan.CreateNewZDO when Game.instance has already been destroyed.
    //
    // The latch distinguishes "title screen, haven't entered a world yet — let
    // vanilla render menu background clutter" from "logged out mid-session, now
    // tearing down — suppress." It resets when a NEW ClutterSystem awakes: a new
    // scene is up (the post-logout menu background), teardown is over, and the
    // menu is back to the fresh-boot state. Without the reset the latch outlived
    // the teardown and the post-logout menu never generated any clutter.
    [HarmonyPatch(typeof(ClutterSystem), "LateUpdate")]
    public static class VAClutterSystemLogoutGuard
    {
        internal static bool _sessionWasAlive;

        [HarmonyPrefix]
        public static bool Prefix()
        {
            bool sessionAlive = Game.instance != null
                                && ZNet.instance != null
                                && ZDOMan.instance != null;

            if (sessionAlive)
            {
                _sessionWasAlive = true;
                return true;
            }
            if (!_sessionWasAlive) return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(ClutterSystem), "Awake")]
    public static class VAClutterSystemLogoutGuardReset
    {
        [HarmonyPostfix]
        public static void Postfix() => VAClutterSystemLogoutGuard._sessionWasAlive = false;
    }
}
