using System.Collections.Concurrent;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Log lines raised on worker threads. BepInEx only receives Unity's main-thread log event, so a
    // worker thread's Debug.Log reaches Player.log but never the BepInEx log, and BepInEx's console
    // writer isn't safe to share across threads anyway. Lines are queued here and written from the
    // plugin's Update on the main thread.
    public static class VAFapThreadLog
    {
        private static readonly ConcurrentQueue<(bool IsWarning, string Message)> _pending =
            new ConcurrentQueue<(bool IsWarning, string Message)>();

        public static void Info(string message) => _pending.Enqueue((false, message));

        public static void Warning(string message) => _pending.Enqueue((true, message));

        public static void Flush()
        {
            while (_pending.TryDequeue(out var line))
            {
                if (line.IsWarning) Debug.LogWarning(line.Message);
                else Debug.Log(line.Message);
            }
        }
    }
}
