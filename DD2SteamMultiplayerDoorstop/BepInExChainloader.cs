using System;
using System.IO;
using System.Reflection;

namespace DD2SteamMultiplayerDoorstop
{
    internal static class BepInExChainloader
    {
        private const string DisableChainloadEnvVar = "DD2_STEAM_MP_DISABLE_BEPINEX_CHAINLOAD";
        private const string ChainloadTargetEnvVar = "DD2_STEAM_MP_BEPINEX_PRELOADER";
        private const string DoorstopInvokeDllPathEnvVar = "DOORSTOP_INVOKE_DLL_PATH";
        private const string DoorstopDllSearchDirsEnvVar = "DOORSTOP_DLL_SEARCH_DIRS";

        public static void StartOriginalPreloader()
        {
            if (string.Equals(Environment.GetEnvironmentVariable(DisableChainloadEnvVar), "1", StringComparison.OrdinalIgnoreCase))
            {
                DoorstopLog.Write("BepInEx chainload skipped by " + DisableChainloadEnvVar + ".");
                return;
            }

            string target = Environment.GetEnvironmentVariable(ChainloadTargetEnvVar);
            if (string.IsNullOrWhiteSpace(target))
            {
                target = Path.Combine(Paths.GameRoot, "BepInEx", "core", "BepInEx.Preloader.dll");
            }
            else if (!Path.IsPathRooted(target))
            {
                target = Path.Combine(Paths.GameRoot, target);
            }

            if (!File.Exists(target))
            {
                DoorstopLog.Write("BepInEx preloader was not found at " + target + "; continuing without chainload.");
                return;
            }

            try
            {
                DoorstopLog.Write("Starting BepInEx preloader: " + target);
                string oldInvokeDllPath = Environment.GetEnvironmentVariable(DoorstopInvokeDllPathEnvVar);
                string oldDllSearchDirs = Environment.GetEnvironmentVariable(DoorstopDllSearchDirsEnvVar);
                try
                {
                    SetBepInExDoorstopEnvironment(target, oldDllSearchDirs);

                    Assembly assembly = Assembly.LoadFrom(target);
                    Type entrypoint = assembly.GetType("Doorstop.Entrypoint", throwOnError: true);
                    MethodInfo start = entrypoint.GetMethod("Start", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                    if (start == null)
                    {
                        DoorstopLog.Write("BepInEx preloader entrypoint Doorstop.Entrypoint.Start() was not found.");
                        return;
                    }

                    start.Invoke(null, null);
                    DoorstopLog.Write("BepInEx preloader returned.");
                }
                finally
                {
                    Environment.SetEnvironmentVariable(DoorstopInvokeDllPathEnvVar, oldInvokeDllPath);
                    Environment.SetEnvironmentVariable(DoorstopDllSearchDirsEnvVar, oldDllSearchDirs);
                }
            }
            catch (TargetInvocationException ex)
            {
                DoorstopLog.Write("BepInEx preloader threw: " + (ex.InnerException ?? ex));
                throw;
            }
            catch (Exception ex)
            {
                DoorstopLog.Write("BepInEx chainload failed: " + ex);
                throw;
            }
        }

        private static void SetBepInExDoorstopEnvironment(string target, string oldDllSearchDirs)
        {
            string coreDir = Path.GetDirectoryName(target);
            Environment.SetEnvironmentVariable(DoorstopInvokeDllPathEnvVar, target);

            if (string.IsNullOrWhiteSpace(coreDir))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(oldDllSearchDirs))
            {
                Environment.SetEnvironmentVariable(DoorstopDllSearchDirsEnvVar, coreDir);
                return;
            }

            string prefix = coreDir + Path.PathSeparator;
            if (oldDllSearchDirs.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(oldDllSearchDirs, coreDir, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Environment.SetEnvironmentVariable(DoorstopDllSearchDirsEnvVar, prefix + oldDllSearchDirs);
        }
    }
}
