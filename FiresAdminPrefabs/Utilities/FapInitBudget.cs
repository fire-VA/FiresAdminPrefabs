using System.Diagnostics;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // 1.1.424 ([ghost], lead 11-12 from Fire's run_3: 57 of 79 post-arrival frames >= 50 ms named FAP's init coroutines; they ran AFTER spawn on
    // purpose, at an 8 ms budget). One per-frame clock for a coroutine:
    //  - while the loading screen is up (no local player yet, or Hud.m_loadingScreen active) the budget is LoadingBudgetMs: the work rides the
    //    load (Fire's rule: a longer load beats post-load hitches);
    //  - after the world shows, PlayBudgetMs (2 ms).
    // The clock is checked per item (Over()); the caller yields and calls NextFrame(). Done() writes one Info line: total ms, frames, worst
    // slice, and how much of it ran under the loading screen.
    internal sealed class FapInitBudget
    {
        internal const float LoadingBudgetMs = 40f, PlayBudgetMs = 2f;

        private readonly string _name;
        private readonly Stopwatch _slice = Stopwatch.StartNew();
        private double _totalMs, _loadingMs, _worstMs;
        private int _frames = 1, _loadingFrames;
        private bool _sliceLoading;

        internal FapInitBudget(string name) { _name = name; _sliceLoading = Loading; }

        internal static bool Loading
        {
            get
            {
                if (Player.m_localPlayer == null) return true;
                var hud = Hud.instance;
                return hud != null && hud.m_loadingScreen != null && hud.m_loadingScreen.gameObject.activeSelf;
            }
        }

        internal float BudgetMs => _sliceLoading ? LoadingBudgetMs : PlayBudgetMs;

        // true = this frame's budget is spent: the caller yields, then calls NextFrame()
        internal bool Over() => _slice.Elapsed.TotalMilliseconds >= BudgetMs;

        internal void NextFrame()
        {
            CloseSlice();
            _frames++;
            _sliceLoading = Loading;
            _slice.Restart();
        }

        private void CloseSlice()
        {
            double ms = _slice.Elapsed.TotalMilliseconds;
            _totalMs += ms;
            if (_sliceLoading) { _loadingMs += ms; _loadingFrames++; }
            if (ms > _worstMs) _worstMs = ms;
        }

        internal void Done()
        {
            CloseSlice();
            VaPieces.Log?.LogInfo($"[InitBudget] {_name}: {_totalMs:0} ms of work over {_frames} frame(s), worst slice {_worstMs:0.0} ms; " +
                                  $"{_loadingMs:0} ms in {_loadingFrames} frame(s) under the loading screen ({LoadingBudgetMs:0} ms/frame), " +
                                  $"{_totalMs - _loadingMs:0} ms after the world showed ({PlayBudgetMs:0} ms/frame)");
        }
    }
}
