using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace ChromeHypercube.AICapture
{
    /// <summary>
    /// Ajusta a resolução da Game View por reflexão (API interna da Unity) e restaura depois.
    /// O estado sobrevive ao domain reload via SessionState.
    /// </summary>
    public static class CaptureSession
    {
        private const string CustomSizeName = "AI Capture";
        private const string KeyOriginalIndex = "AICapture.OriginalSizeIndex";
        private const string KeyAddedCustomIndex = "AICapture.AddedCustomIndex";
        private const string KeyLowRes = "AICapture.OriginalLowRes";
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        private static Assembly EditorAssembly => typeof(EditorWindow).Assembly;

        [InitializeOnLoadMethod]
        private static void Init()
        {
            ScenarioPlayer.ResolutionHandler = SetGameViewResolution;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
                RestoreGameViewResolution();
        }

        public static bool SetGameViewResolution(int width, int height)
        {
            try
            {
                var group = GetCurrentGroup();
                var groupType = group.GetType();
                var gameView = GetGameView();

                if (SessionState.GetInt(KeyOriginalIndex, -1) == -1)
                    SessionState.SetInt(KeyOriginalIndex, GetSelectedIndex(gameView));

                // Remove every size this package ever added (runs that were interrupted or never restored would otherwise
                // pile up in the Game View menu), then add a fresh one.
                RemoveCaptureSizes(group, groupType);

                int builtinCount = (int)groupType.GetMethod("GetBuiltinCount", Any).Invoke(group, null);
                int customCount = (int)groupType.GetMethod("GetCustomCount", Any).Invoke(group, null);

                var sizeType = EditorAssembly.GetType("UnityEditor.GameViewSize");
                var sizeTypeEnum = EditorAssembly.GetType("UnityEditor.GameViewSizeType");
                var fixedResolution = Enum.Parse(sizeTypeEnum, "FixedResolution");
                var ctor = sizeType.GetConstructor(new[] { sizeTypeEnum, typeof(int), typeof(int), typeof(string) });
                var newSize = ctor.Invoke(new object[] { fixedResolution, width, height, CustomSizeName });

                groupType.GetMethod("AddCustomSize", Any).Invoke(group, new[] { newSize });
                int addedIndex = customCount; // appended at the end of the custom list
                SessionState.SetInt(KeyAddedCustomIndex, addedIndex);

                SetSelectedIndex(gameView, builtinCount + addedIndex);
                ForceFullResolution(gameView);
                gameView.Repaint();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AICapture] Falha ao ajustar a Game View por reflexão: {ex.GetBaseException().Message}");
                return false;
            }
        }

        public static void RestoreGameViewResolution()
        {
            int original = SessionState.GetInt(KeyOriginalIndex, -1);
            int added = SessionState.GetInt(KeyAddedCustomIndex, -1);
            if (original == -1 && added == -1) return;

            try
            {
                var group = GetCurrentGroup();
                var groupType = group.GetType();
                var gameView = GetGameView();

                // Removing first: the package's sizes are always at the end, so the original index (a built-in or one of the
                // user's own sizes) keeps its meaning unless it pointed at a package size, which falls back to the first entry.
                RemoveCaptureSizes(group, groupType);
                int total = ((string[])groupType.GetMethod("GetDisplayTexts", Any).Invoke(group, null)).Length;
                SetSelectedIndex(gameView, original >= 0 && original < total ? original : 0);

                RestoreLowResolution(gameView);
                gameView.Repaint();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AICapture] Erro ao restaurar a Game View: {ex.GetBaseException().Message}");
            }
            finally
            {
                SessionState.EraseInt(KeyOriginalIndex);
                SessionState.EraseInt(KeyAddedCustomIndex);
            }
        }

        /// <summary>
        /// Removes every custom size named "AI Capture". Group.RemoveCustomSize takes the index in the WHOLE list (built-in sizes
        /// first), not in the custom list, so the global index is passed; walking from the end keeps the others valid.
        /// </summary>
        public static int RemoveCaptureSizes(object group, Type groupType)
        {
            int builtinCount = (int)groupType.GetMethod("GetBuiltinCount", Any).Invoke(group, null);
            var texts = (string[])groupType.GetMethod("GetDisplayTexts", Any).Invoke(group, null);
            var remove = groupType.GetMethod("RemoveCustomSize", Any);

            int removed = 0;
            for (int i = texts.Length - 1; i >= builtinCount; i--)
            {
                if (!texts[i].StartsWith(CustomSizeName)) continue;
                remove.Invoke(group, new object[] { i });
                removed++;
            }
            return removed;
        }

        [MenuItem("Tools/AI Capture/Clean Game View Sizes")]
        private static void MenuCleanSizes()
        {
            var group = GetCurrentGroup();
            int removed = RemoveCaptureSizes(group, group.GetType());
            Debug.Log($"[AICapture] {removed} tamanho(s) \"{CustomSizeName}\" removido(s) da Game View.");
        }

        // "Low Resolution Aspect Ratios" halves the capture; turn it off for the run and put it back after.
        private static void ForceFullResolution(EditorWindow gameView)
        {
            var prop = gameView.GetType().GetProperty("lowResolutionForAspectRatios", Any);
            if (prop == null || !prop.CanWrite) return; // shortcut: property missing on this Unity version, the old warning still covers it
            if (!SessionState.GetBool(KeyLowRes + ".Saved", false))
            {
                SessionState.SetBool(KeyLowRes, (bool)prop.GetValue(gameView, null));
                SessionState.SetBool(KeyLowRes + ".Saved", true);
            }
            prop.SetValue(gameView, false, null);
        }

        private static void RestoreLowResolution(EditorWindow gameView)
        {
            if (!SessionState.GetBool(KeyLowRes + ".Saved", false)) return;
            var prop = gameView.GetType().GetProperty("lowResolutionForAspectRatios", Any);
            prop?.SetValue(gameView, SessionState.GetBool(KeyLowRes, false), null);
            SessionState.EraseBool(KeyLowRes);
            SessionState.EraseBool(KeyLowRes + ".Saved");
        }

        private static object GetCurrentGroup()
        {
            var sizesType = EditorAssembly.GetType("UnityEditor.GameViewSizes");
            var singletonType = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
            var instance = singletonType.GetProperty("instance", Any).GetValue(null, null);
            return sizesType.GetProperty("currentGroup", Any).GetValue(instance, null);
        }

        private static EditorWindow GetGameView()
        {
            var gameViewType = EditorAssembly.GetType("UnityEditor.GameView");
            return EditorWindow.GetWindow(gameViewType, false, null, false);
        }

        private static int GetSelectedIndex(EditorWindow gameView)
        {
            var prop = gameView.GetType().GetProperty("selectedSizeIndex", Any);
            return (int)prop.GetValue(gameView, null);
        }

        private static void SetSelectedIndex(EditorWindow gameView, int index)
        {
            var type = gameView.GetType();
            var callback = type.GetMethod("SizeSelectionCallback", Any);
            if (callback != null)
                callback.Invoke(gameView, new object[] { index, null });
            else
                type.GetProperty("selectedSizeIndex", Any).SetValue(gameView, index, null);
        }
    }
}
