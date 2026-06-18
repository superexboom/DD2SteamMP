using System;

namespace DD2SteamMultiplayerDoorstop
{
    internal static class PlatformMgrInitializePatch
    {
        internal static void Postfix()
        {
            try
            {
                DoorstopLog.Write("PlatformMgr.Initialize completed; starting DD2SteamMP host.");
                HostLoader.EnsureStarted();
            }
            catch (Exception ex)
            {
                DoorstopLog.Write("PlatformMgr.Initialize postfix failed: " + ex);
            }
        }
    }
}
