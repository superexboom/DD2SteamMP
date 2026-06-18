using System;
using System.IO;

namespace DD2SteamMultiplayerDoorstop
{
    internal static class DoorstopLog
    {
        private static readonly object Lock = new object();

        public static void ResetForNewProcess()
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Paths.ModRoot);
                    if (File.Exists(Paths.LogPath) && new FileInfo(Paths.LogPath).Length > 0L)
                    {
                        File.Copy(Paths.LogPath, Paths.PreviousLogPath, true);
                    }

                    File.WriteAllText(Paths.LogPath, string.Empty);
                }
            }
            catch
            {
            }
        }

        public static void Write(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [DD2SteamMP] " + message;

            try
            {
                Console.WriteLine(line);
            }
            catch
            {
            }

            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Paths.ModRoot);
                    File.AppendAllText(Paths.LogPath, line + Environment.NewLine);
                }
            }
            catch
            {
            }
        }
    }
}
