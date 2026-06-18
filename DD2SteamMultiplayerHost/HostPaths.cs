using System;
using System.Diagnostics;
using System.IO;

namespace DD2SteamMultiplayerHost
{
    internal static class HostPaths
    {
        public static readonly string GameRoot = GetGameRoot();
        public static readonly string ModRoot = Path.Combine(GameRoot, "DD2SteamMP");
        public static readonly string LogPath = Path.Combine(ModRoot, "doorstop_host.log");
        public static readonly string CommandPath = Path.Combine(ModRoot, "command.txt");

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
