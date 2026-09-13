using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using Assets.Code.Game;
using Assets.Code.Math;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DD2SteamMultiplayerHost
{
    /// <summary>
    /// Temporarily detaches Arena combat randomness from the active expedition.
    /// The native runtime still calls its normal RandomContainer APIs; this scope
    /// replaces the saved streams with fresh seeds and restores the exact map when
    /// Arena leaves, including the private active/unidentified state.
    /// </summary>
    internal sealed class ArenaRandomContext
    {
        private static readonly RandomIdentifier[] CombatIdentifiers =
        {
            RandomIdentifier.COMBAT,
            RandomIdentifier.SKILL_CALCULATION,
            RandomIdentifier.EFFECT,
            RandomIdentifier.RESIST,
            RandomIdentifier.DEATHS_DOOR,
            RandomIdentifier.ACTOR_CONTROLLER,
            RandomIdentifier.SUMMON,
            RandomIdentifier.BOSS,
            RandomIdentifier.TOKEN,
            RandomIdentifier.ACTOR,
            RandomIdentifier.ACTOR_CONTAINER,
            RandomIdentifier.ACT_OUT,
            RandomIdentifier.STRESS,
            RandomIdentifier.AFFINITY,
            RandomIdentifier.WOUND,
            RandomIdentifier.SKILL_MODIFIER,
            RandomIdentifier.AI,
            RandomIdentifier.RANDOM_GAMEOBJECT
        };

        private readonly Action<string> _log;
        private readonly FieldInfo _savedInstanceField;
        private readonly FieldInfo _currentIdentifierField;
        private readonly FieldInfo _unidentifiedStateField;
        private readonly FieldInfo _initialRandomMapField;
        private JObject _savedJson;
        private Dictionary<string, UnityEngine.Random.State> _savedInitialMap;
        private object _savedCurrentIdentifier;
        private UnityEngine.Random.State _savedUnidentifiedState;
        private bool _savedUnidentifiedStateCaptured;
        private UnityEngine.Random.State _savedUnityState;
        private System.Random _arenaRandom;
        private bool _active;
        private int _seed;
        private string _savedDigest;

        public ArenaRandomContext(Action<string> log)
        {
            _log = log;
            Type type = typeof(RandomContainer);
            _savedInstanceField = type.GetField("s_SavedInstance", BindingFlags.Static | BindingFlags.NonPublic);
            _currentIdentifierField = type.GetField("m_currentStateIdentifierContainer", BindingFlags.Instance | BindingFlags.NonPublic);
            _unidentifiedStateField = type.GetField("m_unidentifiedState", BindingFlags.Instance | BindingFlags.NonPublic);
            _initialRandomMapField = type.GetField("m_initialRandomMap", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        public bool IsActive => _active;
        public int Seed => _seed;

        public bool TryBegin(string reason, out string error)
        {
            error = string.Empty;
            if (_active)
            {
                return true;
            }

            try
            {
                RandomContainer saved = GetSavedContainer();
                InvokePrivate(saved, "SaveCurrentState");
                _savedJson = JObject.Parse(RandomContainer.SaveToJson().ToString());
                _savedDigest = ComputeDigest(_savedJson);
                _savedUnityState = UnityEngine.Random.state;
                _savedCurrentIdentifier = _currentIdentifierField == null
                    ? null
                    : _currentIdentifierField.GetValue(saved);
                if (_unidentifiedStateField != null)
                {
                    _savedUnidentifiedState = (UnityEngine.Random.State)_unidentifiedStateField.GetValue(saved);
                    _savedUnidentifiedStateCaptured = true;
                }
                _savedInitialMap = CloneInitialMap(saved);

                _seed = CreateSeed();
                _arenaRandom = new System.Random(_seed);
                _active = true;
                foreach (RandomIdentifier identifier in CombatIdentifiers)
                {
                    if (identifier != null)
                    {
                        RandomContainer.SetSeed(identifier, _arenaRandom.Next());
                    }
                }

                Log("[arena-rng] begin reason=" + (reason ?? "[none]") +
                    " seed=" + _seed + " streams=" + CombatIdentifiers.Length +
                    " savedBytes=" + _savedJson.ToString(Newtonsoft.Json.Formatting.None).Length +
                    " savedDigest=" + _savedDigest +
                    " arenaDigest=" + ComputeDigest(JObject.Parse(RandomContainer.SaveToJson().ToString())));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (_active)
                {
                    try { End("begin-failed"); } catch { }
                }
                _active = false;
                _savedJson = null;
                _savedInitialMap = null;
                _savedCurrentIdentifier = null;
                _savedUnidentifiedStateCaptured = false;
                Log("[arena-rng] begin failed error=" + ex);
                return false;
            }
        }

        public double NextDouble()
        {
            return _arenaRandom == null ? 0.5d : _arenaRandom.NextDouble();
        }

        public void End(string reason)
        {
            if (!_active)
            {
                return;
            }

            try
            {
                RandomContainer saved = GetSavedContainer();
                // Return to the unidentified stream before replacing the saved map;
                // otherwise the last Arena identifier can overwrite one Run entry.
                InvokePrivate(saved, "ReturnToUnidentifiedState");
                RandomContainer.LoadFromJson(_savedJson);
                if (_initialRandomMapField != null && _savedInitialMap != null)
                {
                    _initialRandomMapField.SetValue(saved, CloneMap(_savedInitialMap));
                }
                if (_currentIdentifierField != null)
                {
                    _currentIdentifierField.SetValue(saved, _savedCurrentIdentifier);
                }
                if (_unidentifiedStateField != null && _savedUnidentifiedStateCaptured)
                {
                    _unidentifiedStateField.SetValue(saved, _savedUnidentifiedState);
                }
                UnityEngine.Random.state = _savedUnityState;
                string restoredDigest = ComputeDigest(JObject.Parse(RandomContainer.SaveToJson().ToString()));
                Log("[arena-rng] end reason=" + (reason ?? "[none]") +
                    " seed=" + _seed + " restored=true digest=" + restoredDigest +
                    " digestMatch=" + string.Equals(_savedDigest, restoredDigest, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Log("[arena-rng] end failed reason=" + (reason ?? "[none]") + " error=" + ex);
            }
            finally
            {
                _active = false;
                _savedJson = null;
                _savedInitialMap = null;
                _savedCurrentIdentifier = null;
                _savedUnidentifiedStateCaptured = false;
                _arenaRandom = null;
                _seed = 0;
                _savedDigest = null;
            }
        }

        private RandomContainer GetSavedContainer()
        {
            // SaveToJson lazily constructs s_Saved. Read it afterwards so all
            // private state manipulation targets the actual singleton instance.
            RandomContainer.SaveToJson();
            RandomContainer result = _savedInstanceField == null
                ? null
                : _savedInstanceField.GetValue(null) as RandomContainer;
            if (result == null)
            {
                throw new InvalidOperationException("RandomContainer saved instance is unavailable.");
            }
            return result;
        }

        private Dictionary<string, UnityEngine.Random.State> CloneInitialMap(RandomContainer saved)
        {
            if (_initialRandomMapField == null)
            {
                return null;
            }
            var source = _initialRandomMapField.GetValue(saved) as Dictionary<string, UnityEngine.Random.State>;
            return source == null ? null : CloneMap(source);
        }

        private static Dictionary<string, UnityEngine.Random.State> CloneMap(
            Dictionary<string, UnityEngine.Random.State> source)
        {
            return source == null
                ? null
                : new Dictionary<string, UnityEngine.Random.State>(source);
        }

        private static int CreateSeed()
        {
            byte[] bytes = new byte[4];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            int seed = BitConverter.ToInt32(bytes, 0);
            return seed == int.MinValue ? int.MaxValue : Math.Abs(seed);
        }

        private static string ComputeDigest(JObject value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(
                    value == null ? string.Empty : value.ToString(Newtonsoft.Json.Formatting.None));
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static void InvokePrivate(object instance, string name)
        {
            if (instance == null) throw new InvalidOperationException("RandomContainer instance is null.");
            MethodInfo method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new MissingMethodException(instance.GetType().FullName, name);
            method.Invoke(instance, null);
        }

        private void Log(string message)
        {
            try { _log?.Invoke(message); } catch { }
        }
    }
}
