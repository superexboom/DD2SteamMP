using System;

namespace Doorstop
{
    public static class Entrypoint
    {
        public static void Start()
        {
            try
            {
                DD2SteamMultiplayerDoorstop.DoorstopLog.ResetForNewProcess();
                DD2SteamMultiplayerDoorstop.DoorstopLog.Write("Doorstop entry loaded.");
                DD2SteamMultiplayerDoorstop.AssemblyResolver.Install();
                DD2SteamMultiplayerDoorstop.RuntimePatcher.Install();
            }
            catch (Exception ex)
            {
                DD2SteamMultiplayerDoorstop.DoorstopLog.Write("Doorstop entry failed: " + ex);
            }
            finally
            {
                DD2SteamMultiplayerDoorstop.BepInExChainloader.StartOriginalPreloader();
            }
        }
    }
}
