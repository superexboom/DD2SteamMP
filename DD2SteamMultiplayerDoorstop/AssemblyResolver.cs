using System;
using System.IO;
using System.Reflection;

namespace DD2SteamMultiplayerDoorstop
{
    internal static class AssemblyResolver
    {
        private static bool _installed;

        public static void Install()
        {
            if (_installed)
            {
                return;
            }

            _installed = true;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        }

        private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            string assemblyFileName = new AssemblyName(args.Name).Name + ".dll";

            foreach (string directory in Paths.GetProbeDirectories())
            {
                string candidate = Path.Combine(directory, assemblyFileName);
                if (!File.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    return Assembly.LoadFrom(candidate);
                }
                catch (Exception ex)
                {
                    DoorstopLog.Write("Failed to resolve " + args.Name + " from " + candidate + ": " + ex.Message);
                }
            }

            return null;
        }
    }
}
