# AI Capture & Visual Pipeline

`com.chromehypercube.ai-capture` · versão 1.7.0 · Unity 2022.3 ou mais nova (testado no Unity 6)

Pacote de Editor para Unity que deixa a interface do seu jogo **verificável por pessoas e por agentes de IA**: roda roteiros de captura de tela, compara cada tela com uma imagem aprovada, explica em texto o que mudou e onde, e gera um contexto compacto das cenas e prefabs para um agente ler sem abrir arquivos YAML enormes.

## O que ele faz

| Recurso | Para quê |
|---|---|
| **Cenários de captura** | Um asset descreve os passos (esperar, clicar, rolar, trocar idioma, mudar resolução, capturar). O pacote entra em Play Mode, executa tudo e sai sozinho. |
| **Regressão visual com golden images** | Cada captura é comparada com a imagem aprovada. Sai um relatório com o motivo da falha, as caixas do que mudou e recortes *golden \| atual \| diff*. |
| **Mudanças declaradas** | Você (ou o agente) avisa antes o que deve mudar. O que bate fica *aguardando aprovação*; o que mudou fora disso continua falhando. |
| **Descrição de UI em texto** | Um resumo da hierarquia de Canvas (tamanhos, textos, componentes, estouros) pronto para colar num agente. |
| **Contexto do projeto para IA** | A pasta `AIContext/` descreve cada prefab e resume cada cena em poucos KB, e se atualiza sozinha. |
| **Cena → prefabs com segurança** | Sugere grupos que deveriam virar prefab, analisa referências para fora do grupo e converte, pelo menu ou por agente. |
| **API para agentes** | `AICaptureAgentAPI`: métodos que recebem texto e devolvem JSON, para usar com MCP for Unity, Unity CLI ou qualquer ferramenta que rode C# no Editor. |
| **Limpeza automática** | A pasta de saída temporária não cresce sem limite (ver [Onde ficam os arquivos](#onde-ficam-os-arquivos)). |

---

## 1. Instalação

**Pelo Git (recomendado).** No `Packages/manifest.json` do projeto:

```json
"com.chromehypercube.ai-capture": "https://github.com/geandreotti/ai-capture.git#v1.7.0"
```

ou Window → Package Manager → `+` → *Add package from git URL…* com o mesmo endereço. A tag (`#v1.7.0`) fixa a versão; sem ela, o projeto pega o último commit da `main`. Repositório privado precisa de login no Git da máquina (o Package Manager usa o Git do sistema).

**Embutido.** Copie a pasta do repositório para `SeuProjeto/Packages/com.chromehypercube.ai-capture/`. Útil para editar o pacote junto com o projeto; lembre de trazer as mudanças de volta para o repositório.

Depois de instalar: Package Manager → AI Capture → aba **Samples** → *Import* em "Exemplo de hooks" (opcional, recomendado).

Dependências: `com.unity.ugui` e `com.unity.textmeshpro`.

---

## 2. Início rápido

1. **Create → AI Capture → Capture Scenario** e dê um `scenarioId` (ex.: `menu`).
2. Adicione os passos: `WaitUntil` (sua tela pronta), `WaitForStable`, `Capture` com o nome da tela.
3. Escreva os hooks que o cenário chama (ex.: `[CaptureCondition("menu_ready")]`) — ver [Hooks](#4-hooks-ligando-o-cenário-ao-seu-jogo).
4. Selecione o asset e clique **▶ Rodar + Comparar**. Na primeira vez não há golden: confira as imagens e aprove com **Tools → AI Capture → Accept Last Run as Golden**.
5. Faça commit de `AICaptureGoldens/`. A partir daí, cada execução diz se a tela mudou.

---

## 3. Cenários

No Project: botão direito → **Create → AI Capture → Capture Scenario**.

| Campo | Padrão | O que é |
|---|---|---|
| `scenarioId` | `home` | Nome da pasta de saída e das goldens |
| `defaultLocale` | `en` | Idioma usado quando a execução não informa outro |
| `targetWidth` / `targetHeight` | 1080 × 2400 | Resolução da Game View durante a captura |
| `colorTolerance` | 0,05 | Diferença por canal ignorada |
| `maxMismatchPercent` | 0,01 | Falha se mais de 1% dos pixels mudar |
| `maxRegionPixels` | 0 (desligado) | Falha se qualquer área alterada passar desse tamanho, mesmo com poucos pixels (pega um título quebrado) |
| `strictGoldens` | ligado | Captura sem golden falha em vez de virar golden sozinha |
| `rules` | — | Regras por captura (ver [Regras e máscaras](#regras-por-captura-e-máscaras)) |
| `steps` | — | A lista de passos |

### Passos

| Passo | Campos | O que faz |
|---|---|---|
| `Wait` | `floatValue` = segundos | Espera em tempo real |
| `WaitFrames` | `intValue` = frames | Espera N frames |
| `WaitUntil` | `targetName` = condição, `floatValue` = timeout (10 s), `args` | Espera um `[CaptureCondition]` ficar verdadeiro |
| `WaitForStable` | `floatValue` = timeout (5 s) | Espera a tela parar de mudar; estourar só gera aviso |
| `Capture` | `targetName` = nome | Salva `nome_{locale}.png` |
| `SetLocale` | `targetName` = idioma | Chama a action `set_locale` e muda o sufixo dos arquivos |
| `SetResolution` | `intValue` = largura, `extraIntValue` = altura | Muda a Game View no meio do cenário |
| `SetTimeScale` | `floatValue` | Ex.: `0` para congelar animações antes de capturar |
| `InvokeAction` | `targetName` = action, `args` | Chama um `[CaptureAction]` |

Ações e condição que já vêm no pacote, sem hook: `click` (args[0] = caminho ou nome do objeto), `scroll` (args[0] = ScrollRect, args[1] = 0..1) e a condição `exists`.

### Exemplo

```
0  WaitUntil     targetName: menu_ready      floatValue: 15
1  SetTimeScale  floatValue: 0
2  WaitForStable
3  Capture       targetName: home
4  InvokeAction  targetName: click           args: ["Canvas/Tabs/Settings"]
5  WaitUntil     targetName: settings_visible
6  Capture       targetName: settings
7  SetLocale     targetName: pt-BR
8  WaitFrames    intValue: 2
9  Capture       targetName: settings
```

Resultado: `home_en.png`, `settings_en.png`, `settings_pt-BR.png`.

---

## 4. Hooks: ligando o cenário ao seu jogo

```csharp
using ChromeHypercube.AICapture;

public class MenuHooks : MonoBehaviour
{
    [CaptureCondition("menu_ready")]
    bool MenuReady() => mainMenu.activeInHierarchy;

    [CaptureAction("open_settings")]
    void OpenSettings() => settingsPanel.SetActive(true);

    [CaptureAction("set_locale")]            // chamado no início e em cada SetLocale
    static void SetLocale(string[] args) { /* troque o idioma para args[0] */ }

    [CaptureAction("load_scene")]            // IEnumerator: o cenário espera terminar
    static IEnumerator LoadScene(string[] args) { ... }
}
```

- Action: `void M()`, `void M(string[] args)`, `IEnumerator M()` ou `IEnumerator M(string[] args)`.
- Condition: `bool M()` ou `bool M(string[] args)`.
- Métodos de instância precisam estar num MonoBehaviour **ativo na cena**; `static` funciona sempre.
- Hooks costumam ser só de Editor: coloque-os atrás de `#if UNITY_EDITOR` ou numa pasta `Editor` quando não precisarem existir no build.

**Tools → AI Capture → List Hooks** mostra o que o pacote encontrou.

---

## 5. Rodando

- Selecione o asset → **▶ Rodar + Comparar** no Inspector.
- Botão direito no asset → **AI Capture → Run Scenario** (ou *sem comparar*, que só gera as imagens).
- Por código, inclusive por agente via MCP:
  ```csharp
  ChromeHypercube.AICapture.CaptureRunner.RunByPath("Assets/Capture/Menu.asset", "pt-BR");
  ```

O pacote espera o Editor terminar de compilar ou importar, entra em Play Mode, ajusta a Game View, executa os passos, compara, restaura a resolução e sai. Se você já estiver em Play Mode, ele roda nessa sessão e não sai.

### Onde ficam os arquivos

- **Execuções:** `<temp>/ai-capture/<scenarioId>/<data_hora>_<locale>/`
  - `*.png` — capturas
  - `manifest.json` — capturas e erros
  - `regression_report.json` — resultado da comparação
  - `report.md` — resumo por captura, para leitura rápida
  - `*_diff.png` — overlay: vermelho = pixel diferente, amarelo = região alterada, azul = região ignorada
  - `<captura>_box<N>.png` — recorte *golden \| atual \| diff* de cada região alterada
- **Última execução:** `<temp>/ai-capture/latest.txt` tem o caminho; **Tools → AI Capture → Open Last Output Folder** abre a pasta.
- **Goldens:** `<projeto>/AICaptureGoldens/<scenarioId>/`, fora de `Assets/` (a Unity não importa as imagens). **Versione esta pasta.**

**Limpeza automática** (1.7.0). As execuções são cópias de trabalho; o que vale fica nas goldens. Ao iniciar uma execução, o pacote mantém as **3 mais novas** de cada cenário e apaga as com **mais de 7 dias**. Uma execução aguardando aprovação fica até 30 dias, e a última execução nunca é apagada. Pastas e arquivos avulsos na raiz de `ai-capture` saem depois de 7 dias. Aprovar **todas** as capturas de uma execução apaga essa execução (as imagens já foram para as goldens); aprovar só uma parte a mantém.

---

## 6. Regressão visual

1. Cada captura é comparada com a golden de mesmo nome. Tamanho diferente = falha.
2. O relatório traz `status`: `passed`, `needs_approval` (mudou só o que foi declarado) ou `failed`, e cada falha vem com `failReason`.
3. As `boundingBoxes` estão em pixels, com origem no **canto superior esquerdo**: prontas para um agente apontar o que mudou.
4. Mudança intencional: rode de novo e aprove com **Accept Last Run as Golden**, ou só uma captura com `AICaptureAgentAPI.AcceptLastRunAsGolden("settings")`.

### Mudança de UI declarada

1. Depois de editar a UI: `AICaptureAgentAPI.DeclareExpectedChange("menu", "settings", "título maior", x, y, w, h)`. A região é opcional; `"*"` vale para qualquer captura. As declarações ficam em `AICaptureGoldens/<cenário>/expected_changes.json`.
2. Rode o cenário. O que foi declarado e não mudou aparece em `declaredButUnchanged`.
3. Revise os recortes das capturas `AWAITING APPROVAL` e aprove; aprovar limpa a declaração.

### Regras por captura e máscaras

`rules` sobrescreve os limites do cenário por captura. `captureName` casa com o nome do passo `Capture` (e com `nome_pt`, `nome_en`…); use `-1` para herdar o valor do cenário. `ignoreRegions` são retângulos em pixels (origem no canto superior esquerdo) tirados da comparação, ideais para conteúdo ao vivo. A regra por região exige 5% de densidade de pixels alterados na caixa, para não reprovar ruído de antialiasing em títulos largos.

---

## 7. Descrição de UI para IA

- Botão direito num GameObject → **AI Capture → Describe UI (copiar)**.
- **Tools → AI Capture → Describe All Canvases (copiar)** (melhor em Play Mode).

O texto vai para a área de transferência e para `<temp>/ai-capture/ui_description.txt`:

```
## Canvas MainCanvas (mode: ScreenSpaceOverlay, sortOrder: 0)
- MainCanvas | 1080x2400 pos(540,1200)
  - PlayButton | 400x120 pos(0,-300) | Image(sprite:btn_green, color:#FFFFFFFF) | [Button]
    - Label | 400x120 pos(0,0) | TMP("Jogar", size:48)
  - Title | 900x200 pos(0,600) | TMP("Meu Jogo Incrível de Ave...", size:96) [OVERFLOW]
```

Por código: `DescribeUITool.Describe(gameObject)` ou `DescribeUITool.DescribeAllCanvases()`.

---

## 8. Contexto do projeto para agentes (`AIContext/`)

Cenas da Unity são YAML enormes. **Tools → AI Capture → Exportar contexto para IA** cria, na raiz do projeto:

```
AIContext/
├── README.md        ← instruções e legenda para o agente
├── index.txt        ← uma linha por cena e por prefab (leia primeiro)
├── prefabs/…        ← uma descrição por prefab
└── scenes/…         ← resumo de cada cena + candidatos a prefab
```

```
- Assets/UI/Menus/SettingsMenu.prefab | UI | 34 nós | scripts: SettingsMenu, LocalizedText | usa: Button_Primary, Toggle_Default
```

Depois da primeira exportação, tudo se atualiza sozinho ao salvar, mover ou apagar prefabs e ao salvar cenas. Para exportar só alguns itens: botão direito em prefabs ou pastas → **AI Capture → Exportar descrição para IA**. Por código: `AIContextExporter.ExportAll()`, `ExportPrefab(path)`, `ExportScene(scene)`.

**Dica:** versione `AIContext/`. Agentes que trabalham sem a Unity aberta (por exemplo, na nuvem, a partir do repositório) também entendem a UI e as cenas.

### Candidatos a prefab

O resumo de cena marca com `⚠ CANDIDATO A PREFAB` grupos de objetos soltos entre `sceneCandidateThreshold` (15) e `sceneCandidateMaxSize` (200); grupos maiores são examinados por dentro, em vez de sugerir a UI inteira. Estruturas repetidas entre irmãos (cards, linhas de lista) também contam, com "xN repetidos". Também aparecem instâncias com overrides que valem ser aplicados no prefab.

### Configurações

**Edit → Project Settings → AI Capture** (ou **Tools → AI Capture → Configurações…**), salvas em `ProjectSettings/AICaptureSettings.asset`:

| Opção | Padrão | Para que serve |
|---|---|---|
| Pasta de saída | `AIContext` | Onde o contexto é gerado |
| Pastas incluídas / excluídas | `Assets` / — | Ex.: excluir `Assets/ThirdParty` |
| Atualizar automaticamente | ligado | Regera ao salvar prefabs e cenas |
| Não expandir prefabs aninhados | ligado | Descrições bem menores; cada prefab tem a sua |
| Profundidade (prefabs / cenas) | 12 / 6 | Limite da hierarquia descrita |
| Candidato a prefab (mínimo / máximo) | 15 / 200 | Tamanho dos grupos sugeridos |
| Permitir referências externas | desligado | Ligado, a conversão só avisa das referências para fora do grupo em vez de recusar |
| Pasta dos prefabs convertidos | `Assets/Prefabs/Converted` | Onde a conversão salva |

---

## 9. Cena → prefabs

**Pelo Editor:** botão direito na Hierarchy → **AI Capture → Converter em prefab…**. Antes de converter, o pacote lista as ligações de dentro do grupo para fora dele (ex.: o OnClick chamando um GameManager da cena). Elas continuam funcionando nesta cena como override, mas chegam vazias se o prefab for usado em outra. **Tools → AI Capture → Listar candidatos a prefab (cena ativa)** mostra os grupos sugeridos já analisados.

Converter tem desfazer (Ctrl+Z) na cena, mas o `.prefab` criado continua no projeto: faça commit antes de converter vários grupos.

---

## 10. Para agentes de IA

`AICaptureAgentAPI` recebe texto e devolve JSON; cada resposta também vai para `AIContext/last_action.json`.

| Método | O que faz |
|---|---|
| `RunScenario("Assets/Capture/Menu.asset", locale, compare)` | Inicia uma captura (assíncrona: entra e sai do Play Mode) |
| `GetLastRunReport()` | Relatório da última execução |
| `DeclareExpectedChange(cenário, captura, descrição, x, y, w, h)` | Declara uma mudança intencional |
| `AcceptLastRunAsGolden(captura?)` | Aprova a última execução, inteira ou uma captura |
| `ListPrefabCandidates()` | Candidatos da cena ativa, já analisados |
| `AnalyzeForPrefab("Canvas/Settings")` | Riscos antes de converter; não altera nada |
| `ConvertToPrefab("Canvas/Settings")` | Cria o prefab, salva a cena e atualiza o contexto |
| `ExportContext()` | Regera `AIContext/` |

`ConvertToPrefab` recusa quando há referências externas, a menos que receba `allowExternalReferences: true` (ou a opção esteja ligada no projeto). Caminhos ambíguos são recusados; para escolher a cena use `"Assets/Scenes/Main.unity:Canvas/Settings"`.

**Fluxo sugerido para uma mudança de UI feita por agente**

1. Ler `AIContext/index.txt` e a descrição do prefab ou da cena que vai mudar.
2. Fazer a mudança e declarar com `DeclareExpectedChange`.
3. `RunScenario(...)`, esperar o Play Mode terminar e ler `GetLastRunReport()` e o `report.md`.
4. `passed` = nada mudou na tela; `needs_approval` = mostre os recortes a uma pessoa; `failed` = mudou algo fora do declarado, corrija.
5. A aprovação da golden é de uma pessoa (ou do agente revisor), nunca de quem fez a mudança.

Para converter grupos em prefabs: gere a golden "antes", `ListPrefabCandidates()`, `AnalyzeForPrefab(...)`, `ConvertToPrefab(...)` e rode o cenário de novo: `passed` confirma que a conversão não mudou a tela.

---

## 11. Dicas e limitações

- A Game View precisa estar **aberta e visível** para a captura sair na resolução certa. O pacote desliga *Low Resolution Aspect Ratios* durante a execução e restaura depois.
- Use `SetTimeScale 0` e `WaitForStable` antes de capturar para eliminar animações e transições.
- Conteúdo que muda sozinho (hora, rede, dados ao vivo): mascare com `ignoreRegions` ou fixe por um hook.
- A troca de resolução usa APIs internas da Unity por reflexão; se uma versão futura mudá-las, você verá um aviso e a captura sai na resolução atual da Game View.
- Não roda em batchmode com `-nographics` (sem GPU não há screenshot).
- O pacote só cria e remove o **próprio** tamanho "AI Capture" na Game View (**Tools → AI Capture → Clean Game View Sizes** remove sobras).
- Funciona com domain reload desligado: o estado estático é reiniciado a cada sessão de Play.

Histórico de versões: [CHANGELOG.md](CHANGELOG.md).
