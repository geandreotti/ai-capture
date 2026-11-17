using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ChromeHypercube.AICapture
{
    /// <summary>
    /// Gera uma pasta de contexto para agentes de IA:
    ///   AIContext/index.txt            — uma linha por prefab e por cena
    ///   AIContext/prefabs/&lt;caminho&gt;.txt — descrição compacta de cada prefab
    ///   AIContext/scenes/&lt;caminho&gt;.txt  — resumo de cada cena + candidatos a virar prefab
    ///   AIContext/README.md            — instruções para o agente
    /// Assim a IA lê só o que precisa, sem abrir o YAML inteiro da cena.
    /// </summary>
    public static class AIContextExporter
    {
        private const string PrefabsSub = "prefabs";
        private const string ScenesSub = "scenes";

        private static AICaptureSettings Settings => AICaptureSettings.instance;

        public static string Root => Settings.ContextRoot;
        public static string IndexPath => Path.Combine(Root, "index.txt");
        public static bool HasContext => File.Exists(IndexPath);

        // ---------------- Prefabs ----------------

        /// <summary>Caminho do .txt para um prefab: Assets/UI/Menu.prefab → AIContext/prefabs/UI/Menu.txt</summary>
        public static string GetPrefabDescriptionPath(string prefabAssetPath)
            => Path.Combine(Root, PrefabsSub, ToRelativeTxt(prefabAssetPath));

        public static bool ExportPrefab(string assetPath)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (go == null) return false;

            var options = new DescribeOptions
            {
                maxDepth = Settings.maxDepth,
                includeInactive = true,   // menus costumam ficar desativados no prefab
                liveLayout = false,       // asset não renderizado: overflow não é confiável
                listScripts = true,
                stopAtNestedPrefabs = Settings.stopAtNestedPrefabs
            };

            string body = DescribeUITool.Describe(go, options, out var stats);
            var sb = new StringBuilder();
            sb.AppendLine(BuildPrefabHeader(assetPath, go, stats));
            sb.AppendLine($"guid: {AssetDatabase.AssetPathToGUID(assetPath)}");
            sb.AppendLine();
            sb.Append(body);

            WriteIfChanged(GetPrefabDescriptionPath(assetPath), sb.ToString());
            return true;
        }

        public static void DeletePrefabDescription(string assetPath)
        {
            string path = GetPrefabDescriptionPath(assetPath);
            if (File.Exists(path)) File.Delete(path);
        }

        private static string BuildPrefabHeader(string assetPath, GameObject go, DescribeStats s)
        {
            var parts = new List<string> { assetPath, s.isUI ? "UI" : "objeto", $"{s.nodeCount} nós" };

            if (PrefabUtility.GetPrefabAssetType(go) == PrefabAssetType.Variant)
            {
                var basePrefab = PrefabUtility.GetCorrespondingObjectFromSource(go);
                if (basePrefab != null) parts.Add("variante de " + AssetDatabase.GetAssetPath(basePrefab));
            }
            if (s.scripts.Count > 0) parts.Add("scripts: " + Limit(s.scripts, 6));
            if (s.nestedPrefabs.Count > 0) parts.Add("usa: " + Limit(s.nestedPrefabs.Select(Path.GetFileNameWithoutExtension), 5));
            if (s.missingScripts > 0) parts.Add($"⚠ {s.missingScripts} script(s) faltando");
            if (s.truncated) parts.Add("truncado");

            return "# " + string.Join(" | ", parts);
        }

        // ---------------- Cenas ----------------

        public static string GetSceneDescriptionPath(Scene scene)
        {
            string rel = string.IsNullOrEmpty(scene.path) ? $"Untitled_{scene.handle}.unity" : scene.path;
            return Path.Combine(Root, ScenesSub, ToRelativeTxt(rel));
        }

        /// <summary>Resume uma cena: prefabs usados, objetos soltos e quais grupos deveriam virar prefab.</summary>
        public static void ExportScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;

            var roots = scene.GetRootGameObjects();
            var looseCount = new Dictionary<Transform, int>();
            foreach (var r in roots) CountLoose(r.transform, looseCount);

            var ctx = new SceneContext { looseCount = looseCount };
            var body = new StringBuilder();
            foreach (var r in roots) DescribeSceneNode(r.transform, 0, false, ctx, body);

            var sb = new StringBuilder();
            string name = string.IsNullOrEmpty(scene.path) ? scene.name : scene.path;
            sb.AppendLine($"# {name} | cena | {ctx.instances} instância(s) de prefab | {ctx.loose} objeto(s) soltos | " +
                          $"{ctx.candidates.Count} candidato(s) a prefab" +
                          (ctx.overriddenInstances > 0 ? $" | {ctx.overriddenInstances} instância(s) com overrides" : ""));
            sb.AppendLine();

            if (ctx.candidates.Count > 0)
            {
                sb.AppendLine("## Candidatos a prefab (grupos grandes de objetos fora de prefabs)");
                foreach (var c in ctx.candidates.OrderByDescending(c => c.count))
                    sb.AppendLine($"- {c.path} ({c.count} objetos{(c.isUI ? ", UI" : "")})");
                sb.AppendLine();
            }

            sb.AppendLine("## Hierarquia");
            sb.AppendLine("(conteúdo de [Prefab: ...] fica no arquivo do próprio prefab)");
            sb.Append(body);

            WriteIfChanged(GetSceneDescriptionPath(scene), sb.ToString());
        }

        public static void ExportOpenScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                ExportScene(SceneManager.GetSceneAt(i));
        }

        private class Candidate { public string path; public int count; public int repeats; public bool isUI; }

        private class SceneContext
        {
            public Dictionary<Transform, int> looseCount;
            public Dictionary<Transform, string> signatures = new Dictionary<Transform, string>();
            public List<Candidate> candidates = new List<Candidate>();
            public int instances, loose, overriddenInstances;
        }

        /// <summary>Conta objetos fora de instâncias de prefab em cada subárvore.</summary>
        private static int CountLoose(Transform t, Dictionary<Transform, int> cache)
        {
            if (PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) { cache[t] = 0; return 0; }
            int count = 1;
            foreach (Transform child in t) count += CountLoose(child, cache);
            cache[t] = count;
            return count;
        }

        private static void DescribeSceneNode(Transform t, int depth, bool insideCandidate, SceneContext ctx, StringBuilder sb)
        {
            var go = t.gameObject;
            sb.Append(' ', depth * 2).Append("- ").Append(go.name);
            if (!go.activeSelf) sb.Append(" (Inactive)");

            if (PrefabUtility.IsAnyPrefabInstanceRoot(go))
            {
                ctx.instances++;
                sb.Append(" [Prefab: ").Append(DescribeUITool.GetSourcePrefabPath(go)).Append(']');
                int overrides = CountOverrides(go);
                if (overrides > 0)
                {
                    ctx.overriddenInstances++;
                    sb.Append($" ({overrides} override(s) na cena)");
                }
                sb.AppendLine();
                return;
            }

            ctx.loose++;
            int loose = ctx.looseCount.TryGetValue(t, out var n) ? n : 1;
            bool isRootCanvas = go.TryGetComponent<Canvas>(out var canvas) && canvas.isRootCanvas;
            int repeats = CandidateRule.RepeatCount(t, ctx.signatures);
            bool candidate = !insideCandidate && CandidateRule.IsCandidate(t, loose, repeats, isRootCanvas);

            if (candidate)
            {
                ctx.candidates.Add(new Candidate { path = GetHierarchyPath(t), count = loose, repeats = repeats, isUI = t is RectTransform });
                sb.Append($" ⚠ CANDIDATO A PREFAB ({loose} objetos" + (repeats >= 2 ? $", ×{repeats} repetidos" : "") + ")");
            }
            if (isRootCanvas) sb.Append($" [Canvas {canvas.renderMode}]");

            var scripts = go.GetComponents<MonoBehaviour>()
                .Where(c => c != null)
                .Select(c => c.GetType())
                .Where(type => !(type.Namespace ?? "").StartsWith("UnityEngine") &&
                               !(type.Namespace ?? "").StartsWith("TMPro") &&
                               !(type.Namespace ?? "").StartsWith("Unity."))
                .Select(type => type.Name)
                .ToList();
            if (scripts.Count > 0) sb.Append(" | scripts: ").Append(string.Join(", ", scripts));

            if (depth >= Settings.sceneMaxDepth && t.childCount > 0)
            {
                sb.AppendLine($" (+{DescribeUITool.CountDescendants(t)} ocultos)");
                // Ainda conta o resto para estatísticas/candidatos.
                foreach (Transform child in t) CollectStatsOnly(child, insideCandidate || candidate, ctx);
                return;
            }

            sb.AppendLine();
            foreach (Transform child in t)
                DescribeSceneNode(child, depth + 1, insideCandidate || candidate, ctx, sb);
        }

        private static void CollectStatsOnly(Transform t, bool insideCandidate, SceneContext ctx)
        {
            var go = t.gameObject;
            if (PrefabUtility.IsAnyPrefabInstanceRoot(go))
            {
                ctx.instances++;
                if (CountOverrides(go) > 0) ctx.overriddenInstances++;
                return;
            }
            ctx.loose++;
            int loose = ctx.looseCount.TryGetValue(t, out var n) ? n : 1;
            int repeats = CandidateRule.RepeatCount(t, ctx.signatures);
            bool isRootCanvas = go.TryGetComponent<Canvas>(out var canvas) && canvas.isRootCanvas;
            bool candidate = !insideCandidate && CandidateRule.IsCandidate(t, loose, repeats, isRootCanvas);
            if (candidate) ctx.candidates.Add(new Candidate { path = GetHierarchyPath(t), count = loose, repeats = repeats, isUI = t is RectTransform });
            foreach (Transform child in t) CollectStatsOnly(child, insideCandidate || candidate, ctx);
        }

        private static int CountOverrides(GameObject instanceRoot)
        {
            try
            {
                return PrefabUtility.GetObjectOverrides(instanceRoot, false).Count +
                       PrefabUtility.GetAddedGameObjects(instanceRoot).Count +
                       PrefabUtility.GetAddedComponents(instanceRoot).Count +
                       PrefabUtility.GetRemovedComponents(instanceRoot).Count;
            }
            catch
            {
                return 0;
            }
        }

        private static string GetHierarchyPath(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        // ---------------- Exportação completa e índice ----------------

        /// <summary>Exporta todos os prefabs rastreados + cenas abertas e reconstrói o índice.</summary>
        public static int ExportAll()
        {
            var paths = AssetDatabase.FindAssets("t:Prefab", Settings.SearchFolders)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(Settings.IsTracked)
                .Distinct()
                .OrderBy(p => p)
                .ToList();

            int exported = 0;
            bool cancelled = false;
            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("AI Capture",
                            $"Descrevendo {Path.GetFileName(paths[i])} ({i + 1}/{paths.Count})", (float)i / paths.Count))
                    {
                        cancelled = true;
                        break;
                    }
                    try { if (ExportPrefab(paths[i])) exported++; }
                    catch (Exception e) { Debug.LogWarning($"[AICapture] Falha ao descrever {paths[i]}: {e.Message}"); }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (!cancelled) RemoveStalePrefabDescriptions(paths);
            ExportOpenScenes();
            RebuildIndex();

            Debug.Log($"[AICapture] Contexto exportado: {exported} prefab(s){(cancelled ? " (cancelado)" : "")} → {Root}");
            return exported;
        }

        /// <summary>Recria index.txt a partir da primeira linha de cada descrição (rápido, não reabre prefabs).</summary>
        public static void RebuildIndex()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Índice de contexto para IA — gerado pelo AI Capture. Leia AIContext/README.md para o formato.");
            sb.AppendLine();
            AppendIndexSection(sb, "## Cenas", Path.Combine(Root, ScenesSub));
            AppendIndexSection(sb, "## Prefabs", Path.Combine(Root, PrefabsSub));

            WriteIfChanged(IndexPath, sb.ToString());
            WriteAgentReadme();
        }

        private static void AppendIndexSection(StringBuilder sb, string title, string dir)
        {
            sb.AppendLine(title);
            if (!Directory.Exists(dir)) { sb.AppendLine("(nenhum)"); sb.AppendLine(); return; }

            foreach (var file in Directory.GetFiles(dir, "*.txt", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string first = File.ReadLines(file).FirstOrDefault() ?? "";
                sb.Append("- ").AppendLine(first.TrimStart('#', ' '));
            }
            sb.AppendLine();
        }

        private static void RemoveStalePrefabDescriptions(IEnumerable<string> currentPrefabPaths)
        {
            string dir = Path.Combine(Root, PrefabsSub);
            if (!Directory.Exists(dir)) return;

            var expected = new HashSet<string>(
                currentPrefabPaths.Select(p => Path.GetFullPath(GetPrefabDescriptionPath(p))),
                StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(dir, "*.txt", SearchOption.AllDirectories))
                if (!expected.Contains(Path.GetFullPath(file))) File.Delete(file);

            foreach (var sub in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub);
        }

        private static void WriteAgentReadme()
        {
            const string text =
@"# Contexto do projeto Unity para agentes de IA

Gerado automaticamente pelo pacote AI Capture (com.chromehypercube.ai-capture). Não edite à mão.

## Como usar
1. Leia `index.txt` primeiro: uma linha por cena e por prefab, com tipo, tamanho, scripts e prefabs usados.
2. Para entender um prefab, leia a descrição dele:
   `Assets/<caminho>/<Nome>.prefab` → `AIContext/prefabs/<caminho>/<Nome>.txt`
3. Para entender uma cena, leia `AIContext/scenes/<caminho>/<Nome>.txt`.
4. Para EDITAR, altere o arquivo original (.prefab / .unity, YAML). As descrições são só leitura.
5. `[Prefab: X]` indica um prefab aninhado; o conteúdo dele está na descrição do próprio X.

## Legenda
- `- Nome (Inactive)`: GameObject e se está desativado.
- `WxH pos(x,y)`: tamanho do RectTransform e anchoredPosition, em unidades de UI.
- `anchor(a)-(b)`: só aparece quando o elemento estica (anchorMin ≠ anchorMax).
- `Image(...)`, `TMP(""texto"", size)`, `[Button]`, `[Toggle on]`, `[VLayout]`...: componentes relevantes.
- `scripts: A, B`: MonoBehaviours do próprio projeto naquele objeto.
- `⚠MissingScript`: componente com script ausente.
- `(+N internos)` / `(+N ocultos)`: filhos omitidos (prefab aninhado ou profundidade máxima).
- Nas cenas, `⚠ CANDIDATO A PREFAB`: grupo grande de objetos fora de prefabs, bom para extrair.
- Nas cenas, `(N override(s) na cena)`: alterações locais da instância; aplicar no prefab reduz o arquivo da cena.

## Ações (execute C# no Editor; cada método devolve JSON e grava AIContext/last_action.json)
Classe: `ChromeHypercube.AICapture.AICaptureAgentAPI`
- `ListPrefabCandidates()` — candidatos a prefab da cena ativa, já analisados.
- `AnalyzeForPrefab(""Canvas/Painel"")` — riscos antes de converter (referências externas, scripts faltando).
- `ConvertToPrefab(""Canvas/Painel"")` — cria o prefab, salva a cena e atualiza este contexto.
  Recusa se houver referências para objetos fora do grupo; só use `allowExternalReferences: true`
  depois de decidir como religar essas referências (por código ou evento).
- `RunScenario(""Assets/.../Cenario.asset"")` e depois `GetLastRunReport()` — captura e compara com as goldens.
- `ExportContext()` — regera tudo.

Fluxo seguro para converter menus: rodar o cenário (golden ""antes"") → analisar → converter → rodar de novo → conferir o relatório.
";
            WriteIfChanged(Path.Combine(Root, "README.md"), text);
        }

        // ---------------- Utilitários ----------------

        private static string ToRelativeTxt(string assetPath)
        {
            string rel = assetPath.Replace('\\', '/');
            if (rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) rel = rel.Substring("Assets/".Length);
            else if (rel.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) rel = "_packages/" + rel.Substring("Packages/".Length);
            return Path.ChangeExtension(rel, ".txt");
        }

        private static void WriteIfChanged(string path, string content)
        {
            content = content.Replace("\r\n", "\n");
            if (File.Exists(path) && File.ReadAllText(path) == content) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }

        private static string Limit(IEnumerable<string> items, int max)
        {
            var list = items.ToList();
            return list.Count <= max
                ? string.Join(", ", list)
                : string.Join(", ", list.Take(max)) + $" +{list.Count - max}";
        }

        public static void RevealContextFolder()
        {
            Directory.CreateDirectory(Root);
            EditorUtility.RevealInFinder(File.Exists(IndexPath) ? IndexPath : Root);
        }

        // ---------------- Menus ----------------

        [MenuItem("Tools/AI Capture/Exportar contexto para IA (prefabs + cenas abertas)", false, 60)]
        private static void MenuExportAll() => ExportAll();

        [MenuItem("Tools/AI Capture/Exportar resumo das cenas abertas", false, 61)]
        private static void MenuExportScenes()
        {
            ExportOpenScenes();
            RebuildIndex();
            Debug.Log($"[AICapture] Resumo das cenas abertas exportado → {Path.Combine(Root, ScenesSub)}");
        }

        [MenuItem("Tools/AI Capture/Abrir pasta de contexto", false, 62)]
        private static void MenuReveal() => RevealContextFolder();

        [MenuItem("Tools/AI Capture/Configurações…", false, 100)]
        private static void MenuSettings() => SettingsService.OpenProjectSettings(AICaptureSettingsProvider.SettingsPath);

        [MenuItem("Assets/AI Capture/Exportar descrição para IA", false, 1010)]
        private static void MenuExportSelection()
        {
            var paths = new List<string>();
            foreach (var guid in Selection.assetGUIDs)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(path))
                    paths.AddRange(AssetDatabase.FindAssets("t:Prefab", new[] { path }).Select(AssetDatabase.GUIDToAssetPath));
                else if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    paths.Add(path);
            }

            int count = paths.Distinct().Count(ExportPrefab);
            RebuildIndex();
            Debug.Log($"[AICapture] {count} descrição(ões) de prefab exportada(s) → {Path.Combine(Root, PrefabsSub)}");
        }

        [MenuItem("Assets/AI Capture/Exportar descrição para IA", true)]
        private static bool ValidateExportSelection() =>
            Selection.assetGUIDs.Select(AssetDatabase.GUIDToAssetPath)
                .Any(p => AssetDatabase.IsValidFolder(p) || p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Mantém as descrições em dia quando prefabs são salvos, movidos ou apagados.</summary>
    internal class AIContextPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            var settings = AICaptureSettings.instance;
            // Só depois da primeira exportação: evita trabalho pesado na primeira importação do projeto.
            if (!settings.autoUpdate || !AIContextExporter.HasContext) return;

            try
            {
                bool changed = false;

                foreach (var p in deleted.Concat(movedFrom))
                {
                    if (!p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                    AIContextExporter.DeletePrefabDescription(p);
                    changed = true;
                }

                foreach (var p in imported.Concat(moved))
                {
                    if (!settings.IsTracked(p)) continue;
                    changed |= AIContextExporter.ExportPrefab(p);
                }

                if (changed) AIContextExporter.RebuildIndex();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AICapture] Falha ao atualizar o contexto para IA: {e.Message}");
            }
        }
    }

    /// <summary>Atualiza o resumo de uma cena sempre que ela é salva.</summary>
    [InitializeOnLoad]
    internal static class AIContextSceneHook
    {
        static AIContextSceneHook()
        {
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorSceneManager.sceneSaved += OnSceneSaved;
        }

        private static void OnSceneSaved(Scene scene)
        {
            if (!AICaptureSettings.instance.autoUpdate || !AIContextExporter.HasContext) return;
            try
            {
                AIContextExporter.ExportScene(scene);
                AIContextExporter.RebuildIndex();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AICapture] Falha ao atualizar o resumo da cena: {e.Message}");
            }
        }
    }
}
