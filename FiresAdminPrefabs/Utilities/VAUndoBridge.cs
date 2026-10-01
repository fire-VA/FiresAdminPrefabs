using System;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Reflection bridge to WorldEditCommands' undo system. When WEC is
    // installed, every Object.Instantiate of a ZNetView'd piece inside a
    // BeginAction/EndAction window gets folded into a single UndoAction on
    // WEC's stack — one /undo reverts the whole FAP batch.
    //
    // Reflection rather than a hard assembly reference so FAP works
    // standalone without WEC. WEC's ZNetView.Awake postfix does the actual
    // ZDO capture when its static Track flag is true; our only job is to
    // flip the flag on/off around our spawn paths.
    //
    // Nested FAP calls (clutter paint -> monster spawn -> blueprint ghost)
    // share a single outer batch via the depth counter; inner Begins open
    // SubActions and only the outermost End flushes.
    public static class VAUndoBridge
    {
        private static MethodInfo _beginAction;
        private static MethodInfo _endAction;
        private static MethodInfo _beginSubAction;
        private static MethodInfo _endSubAction;
        private static bool _resolved;
        private static bool _available;
        private static int _depth;

        public static bool IsAvailable
        {
            get
            {
                EnsureResolved();
                return _available;
            }
        }

        public static IDisposable Scope() => new ScopeHandle();

        public static bool BeginAction()
        {
            EnsureResolved();
            if (!_available) return false;

            try
            {
                if (_depth == 0)
                    _beginAction.Invoke(null, null);
                else
                    _beginSubAction.Invoke(null, null);
                _depth++;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAUndoBridge.BeginAction threw: {ex.Message}");
                return false;
            }
        }

        public static void EndAction()
        {
            if (!_available) return;
            if (_depth <= 0) return;

            try
            {
                _depth--;
                if (_depth == 0)
                    _endAction.Invoke(null, null);
                else
                    _endSubAction.Invoke(null, null);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAUndoBridge.EndAction threw: {ex.Message}");
            }
        }

        private static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                Type helperType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        Type t = asm.GetType("WorldEditCommands.UndoHelper", false);
                        if (t != null) { helperType = t; break; }
                    }
                    catch { }
                }
                if (helperType == null) return;

                _beginAction    = helperType.GetMethod("BeginAction",    BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                _endAction      = helperType.GetMethod("EndAction",      BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                _beginSubAction = helperType.GetMethod("BeginSubAction", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                _endSubAction   = helperType.GetMethod("EndSubAction",   BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

                _available = _beginAction != null && _endAction != null
                             && _beginSubAction != null && _endSubAction != null;
            }
            catch
            {
                _available = false;
            }
        }

        private sealed class ScopeHandle : IDisposable
        {
            private bool _opened;
            public ScopeHandle() { _opened = BeginAction(); }
            public void Dispose() { if (_opened) EndAction(); }
        }
    }
}
