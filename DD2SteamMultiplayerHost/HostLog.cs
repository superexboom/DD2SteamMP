using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace DD2SteamMultiplayerHost
{
    internal static class HostLog
    {
        private static readonly object Lock = new object();
        private static readonly Dictionary<string, DateTime> NextWriteUtcByKey =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);

        public static void Write(string message)
        {
            try
            {
                lock (Lock)
                {
                    WriteUnlocked(message);
                }
            }
            catch
            {
            }
        }

        public static bool WriteThrottled(string key, string message, TimeSpan interval)
        {
            try
            {
                lock (Lock)
                {
                    DateTime now = DateTime.UtcNow;
                    string normalizedKey = string.IsNullOrWhiteSpace(key) ? message ?? string.Empty : key;
                    DateTime nextWriteUtc;
                    if (NextWriteUtcByKey.TryGetValue(normalizedKey, out nextWriteUtc) && now < nextWriteUtc)
                    {
                        return false;
                    }

                    NextWriteUtcByKey[normalizedKey] = now + interval;
                    WriteUnlocked(message);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void WriteUnlocked(string message)
        {
            Directory.CreateDirectory(HostPaths.ModRoot);
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [DD2SteamMP] " + message;
            File.AppendAllText(HostPaths.LogPath, line + Environment.NewLine);
        }
    }
}
