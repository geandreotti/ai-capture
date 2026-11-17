using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ChromeHypercube.AICapture
{
    /// <summary>
    /// Decides which groups of loose scene objects are worth turning into a prefab. Used by the scene summary and by
    /// the converter so both always agree.
    ///
    /// A group qualifies when it is not a root Canvas, has children and fits the size window
    /// (sceneCandidateThreshold .. sceneCandidateMaxSize). Groups bigger than the window are skipped so the search goes
    /// down to their children (a whole UI root is never a useful suggestion). A smaller group also qualifies when the
    /// same structure repeats among its siblings (cards, rows), which is what prefabs are made for.
    /// </summary>
    internal static class CandidateRule
    {
        public const int RepeatMinSize = 6;

        public static bool IsCandidate(Transform t, int loose, int repeats, bool isRootCanvas)
        {
            if (isRootCanvas || t.childCount == 0) return false;
            var settings = AICaptureSettings.instance;
            if (loose > settings.sceneCandidateMaxSize) return false;
            return loose >= settings.sceneCandidateThreshold || (repeats >= 2 && loose >= RepeatMinSize);
        }

        /// <summary>How many siblings (this one included) have the same structure as t.</summary>
        public static int RepeatCount(Transform t, Dictionary<Transform, string> cache)
        {
            if (t.parent == null) return 1;
            string mine = Signature(t, cache);
            int count = 0;
            foreach (Transform sibling in t.parent)
                if (Signature(sibling, cache) == mine) count++;
            return count;
        }

        // Structure only: component types in order plus the signatures of the children; names and values are ignored.
        private static string Signature(Transform t, Dictionary<Transform, string> cache)
        {
            if (cache.TryGetValue(t, out var known)) return known;

            var sb = new StringBuilder();
            if (PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject))
                sb.Append("P:").Append(PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject)?.name);
            else
            {
                foreach (var c in t.GetComponents<Component>())
                    if (c != null) sb.Append(c.GetType().Name).Append(',');
                sb.Append('{');
                foreach (Transform child in t) sb.Append(Signature(child, cache)).Append(';');
                sb.Append('}');
            }

            string sig = sb.ToString();
            cache[t] = sig;
            return sig;
        }
    }
}
