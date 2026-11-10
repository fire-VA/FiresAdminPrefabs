using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // [fgn] 10-23 for [saddles] (player report via Fire: signs another player wrote show the prefab text until a reload).
    // VALiveChildStrip cancelled Sign's 2 s UpdateText poll on bundle and blueprint children. Sign.Awake reads the text once; after
    // that a sign only updates when the LOCAL player writes it (Sign.SetText calls UpdateText). Text that changes later (another player
    // writes it) or a permission check that has not answered yet (RelationsManager.CheckPermissionAsync is async on Steam; vanilla
    // simply asks again on the next 2 s poll) is never shown until the sign is reloaded.
    // This replaces the cancelled poll: every 2 s it only reads the ZDO's DataRevision (one uint) and calls UpdateText when it changed,
    // then re-asks a few times so a slow permission answer still fills the text. An unchanged sign costs one uint compare per 2 s.
    internal sealed class VASignRevisionPoll : MonoBehaviour
    {
        private const float IntervalSeconds = 2f;
        private const int RetriesAfterChange = 5;   // re-ask the permission check for 10 s after a change, like vanilla's poll would

        private static readonly MethodInfo UpdateTextMethod = AccessTools.Method(typeof(Sign), "UpdateText");

        private Sign _sign;
        private ZNetView _nview;
        private uint _seenRevision;
        private int _retries = RetriesAfterChange;   // Awake's first permission check may still be pending

        private void Start()
        {
            _sign = GetComponent<Sign>();
            _nview = GetComponent<ZNetView>();
            if (_nview != null && _nview.IsValid()) _seenRevision = _nview.GetZDO().DataRevision;
            InvokeRepeating(nameof(Tick), IntervalSeconds, IntervalSeconds);
        }

        private void Tick()
        {
            if (_sign == null || _nview == null || !_nview.IsValid() || UpdateTextMethod == null) return;
            uint revision = _nview.GetZDO().DataRevision;
            if (revision != _seenRevision)
            {
                _seenRevision = revision;
                _retries = RetriesAfterChange;
                VaPieces.Log?.LogDebug($"[VASignRevisionPoll] '{name}' text changed (revision {revision}); updating");
            }
            else if (_retries <= 0) return;
            else _retries--;
            UpdateTextMethod.Invoke(_sign, null);
        }

        // Called from VALiveChildStrip.Apply in place of sign.CancelInvoke("UpdateText").
        internal static void Replace(Sign sign)
        {
            if (sign == null) return;
            sign.CancelInvoke("UpdateText");
            if (sign.GetComponent<VASignRevisionPoll>() == null) sign.gameObject.AddComponent<VASignRevisionPoll>();
        }
    }
}
