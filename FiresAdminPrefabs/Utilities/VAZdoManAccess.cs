using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // ZDOMan.m_objectsByID is private in the shipped assembly, so reading it off the type
    // compiles against the publicized reference and throws FieldAccessException at runtime.
    // Every world-wide ZDO walk in FAP goes through here.
    public static class VAZdoManAccess
    {
        private const string ObjectsByIdFieldName = "m_objectsByID";

        private static System.Reflection.FieldInfo _objectsByIdField;
        private static bool _resolved;
        private static bool _warned;

        public static Dictionary<ZDOID, ZDO> ObjectsById()
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null) return null;

            if (!_resolved)
            {
                _resolved = true;
                _objectsByIdField = HarmonyLib.AccessTools.Field(typeof(ZDOMan), ObjectsByIdFieldName);
                if (_objectsByIdField == null && !_warned)
                {
                    _warned = true;
                    Debug.LogWarning($"{VaPieces.PluginName}: ZDOMan.{ObjectsByIdFieldName} did not resolve — "
                        + "world-wide ZDO walks are unavailable this session. A game patch has probably renamed it.");
                }
            }
            if (_objectsByIdField == null) return null;

            try { return _objectsByIdField.GetValue(zdoMan) as Dictionary<ZDOID, ZDO>; }
            catch (Exception ex)
            {
                if (!_warned)
                {
                    _warned = true;
                    Debug.LogWarning($"{VaPieces.PluginName}: reading ZDOMan.{ObjectsByIdFieldName} threw ({ex.GetType().Name}: {ex.Message}).");
                }
                return null;
            }
        }

        // A snapshot, so a zone streaming in mid-walk cannot invalidate the enumerator.
        public static List<ZDO> SnapshotAllZdos()
        {
            var objectsById = ObjectsById();
            if (objectsById == null) return null;

            var all = new List<ZDO>(objectsById.Count);
            foreach (var entry in objectsById)
            {
                if (entry.Value != null) all.Add(entry.Value);
            }
            return all;
        }
    }
}
