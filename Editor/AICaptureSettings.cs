using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ChromeHypercube.AICapture
{
    /// <summary>
    /// Configurações do pacote por projeto, salvas em ProjectSettings/AICaptureSettings.asset
    /// (vai para o controle de versão junto com o projeto).
    /// </summary>
    [FilePath("ProjectSettings/AICaptureSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class AICaptureSettings : ScriptableSingleton<AICaptureSettings>
    {
        [Tooltip("Pasta (relativa à raiz do projeto) onde o contexto para IA é gerado. Fica fora de Assets/, então a Unity não importa os .txt.")]
        public string contextFolder = "AIContext";

        [Tooltip("Pastas onde procurar prefabs. Vazio = Assets inteira.")]
        public List<string> includeFolders = new List<string> { "Assets" };

        [Tooltip("Pastas ignoradas (ex.: Assets/ThirdParty, Assets/Plugins).")]
        public List<string> excludeFolders = new List<string>();

        [Tooltip("Regera a descrição quando um prefab é salvo, movido ou apagado, e o resumo quando uma cena é salva. Só começa a valer depois da primeira exportação completa.")]
        public bool autoUpdate = true;

        [Tooltip("Profundidade máxima da hierarquia descrita em cada prefab.")]
        [Range(1, 32)] public int maxDepth = 12;

        [Tooltip("Profundidade máxima no resumo de cenas (cenas grandes ficam ilegíveis com valores altos).")]
        [Range(1, 16)] public int sceneMaxDepth = 6;

        [Tooltip("Não descreve o conteúdo de prefabs aninhados — eles têm o próprio arquivo. Deixa as descrições bem menores.")]
        public bool stopAtNestedPrefabs = true;

        [Tooltip("Nas cenas, um grupo de objetos soltos (fora de prefabs) com pelo menos esta quantidade de objetos é marcado como candidato a virar prefab.")]
        [Min(2)] public int sceneCandidateThreshold = 15;

        [Tooltip("Grupos maiores que isto nao sao sugeridos (a busca desce para os filhos). Evita sugerir a raiz inteira da UI.")]
        [Min(15)] public int sceneCandidateMaxSize = 200;

        [Tooltip("Permite converter grupos que apontam para objetos fora deles (ex.: OnClick chamando um manager da cena). A ligacao continua funcionando nesta cena como override; so chega vazia se o prefab for usado em outra cena. Deixe ligado se os prefabs servem so para organizar a cena.")]
        public bool allowExternalReferences = false;

        [Tooltip("Pasta (dentro de Assets) onde os prefabs criados pela conversão automática são salvos.")]
        public string prefabOutputFolder = "Assets/Prefabs/Converted";

        public string ContextRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", string.IsNullOrWhiteSpace(contextFolder) ? "AIContext" : contextFolder));

        public string[] SearchFolders
        {
            get
            {
                var valid = includeFolders
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Select(f => f.Replace('\\', '/').TrimEnd('/'))
                    .Where(AssetDatabase.IsValidFolder)
                    .ToArray();
                return valid.Length > 0 ? valid : new[] { "Assets" };
            }
        }

        /// <summary>True se o caminho é um .prefab dentro das pastas incluídas e fora das excluídas.</summary>
        public bool IsTracked(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                return false;

            string p = assetPath.Replace('\\', '/');
            bool included = SearchFolders.Any(f => IsInFolder(p, f));
            return included && !excludeFolders.Any(f => IsInFolder(p, f));
        }

        public void SaveSettings() => Save(true);

        private static bool IsInFolder(string path, string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return false;
            folder = folder.Replace('\\', '/').TrimEnd('/');
            return path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class AICaptureSettingsProvider
    {
        public const string SettingsPath = "Project/AI Capture";

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider(SettingsPath, SettingsScope.Project)
            {
                label = "AI Capture",
                keywords = new HashSet<string>(new[] { "AI", "Capture", "Prefab", "Context", "Golden", "Screenshot" }),
                guiHandler = _ => DrawGUI()
            };
        }

        private static void DrawGUI()
        {
            var settings = AICaptureSettings.instance;
            settings.hideFlags &= ~HideFlags.NotEditable;
            var so = new SerializedObject(settings);
            so.Update();

            EditorGUILayout.LabelField("Contexto para IA (prefabs e cenas)", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AICaptureSettings.contextFolder)), new GUIContent("Pasta de saída"));
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AICaptureSettings.includeFolders)), new GUIContent("Pastas incluídas"), true);
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AICaptureSettings.excludeFolders)), new GUIContent("Pastas excluídas"), true);
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AICaptureSettings.autoUpdate)), new GUIContent("Atualizar automaticamente"));
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AICaptureSettings.stopAtNestedPrefabs)), new GUIContent("Não expandir prefabs aninhados"));
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AICaptureSettings.maxDepth)), new GUIContent("Profundidade (prefabs)"));
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AICaptureSettings.sceneMaxDepth)), new GUIContent("Profundidade (cenas)"));
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AICaptureSettings.sceneCandidateMaxSize)));
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AICaptureSettings.allowExternalReferences)), new GUIContent("Permitir referências externas"));
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AICaptureSettings.sceneCandidateThreshold)), new GUIContent("Mínimo p/ candidato a prefab"));
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AICaptureSettings.prefabOutputFolder)), new GUIContent("Pasta dos prefabs convertidos"));
            if (EditorGUI.EndChangeCheck())
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                settings.SaveSettings();
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Exportar contexto agora", GUILayout.Height(26)))
                    AIContextExporter.ExportAll();
                if (GUILayout.Button("Abrir pasta", GUILayout.Height(26), GUILayout.Width(110)))
                    AIContextExporter.RevealContextFolder();
            }

            EditorGUILayout.HelpBox(
                AIContextExporter.HasContext
                    ? $"Contexto em: {settings.ContextRoot}"
                    : "Nenhum contexto gerado ainda. A atualização automática começa depois da primeira exportação.",
                MessageType.Info);
        }
    }
}
