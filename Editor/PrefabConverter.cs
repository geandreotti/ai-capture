using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ChromeHypercube.AICapture
{
    [Serializable]
    public class ExternalReference
    {
        /// <summary>Objeto dentro do grupo que tem a referência (caminho relativo ao grupo).</summary>
        public string source;
        public string component;
        /// <summary>Campo serializado (ex.: m_OnClick.m_PersistentCalls.m_Calls.Array.data[0].m_Target).</summary>
        public string field;
        /// <summary>Objeto da cena, fora do grupo, para onde a referência aponta.</summary>
        public string target;
    }

    [Serializable]
    public class PrefabAnalysis
    {
        public string hierarchyPath;
        public string scene;
        public bool canConvert;
        /// <summary>Motivo que impede a conversão (vazio se canConvert = true).</summary>
        public string blockReason;
        /// <summary>True quando o único impedimento são referências externas (liberável com allowExternalReferences).</summary>
        public bool blockedOnlyByExternalReferences;
        public int objectCount;
        /// <summary>Siblings with the same structure (cards, rows); 1 when it is a one-off group.</summary>
        public int repeatCount = 1;
        /// <summary>Not a blocker: what to know before converting (e.g. references that stay as scene overrides).</summary>
        public string warning;
        public bool isUI;
        public int nestedPrefabCount;
        public int missingScripts;
        public string suggestedPrefabPath;
        public List<ExternalReference> externalReferences = new List<ExternalReference>();
    }

    [Serializable]
    public class PrefabConversionResult
    {
        public bool success;
        public string message;
        public string prefabPath;
        public string descriptionPath;
        public bool sceneSaved;
        public PrefabAnalysis analysis;
    }

    [Serializable]
    public class PrefabCandidateList
    {
        public string scene;
        public int threshold;
        public List<PrefabAnalysis> candidates = new List<PrefabAnalysis>();
    }

    /// <summary>
    /// Converte grupos de objetos da cena em prefabs, com análise prévia de riscos.
    ///
    /// Referências externas: um prefab salvo não pode apontar para objetos da cena (ex.: um botão
    /// cujo OnClick chama um GameManager da cena). Na cena atual a ligação continua funcionando,
    /// mas fica guardada como override, e em outras cenas o campo chega vazio. Por isso a conversão
    /// é recusada quando existem referências externas, a menos que allowExternalReferences = true.
    /// </summary>
    public static class PrefabConverter
    {
        private static AICaptureSettings Settings => AICaptureSettings.instance;

        // ---------------- Análise ----------------

        public static PrefabAnalysis Analyze(GameObject go)
        {
            var a = new PrefabAnalysis();
            if (go == null) { a.blockReason = "Objeto nulo."; return a; }

            a.hierarchyPath = GetHierarchyPath(go.transform);
            a.scene = string.IsNullOrEmpty(go.scene.path) ? go.scene.name : go.scene.path;
            a.isUI = go.transform is RectTransform;
            a.objectCount = go.GetComponentsInChildren<Transform>(true).Length;
            a.suggestedPrefabPath = BuildPrefabPath(go.name, Settings.prefabOutputFolder, unique: false);

            if (EditorApplication.isPlayingOrWillChangePlaymode)
                a.blockReason = "Saia do Play Mode antes de converter.";
            else if (EditorUtility.IsPersistent(go) || !go.scene.IsValid())
                a.blockReason = "O objeto não está numa cena (já é um asset).";
            else if (PrefabStageUtility.GetPrefabStage(go) != null)
                a.blockReason = "O objeto está no modo de edição de prefab. Para criar um prefab aninhado, saia do modo de edição e converta na cena.";
            else if (PrefabUtility.IsAnyPrefabInstanceRoot(go))
                a.blockReason = $"Já é uma instância do prefab {DescribeUITool.GetSourcePrefabPath(go)}.";
            else if (PrefabUtility.IsPartOfPrefabInstance(go))
                a.blockReason = "Faz parte de outro prefab. Edite o prefab original ou desempacote a instância antes.";

            CollectReferences(go, a);

            if (string.IsNullOrEmpty(a.blockReason) && a.externalReferences.Count > 0 && Settings.allowExternalReferences)
            {
                a.warning = $"{a.externalReferences.Count} referência(s) para objetos fora do grupo ficam como override nesta cena " +
                            "(chegam vazias se o prefab for usado em outra cena).";
            }
            else if (string.IsNullOrEmpty(a.blockReason) && a.externalReferences.Count > 0)
            {
                a.blockedOnlyByExternalReferences = true;
                a.blockReason = $"{a.externalReferences.Count} referência(s) para objetos fora do grupo. " +
                                "Revise a lista ou converta com allowExternalReferences = true.";
            }

            a.canConvert = string.IsNullOrEmpty(a.blockReason);
            return a;
        }

        private static void CollectReferences(GameObject root, PrefabAnalysis a)
        {
            var rootTransform = root.transform;
            var seen = new HashSet<string>();

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != rootTransform && PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject))
                    a.nestedPrefabCount++;

                foreach (var comp in t.GetComponents<Component>())
                {
                    if (comp == null) { a.missingScripts++; continue; }
                    if (comp is Transform) continue;

                    var so = new SerializedObject(comp);
                    var it = so.GetIterator();
                    while (it.Next(true))
                    {
                        if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var target = it.objectReferenceValue;
                        if (target == null || EditorUtility.IsPersistent(target)) continue; // assets são permitidos

                        var targetGo = target as GameObject ?? (target as Component)?.gameObject;
                        if (targetGo == null || targetGo.transform.IsChildOf(rootTransform)) continue;

                        var reference = new ExternalReference
                        {
                            source = RelativePath(t, rootTransform),
                            component = comp.GetType().Name,
                            field = it.propertyPath,
                            target = GetHierarchyPath(targetGo.transform)
                        };
                        string key = reference.source + "|" + reference.component + "|" + reference.field;
                        if (seen.Add(key)) a.externalReferences.Add(reference);
                    }
                }
            }
        }

        // ---------------- Conversão ----------------

        /// <param name="folder">Pasta em Assets/ (vazio = configuração do projeto).</param>
        /// <param name="allowExternalReferences">Converte mesmo com referências para fora do grupo.</param>
        /// <param name="saveScene">Salva a cena depois (atualiza o resumo da cena no contexto para IA).</param>
        public static PrefabConversionResult Convert(GameObject go, string folder = null,
            bool allowExternalReferences = false, bool saveScene = true)
        {
            var result = new PrefabConversionResult { analysis = Analyze(go) };
            var a = result.analysis;

            if (!a.canConvert && !(allowExternalReferences && a.blockedOnlyByExternalReferences))
            {
                result.message = "Conversão recusada: " + a.blockReason;
                return result;
            }

            try
            {
                string targetFolder = string.IsNullOrWhiteSpace(folder) ? Settings.prefabOutputFolder : folder;
                EnsureFolder(targetFolder);
                string path = BuildPrefabPath(go.name, targetFolder, unique: true);

                var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(go, path, InteractionMode.UserAction, out bool ok);
                if (!ok || prefab == null)
                {
                    result.message = "A Unity não conseguiu salvar o prefab.";
                    return result;
                }

                result.success = true;
                result.prefabPath = path;
                EditorSceneManager.MarkSceneDirty(go.scene);

                if (saveScene && !string.IsNullOrEmpty(go.scene.path))
                    result.sceneSaved = EditorSceneManager.SaveScene(go.scene);

                if (AIContextExporter.HasContext)
                {
                    AIContextExporter.ExportPrefab(path);
                    AIContextExporter.ExportScene(go.scene);
                    AIContextExporter.RebuildIndex();
                    result.descriptionPath = AIContextExporter.GetPrefabDescriptionPath(path);
                }

                result.message = a.externalReferences.Count > 0
                    ? $"Prefab criado em {path}. Atenção: {a.externalReferences.Count} referência(s) externa(s) ficaram como override nesta cena e chegarão vazias em outras cenas."
                    : $"Prefab criado em {path}.";
            }
            catch (Exception e)
            {
                result.success = false;
                result.message = "Erro na conversão: " + e.GetBaseException().Message;
            }

            return result;
        }

        // ---------------- Candidatos ----------------

        /// <summary>Grupos grandes de objetos soltos (mesma regra do resumo de cena), já analisados.</summary>
        public static PrefabCandidateList FindCandidates(Scene scene)
        {
            var list = new PrefabCandidateList
            {
                scene = string.IsNullOrEmpty(scene.path) ? scene.name : scene.path,
                threshold = Settings.sceneCandidateThreshold
            };
            if (!scene.IsValid() || !scene.isLoaded) return list;

            var counts = new Dictionary<Transform, int>();
            foreach (var root in scene.GetRootGameObjects()) CountLoose(root.transform, counts);
            var signatures = new Dictionary<Transform, string>();
            foreach (var root in scene.GetRootGameObjects()) Collect(root.transform, counts, signatures, list);

            list.candidates = list.candidates.OrderByDescending(c => c.objectCount).ToList();
            return list;
        }

        private static int CountLoose(Transform t, Dictionary<Transform, int> counts)
        {
            if (PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) { counts[t] = 0; return 0; }
            int n = 1;
            foreach (Transform child in t) n += CountLoose(child, counts);
            counts[t] = n;
            return n;
        }

        private static void Collect(Transform t, Dictionary<Transform, int> counts, Dictionary<Transform, string> signatures, PrefabCandidateList list)
        {
            if (PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) return;

            bool isRootCanvas = t.TryGetComponent<Canvas>(out var canvas) && canvas.isRootCanvas;
            int loose = counts.TryGetValue(t, out var n) ? n : 1;
            int repeats = CandidateRule.RepeatCount(t, signatures);
            if (CandidateRule.IsCandidate(t, loose, repeats, isRootCanvas))
            {
                var analysis = Analyze(t.gameObject);
                analysis.repeatCount = repeats;
                list.candidates.Add(analysis);
                return; // o grupo inteiro vira um prefab; não sugere os filhos
            }
            foreach (Transform child in t) Collect(child, counts, signatures, list);
        }

        // ---------------- Busca por caminho ----------------

        /// <summary>
        /// Encontra um objeto pelo caminho na hierarquia ("Canvas/SettingsPanel"), em todas as cenas abertas.
        /// Aceita prefixo de cena: "Assets/Scenes/Main.unity:Canvas/SettingsPanel".
        /// </summary>
        public static GameObject FindByHierarchyPath(string hierarchyPath, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(hierarchyPath)) { error = "Caminho vazio."; return null; }

            string scenePart = null, path = hierarchyPath.Trim();
            int colon = path.IndexOf(".unity:", StringComparison.OrdinalIgnoreCase);
            if (colon >= 0)
            {
                scenePart = path.Substring(0, colon + ".unity".Length);
                path = path.Substring(colon + ".unity:".Length);
            }

            var names = path.Split('/');
            var matches = new List<Transform>();

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                if (scenePart != null && !string.Equals(scene.path, scenePart, StringComparison.OrdinalIgnoreCase)) continue;

                var level = scene.GetRootGameObjects().Select(g => g.transform).Where(t => t.name == names[0]).ToList();
                for (int i = 1; i < names.Length && level.Count > 0; i++)
                {
                    var next = new List<Transform>();
                    foreach (var parent in level)
                        foreach (Transform child in parent)
                            if (child.name == names[i]) next.Add(child);
                    level = next;
                }
                matches.AddRange(level);
            }

            if (matches.Count == 0) { error = $"Nenhum objeto encontrado em \"{hierarchyPath}\"."; return null; }
            if (matches.Count > 1)
            {
                error = $"{matches.Count} objetos com o caminho \"{hierarchyPath}\". Renomeie para deixar o caminho único ou informe a cena.";
                return null;
            }
            return matches[0].gameObject;
        }

        // ---------------- Utilitários ----------------

        public static string GetHierarchyPath(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string RelativePath(Transform t, Transform root)
        {
            if (t == root) return root.name;
            var parts = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
            parts.Add(root.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string BuildPrefabPath(string objectName, string folder, bool unique)
        {
            string safeName = string.Concat(objectName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            if (string.IsNullOrEmpty(safeName)) safeName = "Prefab";
            folder = (string.IsNullOrWhiteSpace(folder) ? "Assets/Prefabs/Converted" : folder).Replace('\\', '/').TrimEnd('/');
            string path = $"{folder}/{safeName}.prefab";
            return unique ? AssetDatabase.GenerateUniqueAssetPath(path) : path;
        }

        private static void EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (!folder.StartsWith("Assets", StringComparison.Ordinal))
                throw new ArgumentException($"A pasta precisa ficar dentro de Assets/: {folder}");
            if (AssetDatabase.IsValidFolder(folder)) return;

            string current = "Assets";
            foreach (var part in folder.Split('/').Skip(1))
            {
                string next = current + "/" + part;
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, part);
                current = next;
            }
        }

        // ---------------- Menus (uso humano) ----------------

        [MenuItem("GameObject/AI Capture/Converter em prefab…", false, 50)]
        private static void MenuConvert()
        {
            var go = Selection.activeGameObject;
            var analysis = Analyze(go);
            bool allowRefs = false;

            if (!analysis.canConvert)
            {
                if (!analysis.blockedOnlyByExternalReferences)
                {
                    EditorUtility.DisplayDialog("Converter em prefab", analysis.blockReason, "OK");
                    return;
                }

                var lines = analysis.externalReferences.Take(10)
                    .Select(r => $"• {r.source} ({r.component}) → {r.target}");
                string more = analysis.externalReferences.Count > 10 ? $"\n… e mais {analysis.externalReferences.Count - 10}" : "";
                allowRefs = EditorUtility.DisplayDialog("Referências para fora do grupo",
                    "Estas ligações continuam funcionando nesta cena (como override), mas chegarão vazias se o prefab for usado em outra cena:\n\n" +
                    string.Join("\n", lines) + more,
                    "Converter mesmo assim", "Cancelar");
                if (!allowRefs) return;
            }

            var result = Convert(go, null, allowRefs, saveScene: false);
            if (result.success) Debug.Log("[AICapture] " + result.message);
            else EditorUtility.DisplayDialog("Converter em prefab", result.message, "OK");
        }

        [MenuItem("GameObject/AI Capture/Converter em prefab…", true)]
        private static bool ValidateMenuConvert() =>
            Selection.activeGameObject != null && !EditorApplication.isPlaying &&
            !EditorUtility.IsPersistent(Selection.activeGameObject);

        [MenuItem("Tools/AI Capture/Listar candidatos a prefab (cena ativa)", false, 63)]
        private static void MenuListCandidates()
        {
            var list = FindCandidates(SceneManager.GetActiveScene());
            Debug.Log($"[AICapture] {list.candidates.Count} candidato(s) a prefab em {list.scene}:\n" + JsonUtility.ToJson(list, true));
        }
    }
}
