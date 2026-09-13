using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Assets.Code.Inputs;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DD2SteamMultiplayerHost
{
    /// <summary>
    /// Blocks game input under plugin IMGUI windows (F6/F7 + secondary arena windows)
    /// without blanketing the whole screen.
    ///
    /// Strategy:
    /// 1. Harmony postfix on <see cref="InputSource_Unity.GetPointerValues"/> — the game's
    ///    real mouse path — zeroes button states while the cursor is inside a registered rect.
    /// 2. Region-matched transparent UGUI shields so EventSystem / UICanvasRaycastUtil also
    ///    treat those areas as occupied UI (actors, map drag, etc.).
    /// </summary>
    public static class UiInputBlocker
    {
        private static readonly List<Rect> _screenRects = new List<Rect>();
        private static readonly List<Rect> _guiRects = new List<Rect>();
        private static readonly List<Image> _shieldImages = new List<Image>();

        private static bool _patchesInstalled;
        private static bool _patchFailLogged;
        private static int _patchAttempts;
        private static int _debugLogCounter;

        private static GameObject _shieldRoot;
        private static Canvas _shieldCanvas;
        private static GraphicRaycaster _shieldRaycaster;

        /// <summary>
        /// Install Harmony patches early (Awake). Safe to call repeatedly.
        /// </summary>
        public static void EnsurePatchesInstalled()
        {
            if (_patchesInstalled)
            {
                return;
            }

            if (_patchAttempts >= 60)
            {
                return;
            }

            _patchAttempts++;
            try
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

                object harmony = harmonyCtor.Invoke(new object[] { "com.superexboom.dd2steammultiplayer.inputblocker" });
                MethodInfo patchMethod = FindHarmonyPatchMethod(harmonyType, harmonyMethodType);

                MethodInfo getPointerValues = typeof(InputSource_Unity).GetMethod(
                    "GetPointerValues",
                    BindingFlags.Public | BindingFlags.Instance);
                if (getPointerValues == null)
                {
                    throw new MissingMethodException("InputSource_Unity.GetPointerValues not found.");
                }

                MethodInfo postfix = typeof(UiInputBlocker).GetMethod(
                    nameof(GetPointerValuesPostfix),
                    BindingFlags.NonPublic | BindingFlags.Static);
                object postfixHarmonyMethod = harmonyMethodCtor.Invoke(new object[] { postfix });

                ParameterInfo[] parameters = patchMethod.GetParameters();
                object[] args = new object[parameters.Length];
                args[0] = getPointerValues;
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

                patchMethod.Invoke(harmony, args);
                _patchesInstalled = true;
                HostLog.Write("[input-blocker] Harmony postfix installed on InputSource_Unity.GetPointerValues.");
            }
            catch (Exception ex)
            {
                if (!_patchFailLogged)
                {
                    _patchFailLogged = true;
                    HostLog.Write("[input-blocker] Failed to install Harmony patches: " + ex);
                }
            }
        }

        /// <summary>
        /// Publish blocking rects from known window state (call from Update before gameplay
        /// polls, and again from OnGUI after windows move/resize).
        /// guiRects use GUI space (top-left origin, y-down).
        /// </summary>
        public static void SetGuiBlockingRects(IList<Rect> guiRects)
        {
            _guiRects.Clear();
            _screenRects.Clear();
            if (guiRects != null)
            {
                for (int i = 0; i < guiRects.Count; i++)
                {
                    Rect gui = guiRects[i];
                    if (gui.width <= 0f || gui.height <= 0f)
                    {
                        continue;
                    }

                    _guiRects.Add(gui);
                    _screenRects.Add(GuiToScreenRect(gui, 1f));
                }
            }

            SyncUguiShields();
        }

        public static void Clear()
        {
            _guiRects.Clear();
            _screenRects.Clear();
            SyncUguiShields();
        }

        public static void BeginFrame()
        {
            _guiRects.Clear();
            _screenRects.Clear();
        }

        public static void RegisterRect(Rect guiRect, float scaleFactor)
        {
            if (guiRect.width <= 0f || guiRect.height <= 0f)
            {
                return;
            }

            float scale = Mathf.Max(0.001f, scaleFactor);
            Rect scaled = new Rect(
                guiRect.x * scale,
                guiRect.y * scale,
                guiRect.width * scale,
                guiRect.height * scale);
            _guiRects.Add(scaled);
            _screenRects.Add(GuiToScreenRect(scaled, 1f));
        }

        public static void EndFrame()
        {
            SyncUguiShields();
        }

        /// <summary>
        /// Optional per-frame maintenance (shield survival across scene loads).
        /// </summary>
        public static void EarlyUpdate()
        {
            EnsurePatchesInstalled();
            if (_shieldRoot == null && _screenRects.Count > 0)
            {
                SyncUguiShields();
            }

            _debugLogCounter++;
            if (_debugLogCounter >= 300)
            {
                _debugLogCounter = 0;
                Vector3 mp = Input.mousePosition;
                HostLog.Write("[input-blocker] update: rects=" + _screenRects.Count +
                    " mouse=" + mp.x.ToString("F0") + "," + mp.y.ToString("F0") +
                    " blocking=" + IsMouseOverAnyWindow() +
                    " shield=" + (_shieldRoot != null && _shieldRoot.activeSelf) +
                    " es=" + (EventSystem.current != null ? EventSystem.current.GetType().Name : "[null]"));
            }
        }

        public static bool IsMouseOverAnyWindow()
        {
            if (_screenRects.Count == 0)
            {
                return false;
            }

            Vector2 mousePos = Input.mousePosition;
            return IsScreenPositionOverAnyWindow(mousePos);
        }

        private static void GetPointerValuesPostfix(ref InputPointerValues __result)
        {
            if (_screenRects.Count == 0)
            {
                return;
            }

            if (!IsScreenPositionOverAnyWindow(__result.m_position))
            {
                return;
            }

            InputPointerValues.ButtonValues none = new InputPointerValues.ButtonValues(false, false, false);
            __result = new InputPointerValues(__result.m_position, none, none, none);
        }

        private static bool IsScreenPositionOverAnyWindow(Vector2 screenPos)
        {
            for (int i = 0; i < _screenRects.Count; i++)
            {
                if (_screenRects[i].Contains(screenPos))
                {
                    return true;
                }
            }

            return false;
        }

        private static Rect GuiToScreenRect(Rect guiRect, float scaleFactor)
        {
            float scale = Mathf.Max(0.001f, scaleFactor);
            float screenX = guiRect.x * scale;
            float screenY = Screen.height - (guiRect.y + guiRect.height) * scale;
            float screenW = guiRect.width * scale;
            float screenH = guiRect.height * scale;
            return new Rect(screenX, screenY, screenW, screenH);
        }

        private static void SyncUguiShields()
        {
            try
            {
                if (_screenRects.Count == 0)
                {
                    if (_shieldRoot != null)
                    {
                        _shieldRoot.SetActive(false);
                    }

                    return;
                }

                EnsureShieldRoot();
                _shieldRoot.SetActive(true);

                while (_shieldImages.Count < _screenRects.Count)
                {
                    _shieldImages.Add(CreateShieldImage(_shieldImages.Count));
                }

                for (int i = 0; i < _shieldImages.Count; i++)
                {
                    Image image = _shieldImages[i];
                    if (image == null)
                    {
                        _shieldImages[i] = CreateShieldImage(i);
                        image = _shieldImages[i];
                    }

                    if (i < _screenRects.Count)
                    {
                        Rect screen = _screenRects[i];
                        image.gameObject.SetActive(true);
                        RectTransform rt = image.rectTransform;
                        rt.anchorMin = Vector2.zero;
                        rt.anchorMax = Vector2.zero;
                        rt.pivot = Vector2.zero;
                        rt.anchoredPosition = new Vector2(screen.x, screen.y);
                        rt.sizeDelta = new Vector2(screen.width, screen.height);
                    }
                    else
                    {
                        image.gameObject.SetActive(false);
                    }
                }
            }
            catch (Exception ex)
            {
                HostLog.Write("[input-blocker] UGUI shield sync failed: " + ex.Message);
            }
        }

        private static void EnsureShieldRoot()
        {
            if (_shieldRoot != null)
            {
                return;
            }

            _shieldRoot = new GameObject("DD2SteamMP_UiInputShield");
            UnityEngine.Object.DontDestroyOnLoad(_shieldRoot);
            _shieldCanvas = _shieldRoot.AddComponent<Canvas>();
            _shieldCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _shieldCanvas.sortingOrder = short.MaxValue;
            _shieldCanvas.pixelPerfect = false;
            _shieldRoot.AddComponent<CanvasScaler>();
            _shieldRaycaster = _shieldRoot.AddComponent<GraphicRaycaster>();
            _shieldRaycaster.ignoreReversedGraphics = true;
        }

        private static Image CreateShieldImage(int index)
        {
            EnsureShieldRoot();
            GameObject go = new GameObject("Shield_" + index, typeof(RectTransform));
            go.transform.SetParent(_shieldRoot.transform, false);
            Image image = go.AddComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0f);
            image.raycastTarget = true;
            image.maskable = false;
            return image;
        }

        private static MethodInfo FindHarmonyPatchMethod(Type harmonyType, Type harmonyMethodType)
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

                bool hasPostfix = parameters.Any(p =>
                    string.Equals(p.Name, "postfix", StringComparison.OrdinalIgnoreCase) &&
                    p.ParameterType == harmonyMethodType);
                if (hasPostfix)
                {
                    return method;
                }
            }

            throw new MissingMethodException("Could not find Harmony.Patch method with a 'postfix' parameter.");
        }

        private static Assembly LoadHarmonyAssembly()
        {
            Assembly loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "0Harmony", StringComparison.OrdinalIgnoreCase));
            if (loaded != null)
            {
                return loaded;
            }

            string harmonyPath = Path.Combine(HostPaths.GameRoot, "BepInEx", "core", "0Harmony.dll");
            if (!File.Exists(harmonyPath))
            {
                throw new FileNotFoundException("0Harmony.dll was not found in BepInEx core.", harmonyPath);
            }

            return Assembly.LoadFrom(harmonyPath);
        }
    }
}
