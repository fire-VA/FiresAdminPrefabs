using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Utilities;
using static VerdantsAscent.Patches.TimedDestructionPatchHelpers;

namespace VerdantsAscent.Patches
{
    // Placement-time application of the global hammer toggles (clip ground / clip
    // everything / disable static physics / force persistence / effect TTL) to
    // pieces FAP injected into the hammer. Vanilla pieces keep their author flags.
    //
    // Clip flags are synced onto the ghost every frame; the persistent toggles are
    // written once in Piece.SetCreator, after the instance's ZNetView is valid.

    [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
    public static class Player_UpdatePlacementGhost_HammerToggle_Patch
    {
        private static readonly AccessTools.FieldRef<Player, GameObject> _ghostRef =
            AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");

        [HarmonyPrefix]
        public static void Prefix(Player __instance)
        {
            var ghost = _ghostRef(__instance);
            if (ghost == null) return;

            var piece = ghost.GetComponent<Piece>();
            if (piece == null) return;

            if (!VAVanillaPieceManager.WasInjectedByUs(ghost)) return;

            var config = ConfigManager.Instance;
            piece.m_clipGround = config.configHammerForceClipGround.Value;
            piece.m_clipEverything = config.configHammerForceClipEverything.Value;
        }
    }

    [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
    public static class Piece_SetCreator_HammerToggle_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Piece __instance)
        {
            if (__instance == null) return;

            // WasInjectedByUs is name-based and also matches an Infinity Hammer placement
            // of a FAP-injected prefab; IH owns those.
            if (VAInfinityHammerCompat.IsIhCopyPlacing) return;   // 1.1.206: only an IH copy, not an IH-held hammer
            if (!VAVanillaPieceManager.WasInjectedByUs(__instance.gameObject)) return;

            var config = ConfigManager.Instance;
            var netView = __instance.GetComponent<ZNetView>();
            bool ownsValidNetView = netView != null && netView.IsValid() && netView.IsOwner();

            if (netView != null && config.configHammerForcePersistence.Value && !netView.m_persistent)
                netView.m_persistent = true;

            bool staticPhysicsSuppressed = config.configHammerDisableStaticPhysics.Value
                && !VAInfinityHammerCompat.IsInfinityHammerLoaded
                && ownsValidNetView;
            if (staticPhysicsSuppressed)
                SuppressStaticPhysics(__instance.gameObject, netView);

            bool appliedTtl = ownsValidNetView && TryApplyEffectTtlOverride(__instance, netView, config);

            if (ownsValidNetView)
                VAFapEwpYamlWriter.RegisterPlacement(__instance.gameObject);

            if (config.configVerboseLogging.Value && netView != null)
            {
                var syncTransform = __instance.gameObject.GetComponent<ZSyncTransform>();
                Debug.Log(
                    $"{VaPieces.PluginName}: SetCreator postfix on '{__instance.gameObject.name}' " +
                    $"(creator={__instance.GetCreator()}) — " +
                    $"persistent={netView.m_persistent}, " +
                    $"syncScale={(syncTransform != null && syncTransform.m_syncScale)}, " +
                    $"syncInitialScale={netView.m_syncInitialScale}, " +
                    $"staticPhysDisabled={staticPhysicsSuppressed}, " +
                    $"ttlOverride={appliedTtl}");
            }
        }

        // Mirrors Infinity Hammer's NoCreator. The HasFields gate keys are required:
        // without them ZNetView.LoadFields never applies the per-field overrides on
        // zone reload and the piece ground-snaps again. Skipped entirely when IH is
        // loaded, since IH performs this same suppression itself.
        private static void SuppressStaticPhysics(GameObject root, ZNetView netView)
        {
            var staticPhysics = root.GetComponent<StaticPhysics>();
            bool needsSuppression = staticPhysics != null && (staticPhysics.m_fall || staticPhysics.m_pushUp);

            if (needsSuppression)
            {
                var zdo = netView.GetZDO();
                if (zdo != null)
                {
                    zdo.Set(VAStaticPhysicsKeys.HasFieldsKey, true);
                    zdo.Set(VAStaticPhysicsKeys.HasFieldsStaticPhysicsKey, true);

                    if (staticPhysics.m_fall)
                    {
                        staticPhysics.m_fall = false;
                        zdo.Set(VAStaticPhysicsKeys.VanillaFallKey, false);
                    }
                    if (staticPhysics.m_pushUp)
                    {
                        staticPhysics.m_pushUp = false;
                        zdo.Set(VAStaticPhysicsKeys.VanillaPushUpKey, false);
                    }
                }

                try { netView.LoadFields(); }
                catch (System.Exception ex)
                {
                    Debug.LogWarning(
                        $"{VaPieces.PluginName}: HammerTogglePatches.StaticPhysics LoadFields rerun threw: {ex.Message}");
                }
            }

            DisableStaticPhysicsTree(root);
        }

        // Only an FX/VFX-tab pick gets the loop-forever flag, and only while FAP (not
        // EWP) is the active persistence backend — otherwise the two stack and the
        // effect replays on top of itself.
        private static bool TryApplyEffectTtlOverride(Piece piece, ZNetView netView, ConfigManager config)
        {
            if (config.configVanillaFriendly.Value) return false;
            if (!config.configHammerEffectsOverrideTTL.Value) return false;
            if (VAEwpSoftDep.IsActive) return false;
            if (!IsInFxOrVfxCategory(piece)) return false;
            if (!VAEffectTtlOverride.HasTimedComponents(piece.gameObject)) return false;

            var zdo = netView.GetZDO();
            if (zdo != null)
                zdo.Set(VAEffectTtlKeys.OverrideHash, true);

            VAEffectTtlOverride.Apply(piece.gameObject);
            return true;
        }

        private static bool IsInFxOrVfxCategory(Piece piece)
        {
            if (piece == null) return false;
            var category = piece.m_category;
            if (VACategoryRegistry.TryGet(VACategoryRegistry.CatFX, out var fxCategory) && category == fxCategory)
                return true;
            if (VACategoryRegistry.TryGet(VACategoryRegistry.CatVFX, out var vfxCategory) && category == vfxCategory)
                return true;
            return false;
        }

        private static void DisableStaticPhysicsTree(GameObject root)
        {
            var components = root.GetComponentsInChildren<StaticPhysics>(includeInactive: true);
            for (int i = 0; i < components.Length; i++)
            {
                var staticPhysics = components[i];
                if (staticPhysics == null) continue;
                staticPhysics.m_fall = false;
                staticPhysics.m_pushUp = false;
                staticPhysics.enabled = false;
            }
        }
    }

    // Vanilla ZDO keys in ZNetView.LoadFields' "<Component>.<field>" form, so the
    // engine itself restores the overrides on every Awake. Same pair Infinity
    // Hammer's NoCreator writes.
    internal static class VAStaticPhysicsKeys
    {
        public static readonly int VanillaFallKey = "StaticPhysics.m_fall".GetStableHashCode();
        public static readonly int VanillaPushUpKey = "StaticPhysics.m_pushUp".GetStableHashCode();
        public static readonly int HasFieldsKey = "HasFields".GetStableHashCode();
        public static readonly int HasFieldsStaticPhysicsKey = "HasFieldsStaticPhysics".GetStableHashCode();
    }

    internal static class VAEffectTtlKeys
    {
        public static readonly int OverrideHash = "vap_effect_no_ttl".GetStableHashCode();
    }

    // Turns a one-shot effect into a perpetual one: cancels TimedDestruction, loops
    // every ParticleSystem and AudioSource in the subtree (inactive ones included,
    // since compound effects enable sub-emitters late).
    internal static class VAEffectTtlOverride
    {
        public static bool HasTimedComponents(GameObject root)
        {
            if (root == null) return false;
            if (root.GetComponentInChildren<TimedDestruction>(includeInactive: true) != null)
                return true;

            var particleSystems = root.GetComponentsInChildren<ParticleSystem>(includeInactive: true);
            for (int i = 0; i < particleSystems.Length; i++)
            {
                var particleSystem = particleSystems[i];
                if (particleSystem == null) continue;
                var main = particleSystem.main;
                if (!main.loop && main.duration > 0f) return true;
            }
            return false;
        }

        public static void Apply(GameObject root)
        {
            if (root == null) return;

            int timedCount = 0;
            var timedDestructions = root.GetComponentsInChildren<TimedDestruction>(includeInactive: true);
            for (int i = 0; i < timedDestructions.Length; i++)
            {
                var timed = timedDestructions[i];
                if (timed == null) continue;
                timed.CancelInvoke();
                timed.enabled = false;
                timedCount++;
            }

            int particleCount = 0;
            var particleSystems = root.GetComponentsInChildren<ParticleSystem>(includeInactive: true);
            for (int i = 0; i < particleSystems.Length; i++)
            {
                var particleSystem = particleSystems[i];
                if (particleSystem == null) continue;
                var main = particleSystem.main;
                main.loop = true;
                main.stopAction = ParticleSystemStopAction.None;
                particleCount++;
            }

            int audioCount = 0;
            var audioSources = root.GetComponentsInChildren<AudioSource>(includeInactive: true);
            for (int i = 0; i < audioSources.Length; i++)
            {
                var audio = audioSources[i];
                if (audio == null) continue;
                audio.loop = true;
                audioCount++;
            }

            if (ConfigManager.Instance.configVerboseLogging.Value)
                Debug.Log($"{VaPieces.PluginName}: VAEffectTtlOverride.Apply on '{root.name}' — TimedDestruction cancelled x{timedCount}, ParticleSystem looped x{particleCount}, AudioSource looped x{audioCount}");
        }
    }

    // TimedDestruction re-schedules its destroy loop from Awake on every reload, so
    // the flag is checked at Trigger, before the schedule exists. Both overloads.
    [HarmonyPatch(typeof(TimedDestruction), nameof(TimedDestruction.Trigger), new System.Type[0])]
    public static class TimedDestruction_Trigger_VAOverride_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(TimedDestruction __instance)
            => !ShouldSkipTrigger(__instance);
    }

    [HarmonyPatch(typeof(TimedDestruction), nameof(TimedDestruction.Trigger), new System.Type[] { typeof(float) })]
    public static class TimedDestruction_TriggerFloat_VAOverride_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(TimedDestruction __instance)
            => !ShouldSkipTrigger(__instance);
    }

    internal static class TimedDestructionPatchHelpers
    {
        // When EWP is active it owns the destroy→respawn loop, and a flagged effect
        // must still be destroyed normally so EWP can respawn it.
        public static bool ShouldSkipTrigger(TimedDestruction timed)
        {
            if (timed == null) return false;
            if (!ConfigManager.Instance.configHammerEffectsOverrideTTL.Value) return false;
            if (VAEwpSoftDep.IsActive) return false;
            var netView = timed.GetComponent<ZNetView>();
            if (netView == null || !netView.IsValid()) return false;
            var zdo = netView.GetZDO();
            if (zdo == null) return false;
            return zdo.GetBool(VAEffectTtlKeys.OverrideHash, false);
        }
    }

    // ParticleSystem/AudioSource looping lives on instance components and reverts
    // on every Instantiate, so it is re-applied on each Awake of a flagged piece.
    [HarmonyPatch(typeof(Piece), "Awake")]
    public static class Piece_Awake_VAEffectTtl_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Piece __instance)
        {
            if (__instance == null) return;
            if (!ConfigManager.Instance.configHammerEffectsOverrideTTL.Value) return;
            if (VAEwpSoftDep.IsActive) return;
            var netView = __instance.GetComponent<ZNetView>();
            if (netView == null || !netView.IsValid()) return;
            var zdo = netView.GetZDO();
            if (zdo == null) return;
            if (!zdo.GetBool(VAEffectTtlKeys.OverrideHash, false)) return;
            VAEffectTtlOverride.Apply(__instance.gameObject);
        }
    }

    // The template-time fix in VAVanillaPieceManager can't reach Fireplace
    // instances already alive in the world; this covers those.
    [HarmonyPatch(typeof(Fireplace), "Awake")]
    public static class Fireplace_Awake_VANullGuard_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Fireplace __instance)
        {
            if (__instance == null) return;
            string ownerName = __instance.gameObject != null ? __instance.gameObject.name : "<unknown>";
            VAVanillaPieceManager.NeutralizeBrokenFireplaceInstance(__instance, ownerName);
        }
    }
}
