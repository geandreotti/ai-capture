using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ChromeHypercube.AICapture
{
    /// <summary>
    /// Orquestra a execução de cenários pelo Editor: entra em Play Mode, roda o ScenarioPlayer,
    /// compara com as goldens e sai do Play Mode. Também é a API para agentes de IA/scripts.
    /// </summary>
    public static class CaptureRunner
    {
        private const string KeyPendingGuid = "AICapture.PendingScenario";
        private const string KeyPendingLocale = "AICapture.PendingLocale";
        private const string KeyPendingCompare = "AICapture.PendingCompare";
        private const string KeyPendingExit = "AICapture.PendingExit";
        private const string KeyLastOutput = "AICapture.LastOutputDir";
        private const string KeyLastScenario = "AICapture.LastScenarioId";

        /// <summary>Pasta das Golden Images: &lt;projeto&gt;/AICaptureGoldens/&lt;scenarioId&gt;/ (versione no Git).</summary>
        public static string GoldenRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "AICaptureGoldens"));

        public static string OutputRoot => Path.Combine(Path.GetTempPath(), "ai-capture");

        public static string LastOutputDir => SessionState.GetString(KeyLastOutput, "");
        public static string LastScenarioId => SessionState.GetString(KeyLastScenario, "");

        /// <summary>Disparado ao fim de cada execução (report é null quando compare = false).</summary>
        public static event Action<CaptureResult, VisualRegression.ScenarioReport> Completed;

        [InitializeOnLoadMethod]
        private static void Init()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>
        /// Roda um cenário. Se não estiver em Play Mode, entra, executa e sai sozinho.
        /// </summary>
        public static void Run(CaptureScenario scenario, string locale = null, bool compare = true, bool exitPlayModeWhenDone = true)
        {
            if (scenario == null) { Debug.LogError("[AICapture] Cenário nulo."); return; }
            if (ScenarioPlayer.IsRunning) { Debug.LogWarning("[AICapture] Já existe um cenário rodando."); return; }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                Debug.LogWarning("[AICapture] O Editor está compilando/importando; um recompile sairia do Play Mode no meio da captura. Tente de novo em instantes.");
                return;
            }

            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(scenario));
            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogError("[AICapture] O cenário precisa ser um asset salvo no projeto.");
                return;
            }

            SessionState.SetString(KeyPendingGuid, guid);
            SessionState.SetString(KeyPendingLocale, locale ?? "");
            SessionState.SetBool(KeyPendingCompare, compare);

            if (EditorApplication.isPlaying)
            {
                SessionState.SetBool(KeyPendingExit, false); // não derruba uma sessão que você abriu
                StartPending();
            }
            else
            {
                SessionState.SetBool(KeyPendingExit, exitPlayModeWhenDone);
                EditorApplication.EnterPlaymode();
            }
        }

        /// <summary>Atalho para agentes/scripts: CaptureRunner.RunByPath("Assets/Capture/Home.asset", "pt-BR").</summary>
        public static void RunByPath(string assetPath, string locale = null, bool compare = true)
        {
            var scenario = AssetDatabase.LoadAssetAtPath<CaptureScenario>(assetPath);
            if (scenario == null) { Debug.LogError($"[AICapture] Cenário não encontrado em {assetPath}."); return; }
            Run(scenario, locale, compare);
        }

        /// <summary>Copia os screenshots de uma execução para a pasta de goldens (aprova o visual atual).</summary>
        public static int AcceptAsGolden(string scenarioId, string outputDir, string onlyCapture = null)
        {
            if (string.IsNullOrEmpty(scenarioId) || !Directory.Exists(outputDir))
            {
                Debug.LogWarning("[AICapture] Nenhuma execução para aprovar.");
                return 0;
            }

            string goldenDir = Path.Combine(GoldenRoot, scenarioId);
            Directory.CreateDirectory(goldenDir);
            int count = 0;
            foreach (var file in VisualRegression.GetCaptureFiles(outputDir))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrEmpty(onlyCapture) && name != onlyCapture && !name.StartsWith(onlyCapture + "_")) continue;
                File.Copy(file, Path.Combine(goldenDir, Path.GetFileName(file)), true);
                count++;
                ExpectedChanges.Clear(scenarioId, name.Contains("_") ? name.Substring(0, name.LastIndexOf('_')) : name);
            }
            Debug.Log($"[AICapture] {count} imagem(ns) aprovada(s) como golden em {goldenDir}");
            if (count > 0 && string.IsNullOrEmpty(onlyCapture))
                RunRetention.DeleteRun(outputDir); // every capture is in the goldens now; a partial approval keeps the run
            return count;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
                StartPending();
            else if (state == PlayModeStateChange.EnteredEditMode)
                SessionState.EraseString(KeyPendingGuid); // limpa pendências de execuções abortadas
        }

        private static void StartPending()
        {
            string guid = SessionState.GetString(KeyPendingGuid, "");
            if (string.IsNullOrEmpty(guid)) return;
            SessionState.EraseString(KeyPendingGuid);

            var scenario = AssetDatabase.LoadAssetAtPath<CaptureScenario>(AssetDatabase.GUIDToAssetPath(guid));
            if (scenario == null) { Debug.LogError("[AICapture] Cenário pendente não encontrado."); return; }

            string locale = SessionState.GetString(KeyPendingLocale, "");
            if (string.IsNullOrEmpty(locale)) locale = scenario.defaultLocale;
            bool compare = SessionState.GetBool(KeyPendingCompare, true);
            bool exit = SessionState.GetBool(KeyPendingExit, false);

            RunRetention.Prune(OutputRoot, LastOutputDir); // old runs are working copies: what matters is in AICaptureGoldens
            string outputDir = Path.Combine(OutputRoot, scenario.scenarioId,
                DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + locale);

            Debug.Log($"[AICapture] Rodando cenário \"{scenario.scenarioId}\" ({locale}) → {outputDir}");
            ScenarioPlayer.Run(scenario, locale, outputDir, result => OnFinished(scenario, result, compare, exit));
        }

        private static void OnFinished(CaptureScenario scenario, CaptureResult result, bool compare, bool exit)
        {
            SessionState.SetString(KeyLastOutput, result.outputDir);
            SessionState.SetString(KeyLastScenario, scenario.scenarioId);
            File.WriteAllText(Path.Combine(OutputRoot, "latest.txt"), result.outputDir);

            VisualRegression.ScenarioReport report = null;
            if (compare)
                report = VisualRegression.CompareScenario(scenario, result.outputDir,
                    Path.Combine(GoldenRoot, scenario.scenarioId));

            foreach (var error in result.errors) Debug.LogWarning($"[AICapture] {error}");

            string summary = $"[AICapture] \"{scenario.scenarioId}\": {result.captures.Count} captura(s)";
            if (report != null)
            {
                summary += $", {report.compared} comparada(s), {report.failed} com diferença, {report.awaitingApproval} aguardando aprovação, {report.newGoldens} golden(s) nova(s)";
                foreach (var img in report.images.Where(i => i.hasDifferences))
                    Debug.LogWarning($"[AICapture] FALHOU {img.imageName}: {img.failReason}. " +
                                     $"{img.boundingBoxes.Count} região(ões). Overlay: {img.diffImagePath}");
            }
            summary += $". Saída: {result.outputDir}";

            if ((report != null && !report.passed) || !result.success) Debug.LogWarning(summary);
            else Debug.Log(summary);

            try { Completed?.Invoke(result, report); }
            catch (Exception e) { Debug.LogException(e); }

            if (exit) EditorApplication.delayCall += EditorApplication.ExitPlaymode;
        }

        // ---------- Menus ----------

        [MenuItem("Assets/AI Capture/Run Scenario", false, 1000)]
        private static void MenuRunSelected() => Run(Selection.activeObject as CaptureScenario);

        [MenuItem("Assets/AI Capture/Run Scenario (sem comparar)", false, 1001)]
        private static void MenuRunSelectedNoCompare() => Run(Selection.activeObject as CaptureScenario, null, false);

        [MenuItem("Assets/AI Capture/Run Scenario", true)]
        [MenuItem("Assets/AI Capture/Run Scenario (sem comparar)", true)]
        private static bool ValidateRunSelected() => Selection.activeObject is CaptureScenario;

        [MenuItem("Tools/AI Capture/Accept Last Run as Golden", false, 1)]
        private static void MenuAcceptLast() => AcceptAsGolden(LastScenarioId, LastOutputDir);

        [MenuItem("Tools/AI Capture/Open Last Output Folder", false, 2)]
        private static void MenuOpenLast()
        {
            if (Directory.Exists(LastOutputDir)) EditorUtility.RevealInFinder(Path.Combine(LastOutputDir, "manifest.json"));
            else Debug.LogWarning("[AICapture] Nenhuma execução nesta sessão.");
        }

        [MenuItem("Tools/AI Capture/Open Goldens Folder", false, 3)]
        private static void MenuOpenGoldens()
        {
            Directory.CreateDirectory(GoldenRoot);
            EditorUtility.RevealInFinder(GoldenRoot);
        }

        [MenuItem("Tools/AI Capture/List Hooks", false, 40)]
        private static void MenuListHooks()
        {
            CaptureHooks.Refresh();
            Debug.Log("[AICapture] Actions: " + string.Join(", ", CaptureHooks.ActionIds) +
                      "\nConditions: " + string.Join(", ", CaptureHooks.ConditionIds));
        }
    }
}
