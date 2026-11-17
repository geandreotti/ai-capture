using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ChromeHypercube.AICapture
{
    /// <summary>
    /// Generic actions every scenario can use without writing hooks: click, scroll and an "exists" condition.
    /// Targets are matched by hierarchy path or by its tail ("Canvas/Menu/Play" or just "Play").
    /// </summary>
    public static class BuiltInActions
    {
        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static bool Matches(Transform t, string target)
        {
            string path = PathOf(t);
            return path == target || path.EndsWith("/" + target);
        }

        private static T Find<T>(string target) where T : Component
        {
            if (string.IsNullOrEmpty(target)) throw new ArgumentException("A target name or path is required in args[0].");
            var hit = UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode.None)
                .Where(c => c.gameObject.activeInHierarchy && Matches(c.transform, target))
                .ToList();
            if (hit.Count == 0) throw new InvalidOperationException($"No active {typeof(T).Name} found for \"{target}\".");
            if (hit.Count > 1)
                throw new InvalidOperationException($"\"{target}\" is ambiguous ({hit.Count} matches); use a longer path.");
            return hit[0];
        }

        /// <summary>args[0] = path or name of an object with a click handler (Button, Toggle, custom IPointerClickHandler).</summary>
        [CaptureAction("click")]
        private static void Click(string[] args)
        {
            string target = args != null && args.Length > 0 ? args[0] : null;
            var matches = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)
                .Where(m => m is IPointerClickHandler && m.gameObject.activeInHierarchy && Matches(m.transform, target ?? ""))
                .Select(m => m.gameObject).Distinct().ToList();
            if (matches.Count == 0) throw new InvalidOperationException($"No clickable object found for \"{target}\".");
            if (matches.Count > 1) throw new InvalidOperationException($"\"{target}\" is ambiguous ({matches.Count} matches); use a longer path.");

            var data = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, clickCount = 1 };
            ExecuteEvents.Execute(matches[0], data, ExecuteEvents.pointerClickHandler);
        }

        /// <summary>args[0] = ScrollRect path or name, args[1] = vertical position 0 (bottom) to 1 (top).</summary>
        [CaptureAction("scroll")]
        private static void Scroll(string[] args)
        {
            var rect = Find<ScrollRect>(args != null && args.Length > 0 ? args[0] : null);
            float position = args != null && args.Length > 1 && float.TryParse(args[1],
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;
            rect.verticalNormalizedPosition = Mathf.Clamp01(position);
        }

        /// <summary>args[0] = path or name; true while an active object with that name exists.</summary>
        [CaptureCondition("exists")]
        private static bool Exists(string[] args)
        {
            string target = args != null && args.Length > 0 ? args[0] : "";
            return UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
                .Any(t => t.gameObject.activeInHierarchy && Matches(t, target));
        }
    }
}
