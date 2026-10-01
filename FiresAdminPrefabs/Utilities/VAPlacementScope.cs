using System;
using System.Collections.Generic;

namespace VerdantsAscent.Utilities
{
    // One IDisposable for several placement-time scopes, disposed in reverse order so the outermost closes last.
    internal sealed class VAPlacementScope : IDisposable
    {
        private readonly List<IDisposable> _scopes = new List<IDisposable>();

        public static IDisposable Combine(params IDisposable[] scopes)
        {
            var combined = new VAPlacementScope();
            foreach (IDisposable scope in scopes)
                if (scope != null) combined._scopes.Add(scope);
            return combined._scopes.Count == 0 ? null : combined;
        }

        public void Dispose()
        {
            for (int index = _scopes.Count - 1; index >= 0; index--)
            {
                try { _scopes[index].Dispose(); }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"{VaPieces.PluginName}: placement scope dispose failed: {ex.Message}");
                }
            }
            _scopes.Clear();
        }
    }
}
