using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ChromeHypercube.AICapture
{
    [Serializable]
    public class CaptureResult
    {
        public string scenarioId;
        public string initialLocale;
        public string outputDir;
        public string startedAt;
        public string finishedAt;
        public bool success;
        public List<string> captures = new List<string>();
        public List<string> errors = new List<string>();
    }

    /// <summary>
    /// Runs the steps of a CaptureScenario in Play Mode as a coroutine.
    /// Usually started by CaptureRunner (Editor), but it can be called directly.
    /// </summary>
    public class ScenarioPlayer : MonoBehaviour
    {
        /// <summary>
        /// Resolution handler (the Editor registers one that resizes the Game View).
        /// Returns true if the resolution was applied.
        /// </summary>
        public static Func<int, int, bool> ResolutionHandler;

        public static bool IsRunning { get; private set; }

        // Enter Play Mode Options with domain reload off keep statics between sessions.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => IsRunning = false;

        public static ScenarioPlayer Run(CaptureScenario scenario, string locale, string outputDir, Action<CaptureResult> onComplete)
        {
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));
            if (!Application.isPlaying) throw new InvalidOperationException("ScenarioPlayer only runs in Play Mode.");

            var go = new GameObject("[AICapture] ScenarioPlayer");
            DontDestroyOnLoad(go);
            var player = go.AddComponent<ScenarioPlayer>();
            player.StartCoroutine(player.Execute(scenario, locale, outputDir, onComplete));
            return player;
        }

        private IEnumerator Execute(CaptureScenario scenario, string locale, string outputDir, Action<CaptureResult> onComplete)
        {
            IsRunning = true;
            float originalTimeScale = Time.timeScale;
            locale = string.IsNullOrEmpty(locale) ? scenario.defaultLocale : locale;

            var result = new CaptureResult
            {
                scenarioId = scenario.scenarioId,
                initialLocale = locale,
                outputDir = outputDir,
                startedAt = DateTime.Now.ToString("o")
            };
            Directory.CreateDirectory(outputDir);
            CaptureHooks.Refresh();

            // Let the scene finish initializing.
            yield return null;
            yield return null;

            if (scenario.targetWidth > 0 && scenario.targetHeight > 0)
                yield return ApplyResolution(scenario.targetWidth, scenario.targetHeight, result);

            if (CaptureHooks.HasAction("set_locale"))
                yield return RunAction("set_locale", new[] { locale }, result);

            for (int i = 0; i < scenario.steps.Count; i++)
            {
                var step = scenario.steps[i];
                if (step == null) continue;
                string label = $"Step {i} ({step.stepType})";

                switch (step.stepType)
                {
                    case CaptureStepType.Wait:
                        yield return new WaitForSecondsRealtime(Mathf.Max(0f, step.floatValue));
                        break;

                    case CaptureStepType.WaitFrames:
                        for (int f = 0; f < Mathf.Max(1, step.intValue); f++) yield return null;
                        break;

                    case CaptureStepType.WaitUntil:
                    {
                        float timeout = step.floatValue > 0f ? step.floatValue : 10f;
                        float start = Time.realtimeSinceStartup;
                        bool satisfied = false;
                        while (Time.realtimeSinceStartup - start < timeout)
                        {
                            if (!TryEvaluate(step.targetName, step.args, result, label, out bool value)) break;
                            if (value) { satisfied = true; break; }
                            yield return null;
                        }
                        if (!satisfied)
                            result.errors.Add($"{label}: condition \"{step.targetName}\" was not true within {timeout}s.");
                        break;
                    }

                    case CaptureStepType.Capture:
                    {
                        yield return WaitForCaptureReady(label);
                        yield return new WaitForEndOfFrame();
                        string baseName = string.IsNullOrEmpty(step.targetName) ? $"capture_{i:D2}" : Sanitize(step.targetName);
                        string path = Path.Combine(outputDir, $"{baseName}_{Sanitize(locale)}.png");
                        TakeScreenshot(path, result, label);
                        break;
                    }

                    case CaptureStepType.SetLocale:
                        if (string.IsNullOrEmpty(step.targetName))
                        {
                            result.errors.Add($"{label}: targetName is empty (set the locale, e.g. \"pt-BR\").");
                            break;
                        }
                        locale = step.targetName;
                        if (CaptureHooks.HasAction("set_locale"))
                            yield return RunAction("set_locale", new[] { locale }, result);
                        else
                            result.errors.Add($"{label}: no [CaptureAction(\"set_locale\")] found; only the file names changed.");
                        yield return null;
                        break;

                    case CaptureStepType.SetResolution:
                        yield return ApplyResolution(step.intValue, step.extraIntValue, result);
                        break;

                    case CaptureStepType.SetTimeScale:
                        Time.timeScale = Mathf.Max(0f, step.floatValue);
                        break;

                    case CaptureStepType.InvokeAction:
                        yield return RunAction(step.targetName, step.args, result);
                        break;

                    case CaptureStepType.WaitForStable:
                        yield return WaitForStable(step.floatValue > 0f ? step.floatValue : 5f, label);
                        break;
                }
            }

            Time.timeScale = originalTimeScale;
            result.finishedAt = DateTime.Now.ToString("o");
            result.success = result.errors.Count == 0;

            try
            {
                File.WriteAllText(Path.Combine(outputDir, "manifest.json"), JsonUtility.ToJson(result, true));
            }
            catch (Exception e)
            {
                Debug.LogError($"[AICapture] Failed to write manifest.json: {e.Message}");
            }

            IsRunning = false;
            try { onComplete?.Invoke(result); }
            catch (Exception e) { Debug.LogException(e); }

            Destroy(gameObject);
        }

        /// <summary>
        /// Before every capture, waits for the project's optional [CaptureCondition("capture_ready")] (e.g. "no screen is
        /// mid-fade"). Without that condition nothing is waited for. A timeout only warns, so a capture is never lost.
        /// </summary>
        private IEnumerator WaitForCaptureReady(string label)
        {
            if (!CaptureHooks.HasCondition("capture_ready")) yield break;

            const float timeout = 5f;
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < timeout)
            {
                bool ready;
                try { ready = CaptureHooks.EvaluateCondition("capture_ready", null); }
                catch (Exception e) { Debug.LogWarning($"[AICapture] {label}: capture_ready failed: {e.Message}"); yield break; }
                if (ready) yield break;
                yield return null;
            }
            Debug.LogWarning($"[AICapture] {label}: capture_ready was still false after {timeout}s; capturing anyway.");
        }

        // shortcut: grabs the full frame ~8x/s and compares a sample of pixels; fine for a few seconds of waiting, not for long runs.
        private IEnumerator WaitForStable(float timeout, string label)
        {
            float start = Time.realtimeSinceStartup;
            Color32[] previous = null;
            int calm = 0;
            while (Time.realtimeSinceStartup - start < timeout)
            {
                yield return new WaitForEndOfFrame();
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                var pixels = tex.GetPixels32();
                Destroy(tex);

                if (previous != null && previous.Length == pixels.Length && ChangedFraction(previous, pixels) < 0.0005f)
                {
                    if (++calm >= 2) yield break;
                }
                else calm = 0;

                previous = pixels;
                yield return new WaitForSecondsRealtime(0.1f);
            }
            Debug.LogWarning($"[AICapture] {label}: the screen did not settle within {timeout}s (live content?). Capturing anyway.");
        }

        private static float ChangedFraction(Color32[] a, Color32[] b)
        {
            const int stride = 7, tolerance = 8;
            int samples = 0, changed = 0;
            for (int i = 0; i < a.Length; i += stride)
            {
                samples++;
                if (Math.Abs(a[i].r - b[i].r) > tolerance || Math.Abs(a[i].g - b[i].g) > tolerance ||
                    Math.Abs(a[i].b - b[i].b) > tolerance) changed++;
            }
            return samples == 0 ? 0f : (float)changed / samples;
        }

        private IEnumerator ApplyResolution(int width, int height, CaptureResult result)
        {
            if (width <= 0 || height <= 0)
            {
                result.errors.Add($"Invalid SetResolution: {width}x{height}.");
                yield break;
            }
            if (ResolutionHandler == null)
            {
                result.errors.Add("No ResolutionHandler registered (resolution is not changed outside the Editor).");
                yield break;
            }

            bool ok;
            try { ok = ResolutionHandler(width, height); }
            catch (Exception e) { ok = false; result.errors.Add($"Error changing resolution: {e.Message}"); }

            if (!ok) result.errors.Add($"Could not apply resolution {width}x{height}.");

            // The Game View and UI layout need a few frames to react.
            for (int f = 0; f < 3; f++) yield return null;
            yield return new WaitForEndOfFrame();
            if (Screen.width != width || Screen.height != height)
                Debug.LogWarning($"[AICapture] Requested {width}x{height}, but Screen reports {Screen.width}x{Screen.height}. " +
                                 "Uncheck 'Low Resolution Aspect Ratios' and keep the Game View visible.");
        }

        private IEnumerator RunAction(string id, string[] args, CaptureResult result)
        {
            object returned = null;
            try
            {
                returned = CaptureHooks.InvokeAction(id, args);
            }
            catch (Exception e)
            {
                result.errors.Add($"Action \"{id}\": {e.Message}");
            }

            if (returned is IEnumerator routine)
                yield return routine;
        }

        private static bool TryEvaluate(string id, string[] args, CaptureResult result, string label, out bool value)
        {
            value = false;
            try
            {
                value = CaptureHooks.EvaluateCondition(id, args);
                return true;
            }
            catch (Exception e)
            {
                result.errors.Add($"{label}: condition \"{id}\": {e.Message}");
                return false;
            }
        }

        private static void TakeScreenshot(string path, CaptureResult result, string label)
        {
            Texture2D tex = null;
            try
            {
                tex = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(path, tex.EncodeToPNG());
                result.captures.Add(path);
            }
            catch (Exception e)
            {
                result.errors.Add($"{label}: screenshot failed: {e.Message}");
            }
            finally
            {
                if (tex != null) Destroy(tex);
            }
        }

        private static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }

        private void OnDestroy()
        {
            IsRunning = false;
        }
    }
}
