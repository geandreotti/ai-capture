using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ChromeHypercube.AICapture
{
    /// <summary>
    /// Regressão visual: compara screenshots com Golden Images e gera overlay + relatório JSON.
    /// </summary>
    public static class VisualRegression
    {
        [Serializable]
        public class DiffReport
        {
            public string imageName;
            public string currentImagePath;
            public string goldenImagePath;
            public string diffImagePath;
            public bool isNewGolden;
            /// <summary>Strict mode: there is no golden for this capture, so it fails until approved.</summary>
            public bool missingGolden;
            public bool sizeMismatch;
            public bool hasDifferences;
            public int width;
            public int height;
            public int totalMismatchPixels;
            public float mismatchPercentage;
            /// <summary>Why this capture failed (empty when it passed).</summary>
            public string failReason;
            /// <summary>The capture differs the way a declared change says it should: a person must approve it (it is not a regression).</summary>
            public bool awaitingApproval;
            public string expectedDescription;
            /// <summary>Side-by-side strips (golden | current | diff) of the largest changed regions, small enough to read cheaply.</summary>
            public List<string> boxImages = new List<string>();
            /// <summary>Regiões alteradas em pixels, origem no canto SUPERIOR esquerdo.</summary>
            public List<RectInt> boundingBoxes = new List<RectInt>();
        }

        [Serializable]
        public class ScenarioReport
        {
            public string scenarioId;
            public string outputDir;
            public string goldenDir;
            public bool passed;
            public int compared;
            public int failed;
            public int newGoldens;
            public int missingGoldens;
            /// <summary>Captures that differ as declared and wait for approval.</summary>
            public int awaitingApproval;
            /// <summary>Declared changes whose capture came out identical to the golden (the change did not reach the screen, or is stale).</summary>
            public List<string> declaredButUnchanged = new List<string>();
            /// <summary>passed | needs_approval | failed</summary>
            public string status;
            public List<DiffReport> images = new List<DiffReport>();
        }

        public const string DiffSuffix = "_diff";

        /// <summary>Share of a large region's pixels that must differ before the region rule fails the capture.</summary>
        private const float MinRegionDensity = 0.05f;

        /// <summary>Settings for one comparison. Defaults match the old method signatures.</summary>
        public class CompareOptions
        {
            public float colorTolerance = 0.05f;
            public float maxMismatchPercent = 0.01f;
            /// <summary>Largest allowed changed region (box area in pixels). 0 = off.</summary>
            public int maxRegionPixels;
            /// <summary>When true a missing golden fails instead of being created.</summary>
            public bool strictGoldens;
            /// <summary>Rectangles ignored by the comparison, origin top-left.</summary>
            public List<RectInt> ignoreRegions = new List<RectInt>();
        }

        /// <summary>Options for one capture file: the scenario values plus the rule whose name matches best.</summary>
        public static CompareOptions OptionsFor(CaptureScenario scenario, string imageName)
        {
            var o = new CompareOptions
            {
                colorTolerance = scenario.colorTolerance,
                maxMismatchPercent = scenario.maxMismatchPercent,
                maxRegionPixels = scenario.maxRegionPixels,
                strictGoldens = scenario.strictGoldens
            };

            CaptureRule best = null;
            foreach (var rule in scenario.rules)
            {
                if (rule == null || string.IsNullOrEmpty(rule.captureName)) continue;
                bool match = imageName == rule.captureName || imageName.StartsWith(rule.captureName + "_");
                if (match && (best == null || rule.captureName.Length > best.captureName.Length)) best = rule;
            }
            if (best == null) return o;

            if (best.colorTolerance >= 0f) o.colorTolerance = best.colorTolerance;
            if (best.maxMismatchPercent >= 0f) o.maxMismatchPercent = best.maxMismatchPercent;
            if (best.maxRegionPixels >= 0) o.maxRegionPixels = best.maxRegionPixels;
            if (best.ignoreRegions != null) o.ignoreRegions = best.ignoreRegions;
            return o;
        }

        /// <summary>Compares a run using the scenario's settings and per-capture rules.</summary>
        public static ScenarioReport CompareScenario(CaptureScenario scenario, string outputDir, string goldenDir)
        {
            return CompareScenario(scenario.scenarioId, outputDir, goldenDir, name => OptionsFor(scenario, name));
        }

        /// <summary>Compara todos os PNGs de uma execução com a pasta de goldens do cenário.</summary>
        public static ScenarioReport CompareScenario(string scenarioId, string outputDir, string goldenDir,
            float colorTolerance = 0.05f, float maxMismatchPercent = 0.01f)
        {
            return CompareScenario(scenarioId, outputDir, goldenDir,
                name => new CompareOptions { colorTolerance = colorTolerance, maxMismatchPercent = maxMismatchPercent });
        }

        private static ScenarioReport CompareScenario(string scenarioId, string outputDir, string goldenDir,
            Func<string, CompareOptions> optionsFor)
        {
            var report = new ScenarioReport { scenarioId = scenarioId, outputDir = outputDir, goldenDir = goldenDir };
            var expected = ExpectedChanges.Load(scenarioId);

            foreach (var current in GetCaptureFiles(outputDir))
            {
                string golden = Path.Combine(goldenDir, Path.GetFileName(current));
                var diff = CompareImages(current, golden, outputDir,
                    optionsFor(Path.GetFileNameWithoutExtension(current)));
                report.images.Add(diff);
                if (diff.isNewGolden) report.newGoldens++;
                else if (diff.missingGolden) report.missingGoldens++;
                else report.compared++;

                if (!diff.isNewGolden && !diff.missingGolden && (diff.hasDifferences || diff.boundingBoxes.Count > 0) &&
                    ExpectedChanges.TryMatch(expected, diff, out var declared))
                {
                    diff.awaitingApproval = true;
                    diff.expectedDescription = declared.description;
                    report.awaitingApproval++;
                }
                else if (diff.hasDifferences) report.failed++;
            }

            foreach (var change in expected)
                if (!report.images.Exists(i => i.awaitingApproval && ExpectedChanges.Matches(change.capture, i.imageName)))
                    report.declaredButUnchanged.Add(change.capture);

            report.passed = report.failed == 0 && report.awaitingApproval == 0;
            report.status = report.failed > 0 ? "failed" : (report.awaitingApproval > 0 ? "needs_approval" : "passed");
            WriteMarkdownReport(report);
            File.WriteAllText(Path.Combine(outputDir, "regression_report.json"), JsonUtility.ToJson(report, true));
            return report;
        }

        /// <summary>
        /// Compara uma imagem com a golden. Se a golden não existir, a imagem atual vira a golden.
        /// colorTolerance: diferença por canal (0–1). maxMismatchPercent: fração de pixels (0.01 = 1%).
        /// </summary>
        public static DiffReport CompareImages(string currentImagePath, string goldenImagePath, string outputDir,
            float colorTolerance = 0.05f, float maxMismatchPercent = 0.01f)
        {
            return CompareImages(currentImagePath, goldenImagePath, outputDir,
                new CompareOptions { colorTolerance = colorTolerance, maxMismatchPercent = maxMismatchPercent });
        }

        public static DiffReport CompareImages(string currentImagePath, string goldenImagePath, string outputDir, CompareOptions options)
        {
            float colorTolerance = options.colorTolerance;
            float maxMismatchPercent = options.maxMismatchPercent;
            var report = new DiffReport
            {
                imageName = Path.GetFileNameWithoutExtension(currentImagePath),
                currentImagePath = currentImagePath,
                goldenImagePath = goldenImagePath
            };

            if (!File.Exists(goldenImagePath) && options.strictGoldens)
            {
                report.missingGolden = true;
                report.hasDifferences = true;
                report.failReason = "no golden for this capture (approve the run with Accept Last Run as Golden)";
                return report;
            }

            if (!File.Exists(goldenImagePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(goldenImagePath));
                File.Copy(currentImagePath, goldenImagePath, true);
                report.isNewGolden = true;
                return report;
            }

            var currentTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var goldenTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Texture2D diffTex = null;

            try
            {
                currentTex.LoadImage(File.ReadAllBytes(currentImagePath));
                goldenTex.LoadImage(File.ReadAllBytes(goldenImagePath));

                int width = currentTex.width;
                int height = currentTex.height;
                report.width = width;
                report.height = height;

                if (goldenTex.width != width || goldenTex.height != height)
                {
                    report.sizeMismatch = true;
                    report.hasDifferences = true;
                    report.totalMismatchPixels = width * height;
                    report.mismatchPercentage = 1f;
                    report.failReason = $"size {width}x{height} differs from the golden {goldenTex.width}x{goldenTex.height}";
                    Debug.LogWarning($"[AICapture] {report.imageName}: tamanho {width}x{height} difere da golden {goldenTex.width}x{goldenTex.height}.");
                    return report;
                }

                Color32[] currentPixels = currentTex.GetPixels32();
                Color32[] goldenPixels = goldenTex.GetPixels32();
                var diffPixels = new Color32[currentPixels.Length];
                var mask = new bool[currentPixels.Length];
                var ignored = BuildIgnoreMask(options.ignoreRegions, width, height);
                int tolerance = Mathf.RoundToInt(Mathf.Clamp01(colorTolerance) * 255f);
                int mismatchCount = 0;

                for (int i = 0; i < currentPixels.Length; i++)
                {
                    Color32 c = currentPixels[i];
                    Color32 g = goldenPixels[i];

                    if (ignored != null && ignored[i])
                    {
                        // Ignored (live) area: drawn blue so it is clear it was left out.
                        diffPixels[i] = new Color32(20, 40, 110, 255);
                        continue;
                    }

                    bool different = Math.Abs(c.r - g.r) > tolerance || Math.Abs(c.g - g.g) > tolerance ||
                                     Math.Abs(c.b - g.b) > tolerance || Math.Abs(c.a - g.a) > tolerance;
                    if (different)
                    {
                        mismatchCount++;
                        mask[i] = true;
                        diffPixels[i] = new Color32(255, 0, 0, 255);
                    }
                    else
                    {
                        byte dark = (byte)((g.r + g.g + g.b) / 3 * 0.3f);
                        diffPixels[i] = new Color32(dark, dark, dark, 255);
                    }
                }

                report.totalMismatchPixels = mismatchCount;
                report.mismatchPercentage = (float)mismatchCount / (width * height);
                report.hasDifferences = report.mismatchPercentage > maxMismatchPercent;
                if (report.hasDifferences)
                    report.failReason = $"{report.mismatchPercentage:P2} of the pixels changed (limit {maxMismatchPercent:P2})";

                if (mismatchCount > 0)
                {
                    var boxesBottomLeft = ComputeBoundingBoxes(mask, width, height);
                    foreach (var box in boxesBottomLeft)
                    {
                        DrawRectOutline(diffPixels, width, height, box, new Color32(255, 220, 0, 255));
                        // Converte para origem no topo (padrão de imagens / agentes de IA).
                        report.boundingBoxes.Add(new RectInt(box.x, height - box.yMax, box.width, box.height));
                    }

                    if (options.maxRegionPixels > 0)
                    {
                        // A wide box with a few stray pixels (antialiasing on glyph edges) is noise; a real change fills its box.
                        for (int b = 0; b < boxesBottomLeft.Count; b++)
                        {
                            var box = boxesBottomLeft[b];
                            int area = box.width * box.height;
                            if (area <= options.maxRegionPixels) continue;

                            int changed = 0;
                            for (int y = box.yMin; y < box.yMax; y++)
                                for (int x = box.xMin; x < box.xMax; x++)
                                    if (mask[y * width + x]) changed++;

                            if (changed < area * MinRegionDensity) continue;
                            var top = report.boundingBoxes[b];
                            report.hasDifferences = true;
                            if (string.IsNullOrEmpty(report.failReason))
                                report.failReason = $"changed region {top.width}x{top.height} at ({top.x},{top.y}) is larger than {options.maxRegionPixels} px " +
                                                    $"and {(float)changed / area:P0} of it changed";
                            break;
                        }
                    }

                    for (int b = 0; b < Math.Min(4, boxesBottomLeft.Count); b++)
                    {
                        string strip = Path.Combine(outputDir, $"{report.imageName}_box{b}.png");
                        if (WriteBoxStrip(goldenPixels, currentPixels, diffPixels, width, height, boxesBottomLeft[b], strip))
                            report.boxImages.Add(strip);
                    }

                    Directory.CreateDirectory(outputDir);
                    diffTex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    diffTex.SetPixels32(diffPixels);
                    diffTex.Apply();
                    string diffPath = Path.Combine(outputDir, report.imageName + DiffSuffix + ".png");
                    File.WriteAllBytes(diffPath, diffTex.EncodeToPNG());
                    report.diffImagePath = diffPath;
                }

                return report;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(currentTex);
                UnityEngine.Object.DestroyImmediate(goldenTex);
                if (diffTex != null) UnityEngine.Object.DestroyImmediate(diffTex);
            }
        }

        /// <summary>Writes golden | current | diff of one region (box in bottom-left coordinates) as a single small PNG.</summary>
        private static bool WriteBoxStrip(Color32[] golden, Color32[] current, Color32[] diff, int width, int height, RectInt box, string path)
        {
            const int pad = 16, maxSide = 480, gap = 4;
            int x0 = Mathf.Max(0, box.xMin - pad), x1 = Mathf.Min(width, box.xMax + pad);
            int y0 = Mathf.Max(0, box.yMin - pad), y1 = Mathf.Min(height, box.yMax + pad);
            int w = x1 - x0, h = y1 - y0;
            int step = Mathf.Max(1, (Mathf.Max(w, h) + maxSide - 1) / maxSide);
            int sw = w / step, sh = h / step;
            if (sw < 1 || sh < 1) return false;

            int total = sw * 3 + gap * 2;
            var strip = new Color32[total * sh];
            for (int i = 0; i < strip.Length; i++) strip[i] = new Color32(255, 255, 255, 255);

            var panels = new[] { golden, current, diff };
            for (int p = 0; p < 3; p++)
                for (int y = 0; y < sh; y++)
                    for (int x = 0; x < sw; x++)
                        strip[y * total + p * (sw + gap) + x] = panels[p][(y0 + y * step) * width + x0 + x * step];

            var tex = new Texture2D(total, sh, TextureFormat.RGBA32, false);
            try
            {
                tex.SetPixels32(strip);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
                return true;
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        private static void WriteMarkdownReport(ScenarioReport report)
        {
            var sb = new System.Text.StringBuilder();
            int missing = report.missingGoldens;
            sb.AppendLine($"# Visual regression: {report.scenarioId} - {report.status.ToUpperInvariant().Replace('_', ' ')}");
            sb.AppendLine($"{report.compared} compared, {report.failed} failed, {report.awaitingApproval} awaiting approval (declared changes), " +
                          $"{missing} without golden, {report.newGoldens} new golden(s).");
            if (report.declaredButUnchanged.Count > 0)
                sb.AppendLine($"Declared but unchanged: {string.Join(", ", report.declaredButUnchanged)} (the change did not reach these captures, or the declaration is stale).");
            sb.AppendLine();
            sb.AppendLine("| capture | result | changed pixels | regions |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var img in report.images)
            {
                string result = img.awaitingApproval ? "AWAITING APPROVAL" : img.hasDifferences ? "FAILED" : (img.isNewGolden ? "new golden" : (img.boundingBoxes.Count > 0 ? "passed (has changes)" : "identical"));
                sb.AppendLine($"| {img.imageName} | {result} | {img.mismatchPercentage:P2} | {img.boundingBoxes.Count} |");
            }

            foreach (var img in report.images.Where(i => i.hasDifferences || i.boundingBoxes.Count > 0))
            {
                sb.AppendLine();
                sb.AppendLine($"## {img.imageName}");
                if (img.awaitingApproval) sb.AppendLine($"Declared change: {img.expectedDescription}");
                else if (!string.IsNullOrEmpty(img.failReason)) sb.AppendLine($"Reason: {img.failReason}");
                sb.AppendLine("Each region strip shows golden | current | diff. Boxes are pixels, origin top-left.");
                for (int i = 0; i < img.boundingBoxes.Count && i < 8; i++)
                {
                    var b = img.boundingBoxes[i];
                    sb.AppendLine($"- region {i}: x={b.x} y={b.y} {b.width}x{b.height}" +
                                  (i < img.boxImages.Count ? $" -> {Path.GetFileName(img.boxImages[i])}" : ""));
                }
                if (!string.IsNullOrEmpty(img.diffImagePath)) sb.AppendLine($"Full diff: {Path.GetFileName(img.diffImagePath)}");
            }

            File.WriteAllText(Path.Combine(report.outputDir, "report.md"), sb.ToString());
        }

        /// <summary>Marks the pixels of the ignored rectangles (given top-left, the pixel array is bottom-left). Null when there are none.</summary>
        private static bool[] BuildIgnoreMask(List<RectInt> regions, int width, int height)
        {
            if (regions == null || regions.Count == 0) return null;
            var skip = new bool[width * height];
            foreach (var r in regions)
            {
                int x0 = Mathf.Clamp(r.xMin, 0, width), x1 = Mathf.Clamp(r.xMax, 0, width);
                int y0 = Mathf.Clamp(height - r.yMax, 0, height), y1 = Mathf.Clamp(height - r.yMin, 0, height);
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++) skip[y * width + x] = true;
            }
            return skip;
        }

        /// <summary>False for the files the comparison writes next to the captures (diff overlays and region strips).</summary>
        private static bool IsCaptureName(string name) =>
            !name.EndsWith(DiffSuffix) && !System.Text.RegularExpressions.Regex.IsMatch(name, @"_box\d+$");

        /// <summary>Lista os screenshots de uma execução (ignora overlays de diff).</summary>
        public static IEnumerable<string> GetCaptureFiles(string outputDir)
        {
            if (!Directory.Exists(outputDir)) return Enumerable.Empty<string>();
            return Directory.GetFiles(outputDir, "*.png")
                .Where(p => IsCaptureName(Path.GetFileNameWithoutExtension(p)))
                .OrderBy(p => p);
        }

        /// <summary>Agrupa pixels diferentes em células e une células vizinhas em retângulos.</summary>
        private static List<RectInt> ComputeBoundingBoxes(bool[] mask, int width, int height, int cell = 16, int maxBoxes = 50)
        {
            int cw = (width + cell - 1) / cell;
            int ch = (height + cell - 1) / cell;
            var cells = new bool[cw * ch];

            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                int cellRow = (y / cell) * cw;
                for (int x = 0; x < width; x++)
                    if (mask[row + x]) cells[cellRow + x / cell] = true;
            }

            var visited = new bool[cells.Length];
            var boxes = new List<RectInt>();
            var stack = new Stack<int>();

            for (int start = 0; start < cells.Length; start++)
            {
                if (!cells[start] || visited[start]) continue;

                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                stack.Push(start);
                visited[start] = true;

                while (stack.Count > 0)
                {
                    int idx = stack.Pop();
                    int cx = idx % cw, cy = idx / cw;
                    minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx);
                    minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);

                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= cw || ny >= ch) continue;
                        int n = ny * cw + nx;
                        if (cells[n] && !visited[n]) { visited[n] = true; stack.Push(n); }
                    }
                }

                int px = minX * cell, py = minY * cell;
                int pw = Math.Min((maxX + 1) * cell, width) - px;
                int ph = Math.Min((maxY + 1) * cell, height) - py;
                boxes.Add(new RectInt(px, py, pw, ph));
            }

            return boxes.OrderByDescending(b => b.width * b.height).Take(maxBoxes).ToList();
        }

        private static void DrawRectOutline(Color32[] pixels, int width, int height, RectInt r, Color32 color)
        {
            for (int t = 0; t < 2; t++)
            {
                int x0 = Mathf.Clamp(r.xMin + t, 0, width - 1), x1 = Mathf.Clamp(r.xMax - 1 - t, 0, width - 1);
                int y0 = Mathf.Clamp(r.yMin + t, 0, height - 1), y1 = Mathf.Clamp(r.yMax - 1 - t, 0, height - 1);
                for (int x = x0; x <= x1; x++) { pixels[y0 * width + x] = color; pixels[y1 * width + x] = color; }
                for (int y = y0; y <= y1; y++) { pixels[y * width + x0] = color; pixels[y * width + x1] = color; }
            }
        }
    }
}
