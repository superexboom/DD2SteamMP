using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace DD2SteamMultiplayerDoorstop
{
    internal static class Paths
    {
        public static readonly string GameRoot = GetGameRoot();
        public static readonly string ModRoot = Path.Combine(GameRoot, "DD2SteamMP");
        public static readonly string LogPath = Path.Combine(ModRoot, "doorstop_host.log");
        public static readonly string PreviousLogPath = Path.Combine(ModRoot, "doorstop_host.previous.log");

        public static IEnumerable<string> GetProbeDirectories()
        {
            yield return Path.Combine(GameRoot, "BepInEx", "core");
            yield return ModRoot;
            yield return Path.Combine(GameRoot, "Darkest Dungeon II_Data", "Managed");
            yield return GameRoot;
        }

        private static string GetGameRoot()
        {
            string doorstopProcessPath = Environment.GetEnvironmentVariable("DOORSTOP_PROCESS_PATH");
            if (!string.IsNullOrWhiteSpace(doorstopProcessPath))
            {
                string root = Path.GetDirectoryName(doorstopProcessPath);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    return root;
                }
            }

            try
            {
                string processPath = Process.GetCurrentProcess().MainModule.FileName;
                string root = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    return root;
                }
            }
            catch
            {
            }

            return Environment.CurrentDirectory;
        }
    }
}
