using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace DD2SteamMultiplayerDoorstop
{
    internal static class RuntimePatcher
    {
        private const string HarmonyId = "com.superexboom.dd2steammultiplayer.doorstop";
        private static readonly object Lock = new object();
        private static bool _installed;
        private static object _harmony;

        public static void Install()
        {
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().Where(IsIronCrownAssembly))
            {
                TryPatch(assembly);
            }

            DoorstopLog.Write("Runtime patcher armed; waiting for IronCrown.PlatformMgr.Initialize().");
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            if (IsIronCrownAssembly(args.LoadedAssembly))
            {
                TryPatch(args.LoadedAssembly);
            }
        }

        private static bool IsIronCrownAssembly(Assembly assembly)
        {
            try
            {
                return string.Equals(assembly.GetName().Name, "IronCrown", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void TryPatch(Assembly ironCrown)
        {
            lock (Lock)
            {
                if (_installed)
                {
                    return;
                }

                try
                {
                    Type platformMgr = ironCrown.GetType("Assets.Code.Platform.PlatformMgr", throwOnError: false);
                    MethodInfo original = platformMgr?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
                    MethodInfo postfix = typeof(PlatformMgrInitializePatch).GetMethod(nameof(PlatformMgrInitializePatch.Postfix), BindingFlags.NonPublic | BindingFlags.Static);

                    if (original == null || postfix == null)
                    {
                        DoorstopLog.Write("Could not find PlatformMgr.Initialize patch target.");
                        return;
                    }

                    PatchWithHarmony(original, postfix);
                    _installed = true;
                    DoorstopLog.Write("Patched PlatformMgr.Initialize postfix.");
                }
                catch (Exception ex)
                {
                    DoorstopLog.Write("Failed to patch PlatformMgr.Initialize: " + ex);
                }
            }
        }

        private static void PatchWithHarmony(MethodInfo original, MethodInfo postfix)
        {
            Assembly harmonyAssembly = LoadHarmonyAssembly();
            Type harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", throwOnError: true);
            Type harmonyMethodType = harmonyAssembly.GetType("HarmonyLib.HarmonyMethod", throwOnError: true);

            ConstructorInfo harmonyCtor = harmonyType.GetConstructor(new[] { typeof(string) });
            ConstructorInfo harmonyMethodCtor = harmonyMethodType.GetConstructor(new[] { typeof(MethodInfo) });
            if (harmonyCtor == null || harmonyMethodCtor == null)
            {
                throw new MissingMethodException("Could not find required Harmony constructors.");
            }

            _harmony = harmonyCtor.Invoke(new object[] { HarmonyId });
            object postfixHarmonyMethod = harmonyMethodCtor.Invoke(new object[] { postfix });

            MethodInfo patchMethod = FindPatchMethod(harmonyType, harmonyMethodType);
            ParameterInfo[] parameters = patchMethod.GetParameters();
            object[] args = new object[parameters.Length];
            args[0] = original;

            for (int i = 1; i < parameters.Length; i++)
            {
                if (string.Equals(parameters[i].Name, "postfix", StringComparison.OrdinalIgnoreCase))
                {
                    args[i] = postfixHarmonyMethod;
                }
                else
                {
                    args[i] = null;
                }
            }

            patchMethod.Invoke(_harmony, args);
        }

        private static Assembly LoadHarmonyAssembly()
        {
            Assembly loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "0Harmony", StringComparison.OrdinalIgnoreCase));
            if (loaded != null)
            {
                return loaded;
            }

            string harmonyPath = Path.Combine(Paths.GameRoot, "BepInEx", "core", "0Harmony.dll");
            if (!File.Exists(harmonyPath))
            {
                throw new FileNotFoundException("0Harmony.dll was not found in BepInEx core.", harmonyPath);
            }

            return Assembly.LoadFrom(harmonyPath);
        }

        private static MethodInfo FindPatchMethod(Type harmonyType, Type harmonyMethodType)
        {
            foreach (MethodInfo method in harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!string.Equals(method.Name, "Patch", StringComparison.Ordinal))
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < 3)
                {
                    continue;
                }

                if (parameters[0].ParameterType != typeof(MethodBase))
                {
                    continue;
                }

                bool hasPostfix = parameters.Any(parameter =>
                    string.Equals(parameter.Name, "postfix", StringComparison.OrdinalIgnoreCase) &&
                    parameter.ParameterType == harmonyMethodType);
                if (hasPostfix)
                {
                    return method;
                }
            }

            throw new MissingMethodException("Could not find Harmony.Patch(MethodBase, ..., postfix, ...) method.");
        }
    }
}
