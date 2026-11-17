using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ChromeHypercube.AICapture
{
    /// <summary>One UI change declared as intended: which capture should differ, why, and optionally where.</summary>
    [Serializable]
    public class ExpectedChange
    {
        /// <summary>Capture name (matches name_pt, name_en...) or "*" for every capture.</summary>
        public string capture;
        public string description;
        public string declaredAt;
        /// <summary>When true the change is only expected inside region (pixels, origin top-left).</summary>
        public bool hasRegion;
        public RectInt region;
    }

    [Serializable]
    internal class ExpectedChangeFile
    {
        public List<ExpectedChange> changes = new List<ExpectedChange>();
    }

    /// <summary>
    /// Changes the author declared before running a scenario. A capture that differs the way it was declared is "awaiting
    /// approval" (a person looks at before/after and approves, which refreshes the golden) instead of a regression.
    /// Stored next to the goldens: AICaptureGoldens/&lt;scenarioId&gt;/expected_changes.json (commit it with the UI change).
    /// </summary>
    public static class ExpectedChanges
    {
        public static string PathFor(string scenarioId) =>
            Path.Combine(CaptureRunner.GoldenRoot, scenarioId, "expected_changes.json");

        public static List<ExpectedChange> Load(string scenarioId)
        {
            string path = PathFor(scenarioId);
            if (!File.Exists(path)) return new List<ExpectedChange>();
            try { return JsonUtility.FromJson<ExpectedChangeFile>(File.ReadAllText(path)).changes ?? new List<ExpectedChange>(); }
            catch (Exception e)
            {
                Debug.LogWarning($"[AICapture] Could not read {path}: {e.Message}");
                return new List<ExpectedChange>();
            }
        }

        private static void Save(string scenarioId, List<ExpectedChange> changes)
        {
            string path = PathFor(scenarioId);
            if (changes.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(new ExpectedChangeFile { changes = changes }, true));
        }

        /// <summary>Declares (or replaces) the expected change of one capture.</summary>
        public static void Declare(string scenarioId, string capture, string description, RectInt? region = null)
        {
            var changes = Load(scenarioId);
            changes.RemoveAll(c => c.capture == capture);
            changes.Add(new ExpectedChange
            {
                capture = capture,
                description = description,
                declaredAt = DateTime.Now.ToString("s"),
                hasRegion = region.HasValue,
                region = region ?? default
            });
            Save(scenarioId, changes);
        }

        /// <summary>Removes the declaration of one capture (all when capture is null). Called when its golden is approved.</summary>
        public static void Clear(string scenarioId, string capture = null)
        {
            var changes = Load(scenarioId);
            changes.RemoveAll(c => capture == null || c.capture == "*" || Matches(c.capture, capture));
            Save(scenarioId, changes);
        }

        public static bool Matches(string declared, string imageName) =>
            declared == "*" || imageName == declared || imageName.StartsWith(declared + "_");

        /// <summary>True when the diff is what a declared change describes (same capture, and inside its region when it has one).</summary>
        public static bool TryMatch(List<ExpectedChange> changes, VisualRegression.DiffReport diff, out ExpectedChange match)
        {
            match = null;
            foreach (var c in changes)
            {
                if (!Matches(c.capture, diff.imageName)) continue;
                if (c.hasRegion && !AllInside(diff.boundingBoxes, c.region)) continue;
                if (match == null || c.capture.Length > match.capture.Length) match = c;
            }
            return match != null;
        }

        private static bool AllInside(List<RectInt> boxes, RectInt region)
        {
            foreach (var b in boxes)
                if (b.xMin < region.xMin || b.yMin < region.yMin || b.xMax > region.xMax || b.yMax > region.yMax) return false;
            return true;
        }
    }
}
