# ai-agent-lab — AI Agent state (`stateType: 6`) uçtan uca (vnext#1105)

## Neyi denetliyor

Bir **AiAgent** state'ine girildiğinde runtime agent'ı (`sys-agents` bileşeni) çalıştırır. Agent
tool'larını çağırır, yapılandırılmış çıktısını `agent.output.path`'e yazar ve karar verir. Bu karar
state'in **kuralsız** automatic transition'larından birini seçer. Senaryo üç karar modunu da gerçek
pipeline içinde pinler:

| Mod | Akış | Kim karar verir |
|---|---|---|
| `model` | `ai-agent-lab` | Modelin döndürdüğü `decision` (aday enum'u) |
| `script` | `ai-agent-lab-script` | `IAgentDecisionMapping` (`src/ScriptDecision.csx`) |
| `rules` | `ai-agent-lab-rules` | Transition'ların kendi kuralları, agent'ın çıktısını okuyarak |

Ek olarak: task tool'u (kind 1) ve script tool'u (kind 2), `tools.exclude`, flow kapsamlı oturum ve
ikinci girişte devamı, `policy.maxEntries` + `decision.onExhausted`, aday olmayan karar → `fallback`
ve `functions/agent-runs` okuma ucu.

## Neden var

AiAgent yeni bir state tipidir ve pipeline'a yeni bir adım (`RunAgentStep`, sıra 65) ekler. Karar
Auto adımına (80) bağlam üzerinden taşınır. Unit testler `AutoConditionEvaluator`'ı ve MAF koşusunu
ayrı ayrı pinler; bu senaryo üç parçanın gerçek bir hop'ta birlikte çalıştığını gösterir: agent koşusu,
instance data yazımı ve automatic transition seçimi.

## Deterministik sağlayıcı

Agent `provider: "local"` profilini kullanır (runtime'ın `AiAgent:Providers:local`, `Kind: local`).
Bu sağlayıcı ağa çıkmaz:

1. İlk turda her tool'u **bir kez** çağırır; argümanları tool'un `parameters` şemasından üretir (`default`).
2. İkinci turda cevabı çıktı şemasından üretir: `const` → `default` → `enum[0]`.

Bu yüzden model kararı her zaman **ilk adaydır** (`is-question`) ve `category` her zaman `question`'dır.

## Akışlar

```
ai-agent-lab
  classifying (AiAgent, model, memory: flow, maxEntries: 2)
    ├─ is-question      → answered (Intermediate: ask-again → classifying, close → closed)
    ├─ is-fraud         → escalated
    ├─ classify-failed  → failed       (decision.fallback)
    └─ too-many         → exhausted    (decision.onExhausted)

ai-agent-lab-script
  classifying (AiAgent, script: instance data'daki `pick`'i döndürür, tools.exclude: lab_script)
    ├─ is-question → answered   ├─ is-fraud → escalated   └─ classify-failed → failed (fallback)

ai-agent-lab-rules
  classifying (AiAgent, rules, tools.exclude: lab_script, bump)
    ├─ to-other     rule: NeverRule     → other
    ├─ to-question  rule: CategoryRule  → answered   (triage.category == "question")
    └─ classify-failed (kuralsız fallback, en son) → failed
```

Agent: `core/Agents/ai-agent-lab/ai-triage-agent.json`. Araçları `bump` (kind 2, dynamicExpresso
`args.count + 1`) ve `lab_script` (kind 1, `sys-tasks/sched-script-task`). Akışlar ve agent tek
dosyadan üretilir:

```bash
python3 core/Workflows/ai-agent-lab/build-ai-agent-lab.py
```

## Beklenen sonuç

| Test | İddia |
|---|---|
| `ModelDecision_TakesTheChosenCandidate_AndRecordsTheRun` | `answered`/`A`; `triage.category = question`; `$agent`: `Completed`, `decision = is-question`, `decisionSource = Model`, iki tool çağrısı da `ok`, `session.scope = instance`, `resumed = false`. `agent-runs` tek koşu listeler (`entryNo 1`, `selectedTransition is-question`, `toolCallCount 2`). `?runId=` tool çağrılarını verir; tool argümanını (`from-agent`) içermez |
| `ReEntry_ResumesTheSession_ThenLeavesThroughOnExhausted` | İkinci giriş oturumu devam ettirir (`resumed = true`). Üçüncü giriş `maxEntries: 2`'yi aşar: sağlayıcı çağrılmaz, `exhausted`/`C`, `$agent`: `BudgetExceeded`, `too-many`, `Exhausted`, `toolCalls = []`, `session.scope = none`. `agent-runs`: 3, 2, 1 |
| `ScriptDecision_TakesTheCandidateTheScriptReturns` | `pick = is-fraud` → `escalated`; `decisionSource = Script`; `tools.exclude` yüzünden yalnız `bump` çağrıldı |
| `ScriptDecision_NotACandidate_TakesTheFallback` | `pick = no-such-transition` → `failed`; `decision = classify-failed`, `decisionSource = Fallback`, `reason` aday dışı değeri söyler |
| `RulesMode_TheTransitionsRulesDecideFromTheAgentsOutput` | `answered`; `decisionSource = Rules`; kazanan (`to-question`) koşunun karar satırına geri yazılır (`agent-runs`) |

## Dikkat noktaları

- **SDK açığı:** `LocalDomainPublisher` yalnız yedi bileşen tipini (`schemas … mappings`) yayınlar;
  `Agents` klasörünü bilmez. Ayrıca `sys-agents` flow'u henüz `@burgan-tech/vnext-core-runtime`
  paketinde yok. Bu yüzden test sınıfı ikisini de koşu başına bir kez kendisi yayınlar: önce
  `fixtures/sys-agents.json` (`sys-mappings`'in birebir kopyası), sonra agent. SDK ve sistem paketi
  desteği gelince bu adım kalkar.
- **`npm run validate`:** yayınlanmış vnext-schema `stateType: 6`'yı, `agent` bloğunu ve transition
  `description`'ını henüz bilmez; şema yayınlanana kadar bu üç akış validate'ten geçmez.
- Bir koşunun çıktısı yoksa (fallback, exhausted) önceki koşunun çıktı alanları (`category`,
  `summary`) instance data'da kalır. Hangi koşuya ait olduklarını `$agent.runId` ve `$agent.status`
  söyler. `$agent`'ın kendisi her koşuda tamamen yeniden yazılır.

## Nasıl çalıştırılır

Lokal derlenmiş runtime (vnext, `AiAgent` dalı) ayakta olmalı; MockLab gerekmez.

```bash
dotnet test tests/Core.IntegrationTests \
  --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~AiAgentLab" -v minimal
```

## Doğrulama durumu

Sonuç ve tarih `TEST-SCENARIOS.md`'deki satırdadır.
