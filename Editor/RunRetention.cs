using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ChromeHypercube.AICapture
{
    /// <summary>
    /// Keeps %TEMP%/ai-capture small. Run outputs are only working copies: what matters is copied into the project
    /// (goldens in AICaptureGoldens/). Rules: per scenario keep the newest <see cref="KeepRuns"/> runs and drop older runs past
    /// <see cref="MaxAgeDays"/>; a run waiting for approval is kept (up to <see cref="PendingMaxAgeDays"/>); the last run is
    /// never touched; loose files and ad-hoc folders at the root go after <see cref="MaxAgeDays"/>.
    /// </summary>
    public static class RunRetention
    {
        public const int KeepRuns = 3;
        public const int MaxAgeDays = 7;
        public const int PendingMaxAgeDays = 30;

        private static readonly string[] KeepRootFiles = { "latest.txt" };

        /// <summary>Pure rule: which run folder names (yyyyMMdd_HHmmss_locale) of one scenario to delete.</summary>
        public static List<string> RunsToDelete(IEnumerable<string> runNames, DateTime now, Func<string, bool> isPending, string keep)
        {
            var runs = runNames.Where(n => RunTime(n).HasValue).OrderByDescending(n => n, StringComparer.Ordinal).ToList();
            var delete = new List<string>();
            for (int i = 0; i < runs.Count; i++)
            {
                string name = runs[i];
                if (name == keep) continue;
                double ageDays = (now - RunTime(name).Value).TotalDays;
                bool pending = isPending != null && isPending(name);
                if (pending ? ageDays > PendingMaxAgeDays : (i >= KeepRuns || ageDays > MaxAgeDays))
                    delete.Add(name);
            }
            return delete;
        }

        /// <summary>The run's start time from its folder name, or null when the name is not a run folder.</summary>
        public static DateTime? RunTime(string runName)
        {
            if (string.IsNullOrEmpty(runName) || runName.Length < 15) return null;
            return DateTime.TryParseExact(runName.Substring(0, 15), "yyyyMMdd_HHmmss", null,
                System.Globalization.DateTimeStyles.None, out var t) ? t : (DateTime?)null;
        }

        /// <summary>Applies the rules to <paramref name="root"/>. Never throws: cleanup must not break a capture run.</summary>
        public static void Prune(string root, string keepDir)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                var now = DateTime.Now;
                int removed = 0;
                string keepFull = string.IsNullOrEmpty(keepDir) ? "" : Path.GetFullPath(keepDir);

                foreach (var dir in Directory.GetDirectories(root))
                {
                    var runs = Directory.GetDirectories(dir).Select(Path.GetFileName).ToList();
                    if (runs.Any(r => RunTime(r).HasValue))
                    {
                        string keep = keepFull.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar) ? Path.GetFileName(keepFull) : null;
                        foreach (var name in RunsToDelete(runs, now, n => IsPending(Path.Combine(dir, n)), keep))
                        {
                            Directory.Delete(Path.Combine(dir, name), true);
                            removed++;
                        }
                    }
                    else if ((now - LastWrite(dir)).TotalDays > MaxAgeDays)
                    {
                        Directory.Delete(dir, true); // an ad-hoc folder some tool or agent dropped here
                        removed++;
                    }
                }

                foreach (var file in Directory.GetFiles(root))
                {
                    if (KeepRootFiles.Contains(Path.GetFileName(file))) continue;
                    if ((now - File.GetLastWriteTime(file)).TotalDays > MaxAgeDays)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }

                if (removed > 0) Debug.Log($"[AICapture] Limpeza: {removed} execução(ões)/arquivo(s) antigos removidos de {root}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AICapture] Limpeza da pasta de saída falhou: {e.Message}");
            }
        }

        /// <summary>Deletes one run folder after all its captures became goldens.</summary>
        public static void DeleteRun(string runDir)
        {
            try
            {
                if (Directory.Exists(runDir)) Directory.Delete(runDir, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AICapture] Não consegui apagar a execução aprovada {runDir}: {e.Message}");
            }
        }

        private static bool IsPending(string runDir)
        {
            string report = Path.Combine(runDir, "regression_report.json");
            return File.Exists(report) && File.ReadAllText(report).Contains("\"needs_approval\"");
        }

        private static DateTime LastWrite(string dir)
        {
            var latest = Directory.GetLastWriteTime(dir);
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var t = File.GetLastWriteTime(f);
                if (t > latest) latest = t;
            }
            return latest;
        }
    }
}
