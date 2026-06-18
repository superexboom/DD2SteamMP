using System;
using System.IO;
using System.Reflection;

namespace DD2SteamMultiplayerDoorstop
{
    internal static class HostLoader
    {
        private static bool _started;

        public static void EnsureStarted()
        {
            if (_started)
            {
                return;
            }

            _started = true;

            try
            {
                string hostPath = Path.Combine(Paths.ModRoot, "DD2SteamMultiplayerHost.dll");
                if (!File.Exists(hostPath))
                {
                    DoorstopLog.Write("Host assembly was not found at " + hostPath + ".");
                    return;
                }

                DoorstopLog.Write("Starting host assembly: " + hostPath);
                Assembly assembly = Assembly.LoadFrom(hostPath);
                Type bootstrap = assembly.GetType("DD2SteamMultiplayerHost.Bootstrap", throwOnError: true);
                MethodInfo ensureStarted = bootstrap.GetMethod("EnsureStarted", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (ensureStarted == null)
                {
                    DoorstopLog.Write("Host entrypoint DD2SteamMultiplayerHost.Bootstrap.EnsureStarted() was not found.");
                    return;
                }

                ensureStarted.Invoke(null, null);
            }
            catch (TargetInvocationException ex)
            {
                DoorstopLog.Write("Host startup threw: " + (ex.InnerException ?? ex));
            }
            catch (Exception ex)
            {
                DoorstopLog.Write("Host startup failed: " + ex);
            }
        }
    }
}
