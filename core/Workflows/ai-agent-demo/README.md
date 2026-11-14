# AI Agent demo akışları (vnext#1105)

AiAgent state'inin (`stateType: 6`) istemci tarafında nasıl göründüğünü gösteren üç örnek akış. Her
state'in bir pseudo-ui view'ı var; Forge Quick Run ya da client SDK ile uçtan uca gezilebilir. Bunlar
entegrasyon testi değil, örnektir. Testli senaryo `ai-agent-lab`'dır
(`tests/Core.IntegrationTests/Tests/AiAgentLab`).

Bileşenler her tipin altında `ai-agent-demo/` klasöründe: `Agents/`, `Workflows/`, `Views/`, `Schemas/`
ve `Tasks/`.

| Akış | AiAgent state'i | Gösterdiği |
|---|---|---|
| `triage-flow` 1.1.0 | `classifying` → `triage-agent` | Model kararı (`is-question` / `is-fraud`), zorunlu `fallback`, `policy.maxEntries` aşılınca `too-many`, `ask-again` ile flow oturumundan devam |
| `refund-flow` 1.1.0 | `assessing` → `refund-agent` | Task tool'u `lookup_order` ve onay isteyen `issue_refund` (`requiresApproval`). Onay bekleyen çağrılar `manager-review` ekranında listelenir; `decide` geçişinin `approval` gövdesiyle agent oturumundan devam eder |
| `loan-precheck-agent` 1.0.0 | `extracting` → `loan-intake-agent`, `explaining` → `loan-explainer-agent` | "LLM çıkarır, kod karar verir": agent serbest metinden tutar / vade / gelir alanlarını çıkarır, eksik bilgi varsa soru yazar (en fazla 3 tur). Politika `lp-policy-task` script task'ında deterministik hesaplanır, explainer yalnız açıklama yazar |

## Sağlayıcılar

- `triage-agent`, `refund-agent` ve `loan-intake-fixture-*`, runtime'ın `local` profilini kullanır
  (`AiAgent:Providers:local`, `Kind: local`). Bu profil ağa çıkmaz ve deterministik çalışır; lokal
  docker ortamında varsayılan olarak tanımlıdır.
- `loan-intake-agent` ve `loan-explainer-agent`, `llm` adlı bir profil bekler (openai-compatible). Bu
  profil runtime konfigürasyonunda tanımlı değilse:
  - `extracting` `fallback` ile `failed`'a gider;
  - `explaining` şablon açıklamayla `deciding` üzerinden sonuca yönlenir.
- Anahtar hiçbir bileşen JSON'una yazılmaz. Profil `ApiKeySecretRef` ile bir config ya da secret anahtarını
  adlandırır; değeri host ortamından gelir.

## Fixture akışları

Gerçek model olmadan kredi akışının iki dalını sınamak için iki fixture akışı var:
`loan-precheck-fixture-ok` (uygun → `offer`) ve `loan-precheck-fixture-low-income` (taksit gelirin
%40'ını aşar → `rejected`). İkisi de `loan-precheck-agent`'in kopyasıdır. Tek fark `extracting`
state'inin sabit çıktılı `loan-intake-fixture-*` agent'ını kullanmasıdır.

## Çalıştırma

```bash
wf domain use core && wf domain active   # core yazmalı
wf check && wf sync                      # agent'lar workflow'lardan önce yayınlanır
```

- `Workflows/ai-agent-demo/triage-flow.http`: start → state → transition → `functions/agent-runs`.
- `Agents/ai-agent-demo/triage-agent.http`: dry-run. Dry-run uç noktası varsayılan olarak kapalıdır.
  Açmak için runtime'da `AiAgent:DryRun:Roles` ayarlanır ve istek o rollerden biriyle gönderilir.

Çıktı her AiAgent state'inde `output.path` altına yazılır: triage için `$.triage`, refund için
`$.assessment`, kredi için `$.intake` / `$.explain`. Son koşunun özeti de aynı yolun altında, `$agent`
alanında durur. `*-master` şemaları bu alanları tanımlar.
