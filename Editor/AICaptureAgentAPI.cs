using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ChromeHypercube.AICapture
{
    /// <summary>
    /// Fachada para agentes de IA. Todos os métodos recebem e devolvem texto (JSON),
    /// então funcionam com qualquer ferramenta que execute C# no Editor
    /// (MCP for Unity, Unity CLI "command eval", etc.).
    ///
    /// Cada resposta também é gravada em AIContext/last_action.json, para ferramentas
    /// que executam código mas não devolvem o valor de retorno.
    ///
    /// Fluxo sugerido para converter menus em prefabs:
    ///   1. ListPrefabCandidates()                 → escolhe um candidato
    ///   2. AnalyzeForPrefab("Canvas/Settings")    → confere referências externas
    ///   3. RunScenario("Assets/.../Menus.asset")  → golden "antes" (se ainda não houver)
    ///   4. ConvertToPrefab("Canvas/Settings")     → cria o prefab e atualiza o contexto
    ///   5. RunScenario(...) + GetLastRunReport()  → confirma que nada mudou visualmente
    /// </summary>
    public static class AICaptureAgentAPI
    {
        /// <summary>Lista grupos da cena que deveriam virar prefab, já com a análise de cada um.</summary>
        public static string ListPrefabCandidates(string scenePath = null)
        {
            var scene = string.IsNullOrEmpty(scenePath) ? SceneManager.GetActiveScene() : SceneManager.GetSceneByPath(scenePath);
            if (!scene.IsValid()) return Respond(Error($"Cena não está aberta: {scenePath}"));
            return Respond(JsonUtility.ToJson(PrefabConverter.FindCandidates(scene), true));
        }

        /// <summary>Analisa um objeto pelo caminho na hierarquia, sem alterar nada.</summary>
        public static string AnalyzeForPrefab(string hierarchyPath)
        {
            var go = PrefabConverter.FindByHierarchyPath(hierarchyPath, out var error);
            if (go == null) return Respond(Error(error));
            return Respond(JsonUtility.ToJson(PrefabConverter.Analyze(go), true));
        }

        /// <summary>
        /// Converte o objeto em prefab. Recusa se houver referências para fora do grupo,
        /// a menos que allowExternalReferences = true.
        /// </summary>
        public static string ConvertToPrefab(string hierarchyPath, bool allowExternalReferences = false,
            string folder = null, bool saveScene = true)
        {
            var go = PrefabConverter.FindByHierarchyPath(hierarchyPath, out var error);
            if (go == null) return Respond(Error(error));
            return Respond(JsonUtility.ToJson(PrefabConverter.Convert(go, folder, allowExternalReferences, saveScene), true));
        }

        /// <summary>Gera ou atualiza toda a pasta de contexto (prefabs + cenas abertas + índice).</summary>
        public static string ExportContext()
        {
            int count = AIContextExporter.ExportAll();
            return Respond(Ok($"{count} prefab(s) descritos.", AIContextExporter.IndexPath));
        }

        /// <summary>
        /// Inicia um cenário de captura. A execução é assíncrona (entra e sai do Play Mode);
        /// leia o resultado depois com GetLastRunReport().
        /// </summary>
        public static string RunScenario(string scenarioAssetPath, string locale = null, bool compare = true)
        {
            var scenario = AssetDatabase.LoadAssetAtPath<CaptureScenario>(scenarioAssetPath);
            if (scenario == null) return Respond(Error($"Cenário não encontrado: {scenarioAssetPath}"));
            if (ScenarioPlayer.IsRunning) return Respond(Error("Já existe um cenário rodando."));

            CaptureRunner.Run(scenario, locale, compare);
            return Respond(Ok("Cenário iniciado. Aguarde o fim do Play Mode e chame GetLastRunReport().",
                Path.Combine(CaptureRunner.OutputRoot, "latest.txt")));
        }

        /// <summary>Devolve o regression_report.json (ou o manifest.json) da última execução.</summary>
        public static string GetLastRunReport()
        {
            if (ScenarioPlayer.IsRunning || EditorApplication.isPlayingOrWillChangePlaymode)
                return Respond(Error("Cenário ainda em execução. Tente novamente em alguns segundos."));

            string dir = CaptureRunner.LastOutputDir;
            string latest = Path.Combine(CaptureRunner.OutputRoot, "latest.txt");
            if (string.IsNullOrEmpty(dir) && File.Exists(latest)) dir = File.ReadAllText(latest).Trim();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return Respond(Error("Nenhuma execução encontrada."));

            string report = Path.Combine(dir, "regression_report.json");
            string manifest = Path.Combine(dir, "manifest.json");
            if (File.Exists(report)) return Respond(File.ReadAllText(report));
            if (File.Exists(manifest)) return Respond(File.ReadAllText(manifest));
            return Respond(Error($"Execução sem relatório em {dir}."));
        }

        /// <summary>Aprova as capturas da última execução como novas golden images.</summary>
        public static string AcceptLastRunAsGolden(string onlyCapture = null)
        {
            int n = CaptureRunner.AcceptAsGolden(CaptureRunner.LastScenarioId, CaptureRunner.LastOutputDir, onlyCapture);
            return Respond(n > 0 ? Ok($"{n} imagem(ns) aprovada(s).", null) : Error("Nada para aprovar."));
        }

        /// <summary>
        /// Declares that a capture is EXPECTED to change (a UI edit was made on purpose). The next run lists it as awaiting
        /// approval instead of a regression. region (x, y, width, height, origin top-left) limits where the change may appear;
        /// leave width/height at 0 for "anywhere in the capture". capture "*" = every capture.
        /// </summary>
        public static string DeclareExpectedChange(string scenarioId, string capture, string description,
            int x = 0, int y = 0, int width = 0, int height = 0)
        {
            if (string.IsNullOrEmpty(scenarioId) || string.IsNullOrEmpty(capture))
                return Respond(Error("scenarioId and capture are required."));
            RectInt? region = width > 0 && height > 0 ? new RectInt(x, y, width, height) : (RectInt?)null;
            ExpectedChanges.Declare(scenarioId, capture, description ?? "", region);
            return Respond(Ok($"Declared: {capture} ({description}).", ExpectedChanges.PathFor(scenarioId)));
        }

        // ---------------- Helpers ----------------

        [Serializable]
        private class SimpleResponse
        {
            public bool success;
            public string message;
            public string path;
        }

        private static string Ok(string message, string path) =>
            JsonUtility.ToJson(new SimpleResponse { success = true, message = message, path = path }, true);

        private static string Error(string message) =>
            JsonUtility.ToJson(new SimpleResponse { success = false, message = message }, true);

        private static string Respond(string json)
        {
            try
            {
                string root = AICaptureSettings.instance.ContextRoot;
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "last_action.json"), json);
            }
            catch
            {
                // A resposta continua sendo devolvida mesmo se não der para gravar o arquivo.
            }
            return json;
        }
    }
}
