using UnityEditor;
using UnityEngine;

namespace ChromeHypercube.AICapture
{
    [CustomEditor(typeof(CaptureScenario))]
    public class CaptureScenarioEditor : UnityEditor.Editor
    {
        private static string _localeOverride = "";

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var scenario = (CaptureScenario)target;
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Executar", EditorStyles.boldLabel);

            _localeOverride = EditorGUILayout.TextField(
                new GUIContent("Locale (opcional)", "Vazio = defaultLocale"), _localeOverride);

            using (new EditorGUI.DisabledScope(ScenarioPlayer.IsRunning))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("▶ Rodar + Comparar", GUILayout.Height(28)))
                        CaptureRunner.Run(scenario, _localeOverride, true);
                    if (GUILayout.Button("Só capturar", GUILayout.Height(28)))
                        CaptureRunner.Run(scenario, _localeOverride, false);
                }
            }

            bool hasLast = CaptureRunner.LastScenarioId == scenario.scenarioId &&
                           !string.IsNullOrEmpty(CaptureRunner.LastOutputDir);
            using (new EditorGUI.DisabledScope(!hasLast))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Aprovar última execução como golden"))
                    CaptureRunner.AcceptAsGolden(scenario.scenarioId, CaptureRunner.LastOutputDir);
                if (GUILayout.Button("Abrir pasta", GUILayout.Width(90)))
                    EditorUtility.RevealInFinder(System.IO.Path.Combine(CaptureRunner.LastOutputDir, "manifest.json"));
            }

            EditorGUILayout.HelpBox(
                "Capture: targetName = nome do arquivo\n" +
                "Wait: floatValue = segundos  |  WaitFrames: intValue = frames\n" +
                "WaitUntil: targetName = condição, floatValue = timeout\n" +
                "InvokeAction: targetName = action, args = argumentos\n" +
                "SetLocale: targetName = locale  |  SetTimeScale: floatValue\n" +
                "SetResolution: intValue = largura, extraIntValue = altura",
                MessageType.None);
        }
    }
}
