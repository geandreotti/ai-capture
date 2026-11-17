# Changelog

## [1.7.0] - 2026-11-17
- Limpeza automatica de `%TEMP%/ai-capture` (`RunRetention`): ao iniciar uma execucao, cada cenario guarda as 3 execucoes mais novas e apaga as com mais de 7 dias; execucao aguardando aprovacao (`needs_approval`) fica ate 30 dias; a ultima execucao nunca e apagada; pastas e arquivos avulsos na raiz saem depois de 7 dias.
- `AcceptAsGolden` sem filtro apaga a execucao aprovada (as imagens ja estao em `AICaptureGoldens/`); aprovacao parcial (`onlyCapture`) mantem a execucao.

## [1.6.0] - 2026-10-11
- Regra de candidatos a prefab refeita (compartilhada pelo resumo de cena e pelo conversor): so sugere grupos entre `sceneCandidateThreshold` e o novo `sceneCandidateMaxSize` (padrao 200), descendo nos filhos de grupos maiores em vez de sugerir a raiz inteira da UI.
- Grupos menores tambem sao candidatos quando a mesma estrutura se repete entre irmaos (cards, linhas); o resumo mostra "xN repetidos" e a analise traz `repeatCount`.
- Fix: capturas `_box<N>` (recortes do relatorio) nao entram mais como capturas nem como goldens ao aprovar.
- Fix: a regra por regiao (`maxRegionPixels`) agora exige 5% de densidade de pixels alterados na caixa, para nao reprovar ruido de antialiasing em titulos largos.

## [1.5.0] - 2026-10-11
- Mudancas declaradas: `AICaptureAgentAPI.DeclareExpectedChange(cenario, captura, descricao, x, y, w, h)` marca uma captura como "deve mudar" (opcionalmente numa regiao). Se a diferenca bate com a declaracao, a captura fica `AWAITING APPROVAL` em vez de falha; diferenca fora dela continua falhando.
- Relatorio ganhou `status` (passed | needs_approval | failed), `awaitingApproval` e `declaredButUnchanged`.
- Aprovar a golden de uma captura limpa a declaracao dela. Declaracoes ficam em `AICaptureGoldens/<cenario>/expected_changes.json`.

## [1.4.0] - 2026-10-11
- Relatorio `report.md` por execucao, com recortes `<captura>_box<N>.png` (golden | atual | diff lado a lado, ~200 tokens cada em vez de ~2400 da tela inteira).
- Novo passo `WaitForStable`: espera a tela parar de mudar (timeout so avisa, nao falha).
- Acoes e condicao embutidas, sem hook: `click` (caminho ou nome), `scroll` (ScrollRect, 0-1) e `exists`.
- Runtime passa a referenciar UnityEngine.UI.

## [1.3.0] - 2026-10-11
- Modo estrito de golden (`strictGoldens`, ligado por padrao): captura sem golden falha em vez de virar golden sozinha. Aprove com "Accept Last Run as Golden".
- Regras por captura no cenario (`rules`): tolerancia de cor, % maximo de pixels e regioes ignoradas (mascara) para conteudo ao vivo.
- Regra por regiao (`maxRegionPixels`): falha se qualquer area alterada passar do limite, mesmo com poucos pixels no total.
- Relatorio ganhou `failReason`, `missingGolden` e `missingGoldens`.
- Aprovar uma captura so: `AcceptLastRunAsGolden("explore")`.
- Fix: DescribeUI usava uma API que nao existe no Unity 6.
- Fix: com domain reload desligado, estado estatico era reaproveitado entre sessoes de Play.
- O runner nao inicia enquanto o Editor compila ou importa; desmarca "Low Resolution Aspect Ratios" durante a execucao e restaura depois.

## [1.2.0] - 2026-10-11
- Conversão de grupos da cena em prefabs, pelo menu ou por agentes de IA, com análise prévia de referências para fora do grupo e trava de segurança.
- `AICaptureAgentAPI`: métodos em JSON para agentes (candidatos, análise, conversão, cenários, relatórios), com cópia em `AIContext/last_action.json`.
- Nova configuração: pasta dos prefabs convertidos.

## [1.1.0] - 2026-10-11
- Contexto para agentes de IA: exporta descrições compactas de cada prefab e resumos de cenas para `AIContext/`, com `index.txt` e README para o agente.
- Resumo de cena marca grupos grandes de objetos soltos como candidatos a prefab e instâncias com overrides.
- Atualização automática ao salvar, mover ou apagar prefabs e ao salvar cenas.
- Página de configurações em Project Settings → AI Capture.
- Describe UI agora lista scripts do projeto, scripts faltando, prefabs aninhados, layouts e mais componentes.

## [1.0.0] - 2026-10-11
- Primeira versão: cenários de captura com hooks, ajuste de Game View, descrição de UI e regressão visual com Golden Images.
