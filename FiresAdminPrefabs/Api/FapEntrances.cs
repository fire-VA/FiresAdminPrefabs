using System;

namespace VerdantsAscent.Api
{
    // 1.1.361: a placed dungeon entrance (LocationProxy from FAP's Entrances tab) is being removed, by the hammer or fap_dungeons; raised on the removing peer, while the ZDO is alive.
    public static class FapEntrances
    {
        public static event Action<ZDO> Removed;
        internal static void RaiseRemoved(ZDO zdo) => Removed?.Invoke(zdo);

        /// <summary>1.1.372: server only. Resets a placed entrance to Empty and returns its proxy's ZDO (new for a fully generated row), or null. Does not raise Removed.</summary>
        public static ZDO ResetToEmpty(ZDO entrance) => VerdantsAscent.Utilities.VAFapDungeons.ResetToEmpty(entrance);
    }
}