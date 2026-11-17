using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ChromeHypercube.AICapture
{
    public class DescribeOptions
    {
        public int maxDepth = 8;
        public bool includeInactive = true;
        /// <summary>Checa overflow de TMP (só faz sentido em objetos renderizados na cena).</summary>
        public bool liveLayout = true;
        /// <summary>Lista os MonoBehaviours do projeto em cada nó.</summary>
        public bool listScripts = true;
        /// <summary>Não expande prefabs aninhados (eles têm descrição própria).</summary>
        public bool stopAtNestedPrefabs = false;
    }

    public class DescribeStats
    {
        public int nodeCount;
        public bool isUI;
        public bool truncated;
        public int missingScripts;
        public readonly SortedSet<string> scripts = new SortedSet<string>();
        public readonly SortedSet<string> nestedPrefabs = new SortedSet<string>();
    }

    /// <summary>
    /// Gera uma descrição em texto compacta de uma hierarquia — ideal para agentes de IA (MCP).
    /// Funciona em objetos da cena e direto em assets de prefab (sem instanciar).
    /// </summary>
    public static class DescribeUITool
    {
        public static string LastOutputFile =>
            Path.Combine(Path.GetTempPath(), "ai-capture", "ui_description.txt");

        public static string Describe(GameObject root, int maxDepth = 5, bool includeInactive = true)
            => Describe(root, new DescribeOptions { maxDepth = maxDepth, includeInactive = includeInactive }, out _);

        public static string Describe(GameObject root, DescribeOptions options, out DescribeStats stats)
        {
            stats = new DescribeStats();
            if (root == null) return "Objeto nulo.";
            options = options ?? new DescribeOptions();
            stats.isUI = root.transform is RectTransform;

            var sb = new StringBuilder();
            BuildNode(root.transform, root, 0, options, sb, stats);
            return sb.ToString();
        }

        public static string DescribeAllCanvases(int maxDepth = 6)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# UI snapshot — Screen {Screen.width}x{Screen.height}");
            var canvases = Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            var options = new DescribeOptions { maxDepth = maxDepth, includeInactive = false, liveLayout = true };
            foreach (var canvas in canvases)
            {
                if (!canvas.isRootCanvas) continue;
                sb.AppendLine($"## Canvas {canvas.name} (mode: {canvas.renderMode}, sortOrder: {canvas.sortingOrder})");
                sb.Append(Describe(canvas.gameObject, options, out _));
            }
            if (canvases.Length == 0) sb.AppendLine("(nenhum Canvas ativo encontrado)");
            return sb.ToString();
        }

        /// <summary>Caminho do asset de origem de uma instância de prefab (funciona dentro de prefab assets também).</summary>
        public static string GetSourcePrefabPath(GameObject go)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(go);
            string path = source != null ? AssetDatabase.GetAssetPath(source) : null;
            if (string.IsNullOrEmpty(path))
                path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            return string.IsNullOrEmpty(path) ? "?" : path;
        }

        public static int CountDescendants(Transform t) => t.GetComponentsInChildren<Transform>(true).Length - 1;

        private static void BuildNode(Transform t, GameObject root, int depth, DescribeOptions o, StringBuilder sb, DescribeStats s)
        {
            var go = t.gameObject;
            if (!o.includeInactive && !go.activeInHierarchy) return;

            s.nodeCount++;
            sb.Append(' ', depth * 2).Append("- ").Append(go.name);
            if (!go.activeSelf) sb.Append(" (Inactive)");

            bool nested = go != root && PrefabUtility.IsAnyPrefabInstanceRoot(go);
            if (nested)
            {
                string source = GetSourcePrefabPath(go);
                s.nestedPrefabs.Add(source);
                sb.Append(" [Prefab: ").Append(source).Append(']');
            }

            AppendRect(t, sb);
            AppendComponents(go, o, sb, s);

            bool stopHere = nested && o.stopAtNestedPrefabs;
            bool atLimit = depth >= o.maxDepth;
            if ((stopHere || atLimit) && t.childCount > 0)
            {
                int hidden = CountDescendants(t);
                if (stopHere) sb.Append($" (+{hidden} internos)");
                else { sb.Append($" (+{hidden} ocultos: profundidade máxima)"); s.truncated = true; }
                sb.AppendLine();
                return;
            }

            sb.AppendLine();
            foreach (Transform child in t)
                BuildNode(child, root, depth + 1, o, sb, s);
        }

        private static void AppendRect(Transform t, StringBuilder sb)
        {
            if (!(t is RectTransform rect)) return;
            var r = rect.rect;
            sb.Append($" | {r.width:F0}x{r.height:F0} pos({rect.anchoredPosition.x:F0},{rect.anchoredPosition.y:F0})");
            if (rect.anchorMin != rect.anchorMax)
                sb.Append($" anchor({rect.anchorMin.x:0.##},{rect.anchorMin.y:0.##})-({rect.anchorMax.x:0.##},{rect.anchorMax.y:0.##})");
        }

        private static void AppendComponents(GameObject go, DescribeOptions o, StringBuilder sb, DescribeStats s)
        {
            if (go.TryGetComponent<Canvas>(out var canvas) && !canvas.isRootCanvas)
                sb.Append(" | [Sub-Canvas]");

            if (go.TryGetComponent<Image>(out var img))
            {
                string sprite = img.sprite != null ? img.sprite.name : "none";
                sb.Append($" | Image(sprite:{sprite}, color:#{ColorUtility.ToHtmlStringRGBA(img.color)})");
            }
            else if (go.TryGetComponent<RawImage>(out var raw))
            {
                sb.Append($" | RawImage(tex:{(raw.texture != null ? raw.texture.name : "none")})");
            }

            if (go.TryGetComponent<TMP_Text>(out var tmp))
            {
                sb.Append($" | TMP(\"{Shorten(tmp.text, 40)}\", size:{tmp.fontSize:0.#})");
                if (o.liveLayout && tmp.isActiveAndEnabled && tmp.isTextOverflowing) sb.Append(" [OVERFLOW]");
            }
            else if (go.TryGetComponent<Text>(out var legacy))
            {
                sb.Append($" | Text(\"{Shorten(legacy.text, 40)}\", size:{legacy.fontSize})");
            }

            if (go.TryGetComponent<Button>(out var button))
                sb.Append(button.interactable ? " | [Button]" : " | [Button disabled]");
            else if (go.TryGetComponent<Toggle>(out var toggle))
                sb.Append($" | [Toggle {(toggle.isOn ? "on" : "off")}]");
            else if (go.TryGetComponent<Slider>(out var slider))
                sb.Append($" | [Slider {slider.value:0.##}]");
            else if (go.TryGetComponent<TMP_InputField>(out var input))
                sb.Append($" | [Input \"{Shorten(input.text, 20)}\"]");
            else if (go.TryGetComponent<TMP_Dropdown>(out var dropdown))
                sb.Append($" | [Dropdown {dropdown.options.Count} opções]");
            else if (go.TryGetComponent<ScrollRect>(out _))
                sb.Append(" | [ScrollRect]");

            if (go.TryGetComponent<GridLayoutGroup>(out _)) sb.Append(" | [GridLayout]");
            else if (go.TryGetComponent<HorizontalLayoutGroup>(out _)) sb.Append(" | [HLayout]");
            else if (go.TryGetComponent<VerticalLayoutGroup>(out _)) sb.Append(" | [VLayout]");

            if (!o.listScripts) return;

            List<string> names = null;
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null)
                {
                    s.missingScripts++;
                    (names ?? (names = new List<string>())).Add("⚠MissingScript");
                    continue;
                }
                if (comp is MonoBehaviour && !IsBuiltin(comp.GetType()))
                {
                    string n = comp.GetType().Name;
                    s.scripts.Add(n);
                    (names ?? (names = new List<string>())).Add(n);
                }
            }
            if (names != null) sb.Append(" | scripts: ").Append(string.Join(", ", names));
        }

        private static bool IsBuiltin(System.Type type)
        {
            string ns = type.Namespace ?? "";
            return ns.StartsWith("UnityEngine") || ns.StartsWith("TMPro") || ns.StartsWith("Unity.");
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r", "").Replace("\n", "\\n").Replace("\"", "'");
            return text.Length > max ? text.Substring(0, max - 3) + "..." : text;
        }

        private static void Output(string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastOutputFile));
            File.WriteAllText(LastOutputFile, text);
            EditorGUIUtility.systemCopyBuffer = text;
            Debug.Log($"[AICapture] Descrição copiada para a área de transferência e salva em {LastOutputFile}\n{text}");
        }

        [MenuItem("GameObject/AI Capture/Describe UI (copiar)", false, 49)]
        private static void MenuDescribeSelected()
        {
            var go = Selection.activeGameObject;
            if (go != null) Output(Describe(go, new DescribeOptions { maxDepth = 8 }, out _));
        }

        [MenuItem("GameObject/AI Capture/Describe UI (copiar)", true)]
        private static bool ValidateDescribeSelected() => Selection.activeGameObject != null;

        [MenuItem("Tools/AI Capture/Describe All Canvases (copiar)", false, 20)]
        private static void MenuDescribeAll() => Output(DescribeAllCanvases());
    }
}
