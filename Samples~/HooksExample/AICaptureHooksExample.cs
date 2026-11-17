using System.Collections;
using ChromeHypercube.AICapture;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Exemplo de hooks para o AI Capture. Coloque este componente num GameObject da sua cena
/// (métodos de instância precisam de um objeto ativo; métodos static funcionam sempre).
/// </summary>
public class AICaptureHooksExample : MonoBehaviour
{
    [SerializeField] private GameObject settingsPanel;

    public static string CurrentLocale = "en";

    // Chamado automaticamente no início do cenário e em cada passo SetLocale.
    [CaptureAction("set_locale")]
    private static void SetLocale(string[] args)
    {
        CurrentLocale = args.Length > 0 ? args[0] : "en";
        Debug.Log($"[Exemplo] Locale -> {CurrentLocale}");

        // Integre aqui com o seu sistema de localização, por exemplo (Unity Localization):
        // var locale = UnityEngine.Localization.Settings.LocalizationSettings.AvailableLocales.GetLocale(CurrentLocale);
        // UnityEngine.Localization.Settings.LocalizationSettings.SelectedLocale = locale;
    }

    // Action de instância: abre um painel.
    [CaptureAction("open_settings")]
    private void OpenSettings()
    {
        if (settingsPanel != null) settingsPanel.SetActive(true);
    }

    [CaptureAction("close_settings")]
    private void CloseSettings()
    {
        if (settingsPanel != null) settingsPanel.SetActive(false);
    }

    // Action que retorna IEnumerator: o cenário espera ela terminar.
    // Uso no cenário: InvokeAction, targetName = "load_scene", args = ["Shop"]
    [CaptureAction("load_scene")]
    private static IEnumerator LoadScene(string[] args)
    {
        var op = SceneManager.LoadSceneAsync(args[0]);
        while (!op.isDone) yield return null;
    }

    // Condição para WaitUntil: targetName = "settings_visible"
    [CaptureCondition("settings_visible")]
    private bool SettingsVisible() => settingsPanel != null && settingsPanel.activeInHierarchy;

    // Condição com argumento: WaitUntil, targetName = "object_active", args = ["ShopPanel"]
    [CaptureCondition("object_active")]
    private static bool ObjectActive(string[] args)
    {
        var go = GameObject.Find(args.Length > 0 ? args[0] : "");
        return go != null && go.activeInHierarchy;
    }
}
