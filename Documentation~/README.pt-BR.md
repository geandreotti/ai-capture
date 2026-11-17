# AI Capture & Visual Pipeline (com.chromehypercube.ai-capture)

Pacote UPM para Unity 2022.3+ que automatiza:

1. **Capturas de tela roteirizadas** — um asset `CaptureScenario` descreve passos (esperar, clicar via código, trocar idioma, mudar resolução, capturar).
2. **Descrição de UI em texto** — um resumo compacto da hierarquia de Canvas para colar num agente de IA (MCP).
3. **Regressão visual com Golden Images** — compara cada captura com uma imagem aprovada e gera overlay de diferenças + relatório JSON.

---

## 1. Instalação

Escolha **uma** das opções:

**A) Embutido no projeto (mais simples)**
Copie a pasta `com.chromehypercube.ai-capture/` para dentro de `SeuProjeto/Packages/`. A Unity detecta sozinha.

**B) Do disco**
Window → Package Manager → `+` → *Add package from disk…* → selecione o `package.json` desta pasta.

**C) Do Git**
Suba esta pasta como raiz de um repositório e use `+` → *Add package from git URL…*

Depois de instalado: Package Manager → AI Capture → aba **Samples** → *Import* em "Exemplo de hooks" (opcional, recomendado).

---

## 2. Criando um cenário

1. No Project: botão direito → **Create → AI Capture → Capture Scenario**.
2. Preencha:
   - `scenarioId` — nome da pasta de saída/goldens (ex.: `home`).
   - `defaultLocale` — ex.: `en` ou `pt-BR`.
   - `targetWidth` / `targetHeight` — resolução da Game View durante a captura (ex.: 1080 × 2400).
   - `steps` — a lista de passos.

### Tipos de passo

| Passo | Campos usados | O que faz |
|---|---|---|
| `Wait` | `floatValue` = segundos | Espera em tempo real |
| `WaitFrames` | `intValue` = frames | Espera N frames |
| `WaitUntil` | `targetName` = id da condição, `floatValue` = timeout (padrão 10s), `args` | Espera um `[CaptureCondition]` ficar `true` |
| `Capture` | `targetName` = nome do arquivo | Salva `nome_{locale}.png` |
| `SetLocale` | `targetName` = locale | Chama a action `set_locale` e muda o sufixo dos arquivos |
| `SetResolution` | `intValue` = largura, `extraIntValue` = altura | Muda a Game View no meio do cenário |
| `SetTimeScale` | `floatValue` | Ex.: `0` para congelar animações antes de capturar |
| `InvokeAction` | `targetName` = id da action, `args` | Chama um método `[CaptureAction]` |

### Exemplo de cenário "home"

```
0  WaitUntil     targetName: main_menu_ready   floatValue: 15
1  SetTimeScale  floatValue: 0
2  WaitFrames    intValue: 2
3  Capture       targetName: home
4  InvokeAction  targetName: open_settings
5  WaitUntil     targetName: settings_visible
6  Capture       targetName: settings
7  SetLocale     targetName: pt-BR
8  WaitFrames    intValue: 2
9  Capture       targetName: settings
```

Resultado: `home_en.png`, `settings_en.png`, `settings_pt-BR.png`.

---

## 3. Hooks: ligando o cenário ao seu jogo

Marque métodos no seu código:

```csharp
using ChromeHypercube.AICapture;

public class MenuHooks : MonoBehaviour
{
    [CaptureAction("open_settings")]
    void OpenSettings() => settingsPanel.SetActive(true);

    [CaptureCondition("settings_visible")]
    bool SettingsVisible() => settingsPanel.activeInHierarchy;

    [CaptureAction("set_locale")]            // chamado no início e em cada SetLocale
    static void SetLocale(string[] args) { /* troque o idioma para args[0] */ }

    [CaptureAction("load_scene")]            // IEnumerator = o cenário espera terminar
    static IEnumerator LoadScene(string[] args) { ... }
}
```

Assinaturas aceitas:
- Action: `void M()`, `void M(string[] args)`, `IEnumerator M()`, `IEnumerator M(string[] args)`
- Condition: `bool M()` ou `bool M(string[] args)`
- Métodos de instância precisam estar num MonoBehaviour **ativo na cena**. Métodos `static` funcionam sempre.

Para conferir o que o pacote encontrou: **Tools → AI Capture → List Hooks**.

---

## 4. Rodando

Qualquer uma destas formas:

- Selecione o asset do cenário → no Inspector clique **▶ Rodar + Comparar**.
- Botão direito no asset → **AI Capture → Run Scenario**.
- Por código (útil para agentes de IA via MCP / `execute_code`):
  ```csharp
  ChromeHypercube.AICapture.CaptureRunner.RunByPath("Assets/Capture/Home.asset", "pt-BR");
  ```

O pacote entra em Play Mode, ajusta a Game View, executa os passos, compara com as goldens, restaura a resolução original e sai do Play Mode. Se você já estiver em Play Mode, ele roda na sessão atual e não sai.

### Onde ficam os arquivos

- **Saída de cada execução:** `<pasta temporária do sistema>/ai-capture/<scenarioId>/<data_hora>_<locale>/`
  - `*.png` — capturas
  - `manifest.json` — capturas e erros da execução
  - `regression_report.json` — resultado da comparação
  - `*_diff.png` — overlay: vermelho = pixel diferente, amarelo = caixa da região alterada
- **Última execução:** `<temp>/ai-capture/latest.txt` contém o caminho, e **Tools → AI Capture → Open Last Output Folder** abre a pasta.
- **Goldens:** `<projeto>/AICaptureGoldens/<scenarioId>/` (fora de `Assets/`, então a Unity não importa as imagens). **Versione esta pasta no Git.**

---

## 5. Fluxo de regressão visual

1. **Primeira execução:** não existe golden → cada captura vira golden automaticamente (`isNewGolden: true`).
2. **Execuções seguintes:** cada captura é comparada com a golden de mesmo nome.
   - Tolerância por canal: 5% (`colorTolerance = 0.05`).
   - Falha se mais de 1% dos pixels diferirem (`maxMismatchPercent = 0.01`).
   - Tamanho diferente da golden = falha (`sizeMismatch: true`).
3. **Mudança intencional?** Rode de novo e clique **Aprovar última execução como golden** (ou **Tools → AI Capture → Accept Last Run as Golden**).

As `boundingBoxes` do relatório estão em pixels com origem no **canto superior esquerdo** — prontas para um agente de IA apontar "o que mudou".

---

## 6. Descrição de UI para IA

- Selecione um GameObject → botão direito → **AI Capture → Describe UI (copiar)**.
- Ou **Tools → AI Capture → Describe All Canvases (copiar)** (melhor em Play Mode).

O texto vai para a área de transferência e para `<temp>/ai-capture/ui_description.txt`. Exemplo:

```
## Canvas MainCanvas (mode: ScreenSpaceOverlay, sortOrder: 0)
- MainCanvas | 1080x2400 pos(540,1200)
  - PlayButton | 400x120 pos(0,-300) | Image(sprite:btn_green, color:#FFFFFFFF) | [Button]
    - Label | 400x120 pos(0,0) | TMP("Jogar", size:48)
  - Title | 900x200 pos(0,600) | TMP("Meu Jogo Incrível de Ave...", size:96) [OVERFLOW]
```

Por código: `DescribeUITool.Describe(gameObject)` ou `DescribeUITool.DescribeAllCanvases()`.

---

## 7. Contexto do projeto para agentes de IA (prefabs e cenas)

Cenas da Unity são arquivos YAML enormes. Para entender um menu, uma IA teria que ler a cena inteira. Este recurso gera uma pasta de contexto compacta e sempre atualizada, para que o agente leia só o que precisa.

### Gerar

**Tools → AI Capture → Exportar contexto para IA (prefabs + cenas abertas)**

Isso cria, na raiz do projeto (fora de `Assets/`):

```
AIContext/
├── README.md        ← instruções e legenda para o agente
├── index.txt        ← uma linha por cena e por prefab (leia primeiro)
├── prefabs/
│   └── UI/Menus/SettingsMenu.txt   ← descrição de Assets/UI/Menus/SettingsMenu.prefab
└── scenes/
    └── Scenes/Main.txt             ← resumo da cena + candidatos a prefab
```

Exemplo de linha do `index.txt`:

```
- Assets/UI/Menus/SettingsMenu.prefab | UI | 34 nós | scripts: SettingsMenu, LocalizedText | usa: Button_Primary, Toggle_Default
```

Exemplo de descrição de prefab:

```
# Assets/UI/Menus/SettingsMenu.prefab | UI | 34 nós | scripts: SettingsMenu | usa: Button_Primary
guid: 3f2a...

- SettingsMenu | 1080x2400 pos(0,0) anchor(0,0)-(1,1) | scripts: SettingsMenu
  - Header | 1080x200 pos(0,-100) | [HLayout]
    - Title | 600x120 pos(0,0) | TMP("Configurações", size:64)
  - SaveButton [Prefab: Assets/UI/Widgets/Button_Primary.prefab] | 400x120 pos(0,-900) (+2 internos)
```

### Resumo de cenas e candidatos a prefab

O resumo de cada cena mostra os prefabs usados, os objetos soltos e marca com `⚠ CANDIDATO A PREFAB` os grupos grandes fora de prefabs (por padrão, 15 objetos ou mais; Canvas raiz não conta). Também indica instâncias com overrides na cena, que vale aplicar no prefab para enxugar o arquivo da cena. É o mapa para a otimização "cena → prefabs".

### Atualização automática

Depois da primeira exportação, o pacote mantém tudo em dia sozinho: salvar, mover ou apagar um prefab atualiza a descrição dele, e salvar uma cena atualiza o resumo dela.

Para exportar só alguns itens: botão direito em prefabs ou pastas → **AI Capture → Exportar descrição para IA**.

### Configurações

**Edit → Project Settings → AI Capture** (ou **Tools → AI Capture → Configurações…**):

| Opção | Padrão | Para que serve |
|---|---|---|
| Pasta de saída | `AIContext` | Onde o contexto é gerado |
| Pastas incluídas / excluídas | `Assets` / — | Ex.: excluir `Assets/ThirdParty` |
| Atualizar automaticamente | ligado | Regera ao salvar prefabs e cenas |
| Não expandir prefabs aninhados | ligado | Descrições bem menores; cada prefab tem a sua |
| Profundidade (prefabs / cenas) | 12 / 6 | Limite da hierarquia descrita |
| Mínimo p/ candidato a prefab | 15 | Tamanho do grupo para sugerir conversão |

As configurações ficam em `ProjectSettings/AICaptureSettings.asset` e vão para o Git com o projeto.

### Dica: versione a pasta `AIContext/`

Se você fizer commit dela, agentes que trabalham **sem a Unity aberta** (por exemplo, rodando na nuvem a partir do repositório) também conseguem entender a estrutura da UI e das cenas.

### Por código

```csharp
AIContextExporter.ExportAll();
AIContextExporter.ExportPrefab("Assets/UI/Menus/SettingsMenu.prefab");
AIContextExporter.ExportScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
```

---

## 8. Conversão de grupos da cena em prefabs (por você ou pela IA)

### Pelo Editor

Botão direito num objeto da Hierarchy → **AI Capture → Converter em prefab…**

Antes de converter, o pacote analisa o grupo. Se algum objeto dentro dele aponta para algo **fora** do grupo (por exemplo, o OnClick de um botão chamando um GameManager da cena), aparece a lista dessas ligações. Elas continuam funcionando nesta cena, como override, mas chegam **vazias** se o prefab for usado em outra cena. Você escolhe converter mesmo assim ou cancelar e religar essas referências por código antes.

**Tools → AI Capture → Listar candidatos a prefab (cena ativa)** mostra os grupos sugeridos, já analisados.

O prefab é salvo em `Assets/Prefabs/Converted/` (configurável em Project Settings → AI Capture).

### Pela IA

A classe `AICaptureAgentAPI` reúne métodos simples que recebem texto e devolvem JSON. Funcionam com qualquer ferramenta que execute C# no Editor, como o MCP for Unity ou o Unity CLI. Cada resposta também é gravada em `AIContext/last_action.json`.

| Método | O que faz |
|---|---|
| `ListPrefabCandidates()` | Candidatos da cena ativa, já analisados |
| `AnalyzeForPrefab("Canvas/Settings")` | Riscos antes de converter; não altera nada |
| `ConvertToPrefab("Canvas/Settings")` | Cria o prefab, salva a cena e atualiza o contexto |
| `RunScenario("Assets/Capture/Menus.asset")` | Inicia uma captura (assíncrona) |
| `GetLastRunReport()` | Relatório da última captura |
| `AcceptLastRunAsGolden()` | Aprova as capturas atuais |
| `ExportContext()` | Regera a pasta `AIContext/` |

`ConvertToPrefab` **recusa** a conversão se houver referências externas, a menos que receba `allowExternalReferences: true`. Assim o agente precisa decidir conscientemente o que fazer com elas.

Caminhos ambíguos (dois objetos com o mesmo caminho) são recusados. Para escolher a cena, use o prefixo: `"Assets/Scenes/Main.unity:Canvas/Settings"`.

Fluxo seguro sugerido para um agente:

1. `RunScenario(...)` para gerar a golden "antes", se ainda não existir.
2. `ListPrefabCandidates()` e `AnalyzeForPrefab(...)`.
3. `ConvertToPrefab(...)`.
4. `RunScenario(...)` de novo e depois `GetLastRunReport()`. Se `passed` for `true`, a conversão não mudou nada na tela.

Converter tem desfazer (Ctrl+Z) na cena, mas o arquivo `.prefab` criado continua no projeto. Mesmo assim, faça commit no Git antes de deixar um agente converter vários grupos seguidos.

---

## 9. Dicas e limitações

- A Game View precisa estar **aberta e visível** (não minimizada) para a captura sair na resolução certa.
- Desmarque **Low Resolution Aspect Ratios** na Game View, ou a imagem sai com metade da resolução.
- Use `SetTimeScale 0` + `WaitFrames 2` antes de capturar para eliminar diferenças de animação.
- A troca de resolução usa APIs internas da Unity via reflexão; se uma versão futura mudá-las, você verá um aviso no Console e a captura sai na resolução atual da Game View.
- Não roda em batchmode com `-nographics` (sem GPU não há screenshot).
- O pacote só cria e remove o **próprio** tamanho "AI Capture" na Game View; seus tamanhos personalizados não são tocados.
