using System;
using System.Reflection;

namespace VerdantsAscent.Utilities
{
    // Reads InfinityHammer's "override health" and "make invulnerable"
    // config knobs via reflection so blueprint placement can mirror IH's
    // overrides without a hard assembly reference. Returns null when IH
    // isn't installed (or the resolved knob isn't active) so the caller
    // can fall back to vanilla defaults.
    //
    // Precedence: Invulnerability mode > Indestructible flag > OverwriteHealth > vanilla.
    //
    // Infinity Hammer exposes invulnerability as a string mode, not a flag. Read from its
    // CustomHealth.SetInfiniteHealth IL: for a structure it sets health to -1 in every mode and
    // picks the LOOK through a per-piece max-health override written with vanilla's field system
    // (HasFields + HasFieldsWearNTear + "WearNTear.m_health"): On -1/-1, Worn -1/-2, Damaged -1/-4.
    // ZNetView.Awake applies that override to the live WearNTear, so -1/-2 reads 0.5 (worn) and
    // -1/-4 reads 0.25 (broken) on every client, with or without Infinity Hammer. Legacy writes
    // 1E30 as plain health. The earlier lookup only knew a bool flag and "OverrideHealth", so it
    // resolved neither knob and blueprint placement always fell back to vanilla health.
    public static class VAInfinityHammerCompat
    {
        private const float IndestructibleHealth = -1f;
        private const float IntactLookMaxHealth = -1f;
        private const float WornLookMaxHealth = -2f;
        private const float DamagedLookMaxHealth = -4f;
        private const float LegacyHealth = 1e30f;

        private static readonly int FieldsKey = ZNetView.CustomFieldsStr.GetStableHashCode();
        private static readonly int WearNTearFieldsKey = (ZNetView.CustomFieldsStr + nameof(WearNTear)).GetStableHashCode();
        private static readonly int WearNTearMaxHealthKey = (nameof(WearNTear) + "." + nameof(WearNTear.m_health)).GetStableHashCode();

        private static readonly string[] IhAssemblyNames =
        {
            "InfinityHammer",
            "Azumatt.InfinityHammer",
        };

        private static readonly string[] IhConfigTypeNames =
        {
            "InfinityHammer.Configuration",
            "InfinityHammer.Config",
            "InfinityHammer.Settings",
        };

        private static readonly string[] HealthMemberNames =
        {
            "OverwriteHealth",
            "OverrideHealth",
            "configOverrideHealth",
            "Health",
        };

        private static readonly string[] InvulnerabilityModeMemberNames =
        {
            "Invulnerability",
        };

        private static readonly string[] IndestructibleMemberNames =
        {
            "MakeIndestructible",
            "Indestructible",
            "Invulnerable",
            "configIndestructible",
            "configMakeIndestructible",
        };

        private static bool s_resolved;
        private static bool s_ihPresent;
        private static Func<float>  s_overrideHealthGetter;
        private static Func<bool>   s_indestructibleGetter;
        private static Func<string> s_invulnerabilityModeGetter;

        public static bool IsInfinityHammerLoaded
        {
            get { EnsureResolved(); return s_ihPresent; }
        }

        // The health a placement will carry, for the placement banner. Every invulnerable mode is -1;
        // the look it picks rides separately in the max-health override ApplyHealthOverride writes.
        public static float? GetEffectiveHealth()
        {
            EnsureResolved();
            if (!s_ihPresent) return null;

            string mode = ReadInvulnerabilityMode();
            if (LookMaxHealthForInvulnerabilityMode(mode).HasValue) return IndestructibleHealth;
            if (IsLegacyMode(mode)) return LegacyHealth;

            try
            {
                if (s_indestructibleGetter != null && s_indestructibleGetter())
                    return IndestructibleHealth;
            }
            catch { }

            try
            {
                if (s_overrideHealthGetter != null)
                {
                    float v = s_overrideHealthGetter();
                    if (v > 0f && !float.IsNaN(v) && !float.IsInfinity(v))
                        return v;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Writes Infinity Hammer's health override onto a placement's ZDO before the piece is created, in IH's own
        /// layout, so vanilla's field pass in ZNetView.Awake gives the live piece the look IH would. False when there is
        /// nothing to write and the prefab keeps its own health.
        /// </summary>
        public static bool ApplyHealthOverride(ZDO zdo, bool isStructure)
        {
            if (zdo == null) return false;
            EnsureResolved();
            if (!s_ihPresent) return false;

            float? lookMaxHealth = LookMaxHealthForInvulnerabilityMode(ReadInvulnerabilityMode());
            if (lookMaxHealth.HasValue)
            {
                zdo.Set(ZDOVars.s_health, IndestructibleHealth);
                if (isStructure)
                {
                    zdo.Set(FieldsKey, true);
                    zdo.Set(WearNTearFieldsKey, true);
                    zdo.Set(WearNTearMaxHealthKey, lookMaxHealth.Value);
                }
                return true;
            }

            float? health = GetEffectiveHealth();
            if (!health.HasValue) return false;
            zdo.Set(ZDOVars.s_health, health.Value);
            return true;
        }

        // True while Infinity Hammer (or World Edit Commands, which routes
        // object placement through IH's selection) currently owns the local
        // player's hammer/placement. Mirrors IH's own discriminators using
        // ONLY vanilla types — no hard reference. FAP placement/menu patches
        // early-return on this so they NEVER touch an IH freeze/select/place
        // operation. Inert (returns false) during normal FAP use, so adding
        // the guard cannot change non-IH behaviour.
        public static bool IsInfinityHammerActive()
        {
            try
            {
                var p = Player.m_localPlayer;
                if (p == null) return false;
                var right = p.GetRightItem();
                if (right != null && right.m_customData != null
                    && right.m_customData.ContainsKey("infinity_hammer"))
                    return true;
                var pt = p.m_buildPieces;
                return pt != null && pt.name == "_InfinityHammerPieceTable";
            }
            catch { return false; }
        }

        // Infinity Hammer batches undo through World Edit Commands' UndoHelper, and our
        // group cascade destroys sibling objects IH never targeted — so undo used to
        // restore only the trigger piece. Recording every cascaded ZDO into the open
        // batch makes undo and redo restore the whole group.
        //
        // Recording is gated on WEC's own AddedZDOs.Track marker, so a cascade fired
        // outside an IH/WEC action can't leak stale entries into the next batch. It all
        // resolves by reflection; without WEC the bridge is a no-op.

        private static bool s_undoResolved;
        private static MethodInfo s_undoAddRemoveAction;
        private static FieldInfo s_undoTrackField;

        // True while an IH/WEC undo batch is open (between Begin/EndAction).
        public static bool IsUndoBatchOpen
        {
            get
            {
                EnsureUndoResolved();
                if (s_undoTrackField == null) return false;
                try { return (bool)s_undoTrackField.GetValue(null); }
                catch { return false; }
            }
        }

        // Records a ZDO the FAP cascade is about to destroy into the open
        // WEC undo batch. Call BEFORE the destroy so the snapshot captures
        // live data. Safe no-op when WEC is absent or no batch is open.
        public static void RecordCascadeRemoval(ZDO zdo)
        {
            if (zdo == null || !zdo.IsValid()) return;
            EnsureUndoResolved();
            if (s_undoAddRemoveAction == null || !IsUndoBatchOpen) return;
            try { s_undoAddRemoveAction.Invoke(null, new object[] { zdo }); }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"{VaPieces.PluginName}: WEC undo bridge AddRemoveAction threw: {ex.Message}");
                s_undoAddRemoveAction = null;   // disarm — don't spam per sibling
            }
        }

        private static void EnsureUndoResolved()
        {
            if (s_undoResolved) return;
            s_undoResolved = true;

            try
            {
                Assembly wec = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name;
                    try { name = asm.GetName().Name; }
                    catch { continue; }
                    if (string.Equals(name, "WorldEditCommands", StringComparison.Ordinal)) { wec = asm; break; }
                }
                if (wec == null) return;

                var undoHelper = wec.GetType("WorldEditCommands.UndoHelper", throwOnError: false);
                var addedZdos = wec.GetType("WorldEditCommands.AddedZDOs", throwOnError: false);
                if (undoHelper == null || addedZdos == null) return;

                s_undoAddRemoveAction = undoHelper.GetMethod(
                    "AddRemoveAction",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null, types: new[] { typeof(ZDO) }, modifiers: null);
                s_undoTrackField = addedZdos.GetField(
                    "Track", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch
            {
                s_undoAddRemoveAction = null;
                s_undoTrackField = null;
            }
        }

        private static void EnsureResolved()
        {
            if (s_resolved) return;
            s_resolved = true;

            try
            {
                Assembly ihAsm = FindIhAssembly();
                if (ihAsm == null) return;

                Type configType = FindConfigType(ihAsm);
                if (configType == null)
                {
                    // IH is loaded but the config class is from a fork we
                    // don't know about; mark "present" without getters.
                    s_ihPresent = true;
                    return;
                }

                s_overrideHealthGetter = BuildFloatGetter(configType, HealthMemberNames);
                s_indestructibleGetter = BuildBoolGetter(configType, IndestructibleMemberNames);
                s_invulnerabilityModeGetter = BuildStringGetter(configType, InvulnerabilityModeMemberNames);
                s_ihPresent = true;
            }
            catch
            {
                s_ihPresent = false;
            }
        }

        private static Assembly FindIhAssembly()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try { name = asm.GetName().Name; }
                catch { continue; }
                if (string.IsNullOrEmpty(name)) continue;
                for (int i = 0; i < IhAssemblyNames.Length; i++)
                {
                    if (string.Equals(name, IhAssemblyNames[i], StringComparison.Ordinal))
                        return asm;
                }
            }
            return null;
        }

        private static Type FindConfigType(Assembly asm)
        {
            for (int i = 0; i < IhConfigTypeNames.Length; i++)
            {
                Type t = null;
                try { t = asm.GetType(IhConfigTypeNames[i], throwOnError: false); }
                catch { }
                if (t != null) return t;
            }
            return null;
        }

        private static Func<float> BuildFloatGetter(Type configType, string[] memberNames)
        {
            for (int i = 0; i < memberNames.Length; i++)
            {
                var getter = TryBuildScalarGetter<float>(configType, memberNames[i]);
                if (getter != null) return getter;
            }
            return null;
        }

        private static Func<string> BuildStringGetter(Type configType, string[] memberNames)
        {
            for (int i = 0; i < memberNames.Length; i++)
            {
                var getter = TryBuildScalarGetter<string>(configType, memberNames[i]);
                if (getter != null) return getter;
            }
            return null;
        }

        private static string ReadInvulnerabilityMode()
        {
            try { return s_invulnerabilityModeGetter != null ? s_invulnerabilityModeGetter() : null; }
            catch { return null; }
        }

        // The max health Infinity Hammer stores to pick an invulnerable structure's look; null for "Off", "Legacy"
        // and anything unrecognised, none of which are the -1-health kind of invulnerability.
        private static float? LookMaxHealthForInvulnerabilityMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return null;
            switch (mode.Trim().ToLowerInvariant())
            {
                case "on":      return IntactLookMaxHealth;
                case "worn":    return WornLookMaxHealth;
                case "damaged": return DamagedLookMaxHealth;
                default:        return null;
            }
        }

        private static bool IsLegacyMode(string mode) =>
            !string.IsNullOrEmpty(mode) && string.Equals(mode.Trim(), "Legacy", StringComparison.OrdinalIgnoreCase);

        private static Func<bool> BuildBoolGetter(Type configType, string[] memberNames)
        {
            for (int i = 0; i < memberNames.Length; i++)
            {
                var getter = TryBuildScalarGetter<bool>(configType, memberNames[i]);
                if (getter != null) return getter;
            }
            return null;
        }

        // Handles plain static fields/properties of T plus BepInEx
        // ConfigEntry<T> wrappers (read .Value).
        private static Func<T> TryBuildScalarGetter<T>(Type owner, string memberName)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            FieldInfo fi = owner.GetField(memberName, flags);
            PropertyInfo pi = (fi == null) ? owner.GetProperty(memberName, flags) : null;
            if (fi == null && pi == null) return null;

            Type memberType = fi?.FieldType ?? pi?.PropertyType;
            if (memberType == null) return null;

            if (memberType == typeof(T))
            {
                if (fi != null) return () => (T)fi.GetValue(null);
                return () => (T)pi.GetValue(null, null);
            }

            if (memberType.IsGenericType
                && memberType.GetGenericTypeDefinition().FullName == "BepInEx.Configuration.ConfigEntry`1"
                && memberType.GetGenericArguments()[0] == typeof(T))
            {
                PropertyInfo valueProp = memberType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                if (valueProp == null) return null;

                if (fi != null)
                {
                    return () =>
                    {
                        var entry = fi.GetValue(null);
                        return entry == null ? default(T) : (T)valueProp.GetValue(entry, null);
                    };
                }
                return () =>
                {
                    var entry = pi.GetValue(null, null);
                    return entry == null ? default(T) : (T)valueProp.GetValue(entry, null);
                };
            }

            return null;
        }
    }
}
