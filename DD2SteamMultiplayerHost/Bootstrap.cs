using System;
using Assets.Code.Platform;
using UnityEngine;

namespace DD2SteamMultiplayerHost
{
    public static class Bootstrap
    {
        private const string RunnerObjectName = "DD2SteamMultiplayerHost";
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
                if (PlatformMgr.Instance == null)
                {
                    LogWarning("PlatformMgr is not initialized; delaying host startup.");
                    _started = false;
                    return;
                }

                Debug.Log("[DD2SteamMP] Bootstrap.EnsureStarted invoked.");
                HostLog.Write("Bootstrap.EnsureStarted invoked.");

                GameObject gameObject = GameObject.Find(RunnerObjectName);
                if (gameObject == null)
                {
                    gameObject = new GameObject(RunnerObjectName);
                    UnityEngine.Object.DontDestroyOnLoad(gameObject);
                    gameObject.hideFlags = HideFlags.HideAndDontSave;
                }

                if (gameObject.GetComponent<DD2SteamMultiplayerRunner>() == null)
                {
                    gameObject.AddComponent<DD2SteamMultiplayerRunner>();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[DD2SteamMP] Bootstrap failed: " + ex);
                HostLog.Write("Bootstrap failed: " + ex);
                _started = false;
            }
        }

        private static void LogWarning(string message)
        {
            Debug.LogWarning("[DD2SteamMP] " + message);
            HostLog.Write(message);
        }
    }
}
