using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Written hints for the fields admins touch most, overridable per server. Anything without an entry still
    // gets a hint, built from the field's type, the prefab's value and any Range or Tooltip attribute
    // (VAExtraFieldCatalogue). File: config/FiresAdminPrefabs/field_hints.yaml, one "Component.field: text" per
    // line. Plan: Docs/SPAWN_WITH_EXTRA_DATA.md
    internal static class VAExtraFieldHints
    {
        private const string FileName = "field_hints.yaml";
        private const char KeyValueSeparator = ':';
        private const string CommentPrefix = "#";

        private static readonly Dictionary<string, string> BuiltIn = new Dictionary<string, string>
        {
            { "Character.m_health", "Max health. Applied when the creature sets up its health, so it takes effect on spawn." },
            { "Character.m_faction", "Faction number; enums cannot be set per object, so this is the underlying int." },
            { "Character.m_crouchSpeed", "Movement speed while crouching, metres per second." },
            { "Character.m_walkSpeed", "Walk speed, metres per second." },
            { "Character.m_runSpeed", "Run speed, metres per second." },
            { "Character.m_jumpForce", "Upward force of a jump." },
            { "Humanoid.m_name", "The name shown on the health bar and in messages." },
            { "MonsterAI.m_viewRange", "How far it can see a target, in metres." },
            { "MonsterAI.m_hearRange", "How far it can hear a target, in metres." },
            { "MonsterAI.m_alertRange", "Distance at which it becomes alerted." },
            { "MonsterAI.m_fleeRange", "Distance it keeps when fleeing." },
            { "MonsterAI.m_attackPlayerObjects", "Whether it attacks player-built pieces." },
            { "BaseAI.m_viewRange", "How far it can see a target, in metres." },
            { "Tameable.m_tamingTime", "Seconds of taming required." },
            { "Tameable.m_commandable", "Whether the tamed creature can be told to follow and stay." },
            { "Procreation.m_totalCheckRange", "Radius searched for a mate." },
            { "Procreation.m_maxCreatures", "Population cap inside that radius." },
            { "Procreation.m_pregnancyChance", "Chance per check that a pregnancy starts." },
            { "Piece.m_comfort", "Comfort value this piece contributes to Rested." },
            { "WearNTear.m_health", "Structural health of the piece." },
            { "WearNTear.m_noRoofWear", "True stops rain damaging it without a roof." },
            { "WearNTear.m_noSupportWear", "True stops it needing structural support." },
            { "Container.m_width", "Inventory columns." },
            { "Container.m_height", "Inventory rows." },
            { "Container.m_autoDestroyEmpty", "Destroys the container once it is emptied." },
            { "Door.m_canNotBeClosed", "True leaves the door permanently open." },
            { "Fireplace.m_secPerFuel", "Seconds each fuel item burns for." },
            { "Fireplace.m_maxFuel", "Maximum fuel items it holds." },
            { "Smelter.m_secPerProduct", "Seconds per output item." },
            { "Smelter.m_maxOre", "Maximum queued ore." },
            { "Smelter.m_maxFuel", "Maximum queued fuel." },
            { "Fermenter.m_fermentationDuration", "Seconds to ferment a batch." },
            { "CookingStation.m_cookTime", "Seconds to cook an item." },
            { "CraftingStation.m_rangeBuild", "Build radius the station provides." },
            { "Pickable.m_respawnTimeMinutes", "Minutes before it can be picked again; 0 never respawns." },
            { "Destructible.m_health", "Hit points before it breaks." },
            { "MineRock.m_health", "Hit points of each minable area." },
            { "TreeBase.m_health", "Hit points of the trunk." },
            { "Ship.m_sailForce", "Sail force; higher is faster." },
            { "Beehive.m_secPerUnit", "Seconds per honey produced." },
            { "Turret.m_attackCooldown", "Seconds between shots." },
        };

        private static Dictionary<string, string> _userHints;

        public static string For(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            EnsureUserHints();
            if (_userHints.TryGetValue(key, out string user)) return user;
            return BuiltIn.TryGetValue(key, out string built) ? built : null;
        }

        private static void EnsureUserHints()
        {
            if (_userHints != null) return;
            _userHints = new Dictionary<string, string>();

            string path = Path.Combine(BepInEx.Paths.ConfigPath, VaPieces.PluginName, FileName);
            if (!File.Exists(path)) return;
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(CommentPrefix, StringComparison.Ordinal)) continue;
                    int separator = line.IndexOf(KeyValueSeparator);
                    if (separator <= 0) continue;
                    string key = line.Substring(0, separator).Trim();
                    string text = line.Substring(separator + 1).Trim().Trim('"');
                    if (key.Length > 0 && text.Length > 0) _userHints[key] = text;
                }
                Debug.Log($"{VaPieces.PluginName}: loaded {_userHints.Count} field hint override(s) from {path}.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: could not read {path}: {ex.Message}");
            }
        }
    }
}
