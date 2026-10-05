# contract-signing

SubFlow korelasyonu **olmadan**, yalnız task'larla birbirine bağlanan üç akış: `login-flow` (kök)
`contract-flow`'u SubProcess olarak başlatır; `contract-flow` doküman listesini okuyup `$self` auto
döngüsüyle doküman başına bir `online-flow` spawn eder; onaylar trigger task'larıyla yukarı taşınır ve
`login-flow` sonlandırıldığında zincirdeki her instance `C` olur. Zincir korelasyon tablosundan değil
**instance data'dan** izlenir: `login-flow` `contractInstanceId`'yi, `contract-flow`
`onlineInstanceIds` + `documentCount`'u yazar.

## Neyi denetliyor

- **SubProcess (fire-and-forget)**: `SubProcessTask` (type 14) onEntry'de çocuk akış başlatır, parent
  beklemez ve Busy'de tutulmaz.
- **`$self` auto döngüsü**: `invoke-next` her turda farklı bir çocuk üretir (doküman id'leri ayrık),
  `invoke-done` kural ile döngüyü kapatır.
- **Trigger task'ları (type 12)** ile akışlar arası transition: `login-ready`, `login-approvals-done`
  (shared, `availableIn` ile tek state'e daraltılmış), `contract-finalize`, `online-finalize`.
- **updateData ile fan-in**: çocuklar `contract-progress`'i (updateData) çağırır; her çocuk kendi
  anahtarını (`rr_{doc}` / `ap_{doc}`) damgalar, kapı kuralları (`AllRenderedRule`, `AllApprovedRule`)
  taze veriye karşı yeniden değerlendirilir.
- **Start mapping'in guard'sızlığı**: `sub` / `act_sub` olmadan start, ilk task'ta instance'ı fault'lar.

## Akış

```
login-flow (F)                 contract-flow (P)                       online-flow (P) × N
login-initial                  contract-initial  onEntry get-contract-documents (HTTP)
  onEntry start-subprocess ──▶   └ auto ─▶ received-documents ─auto─▶ invoke-loop
  └ auto ─▶ awaiting-ready                invoke-next ($self, auto) exec start-subprocess ──▶ online-initial
                                          invoke-done (auto) ─▶ awaiting-renders                (onEntry script: render)
                                                                                                └ auto ─▶ notify-render-ready
                                awaiting-renders ◀── contract-progress (updateData, kind=render) ─┘  onEntry trigger
                                  render-all-done (auto) ─▶ render-ready-done                          └ auto ─▶ pre-approval-waiting
  login-ready (shared) ◀──────── onEntry notify-parent-transition                                     pre-approve (manual)
waiting-approval-doc            └ auto ─▶ approval-waiting                                            ─▶ pre-approved
                                approval-waiting ◀── contract-progress (updateData, kind=approval) ──── onEntry trigger
  login-approvals-done ◀──────── all-approved (auto) ─▶ approval-done (onEntry trigger)               └ auto ─▶ pre-finalize
all-documents-approved          └ auto ─▶ awaiting-finalize
  login-finalize (manual) ─────▶ contract-finalize (manual, trigger) ─▶ finalize-loop
awaiting-completed                finalize-next ($self, auto) exec trigger ──────────────────────────▶ online-finalize ─▶ online-completed
  contract-status (updateData) ◀ finalize-done (auto) ─▶ contract-completed (onEntry trigger)
  login-contract-done (auto) ─▶ login-completed-state
```

Her akışta `cancel-*` → `*-cancelled`. `login-flow` ve `contract-flow` trigger/spawn task'larında
`409` için task seviyesi **retry** (8 deneme, üstel geri çekilme) taşır.

## Testler

| Test | Kanıtladığı | Not |
|---|---|---|
| `StartingLoginFlow_SpawnsTheContractSubProcess_AndOneOnlineFlowPerDocument` | SubProcess başlar; doküman sayısı kadar, birbirinden farklı `online-flow` | Bitiş sinyali `documentCount`, liste değil |
| `EachOnlineFlow_CarriesItsOwnDocument` | `$self` döngüsü her turda ilerler: `documentId`'ler boş değil ve ayrık | |
| `ApprovingEveryDocument_MovesLoginFlowToAllDocumentsApproved` | Onaylar updateData + trigger zinciriyle köke ulaşır | 90 sn bütçe |
| `FinalisingLoginFlow_CompletesEveryInstanceInTheChain` | `login-finalize` sonrası üç akışın tüm instance'ları `C`, beklenen terminal state'lerde | 120 sn bütçe |
| `StartingWithoutIdentityClaims_FaultsTheInstance` | `sub`/`act_sub`'sız start `login-initial`'da `F` | Keskin kenarın belgelenmesi |

## Bağımlılıklar ve ön koşullar

| Bağımlılık | Durum |
|---|---|
| Runtime | `core`, `VNEXT_BASE_URL` (`test.runsettings`: `http://localhost:4201`), lokal build |
| System package | Gerekmez — bütün task'lar `core/Tasks/contract-signing/` altında |
| MockLab | **Gerekir** — `contract-signing-collection.json`: `GET api/contract-signing/contracts/documents` |
| Dapr | Akışa özgü bileşen yok (trigger/SubProcess runtime'ın kendi invocation yolu) |
| Caller rolleri / header'lar | Rol gerekmez (akışlarda `queryRoles`/`roles` yok). `sub` ve `act_sub` **header değil, start gövdesinde** olmalı |
| Cross-domain | Gerekmez — bütün hedefler `domain: core` |
| morph-idm provider | Gerekmez |

## Nasıl çalıştırılır

```bash
dotnet test tests/Core.IntegrationTests \
  --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~ContractSigning"
```

Python scripti yok; elle sürmek için `core/Workflows/contract-signing/contract-signing.http`.

## Dikkat noktaları / bilinen istisnalar

- **`awaiting-renders` / `approval-waiting` fan-in kapısıdır.** Kural yanlışken instance Busy'de
  **park eder** (tasarım gereği); status'u değil state'i bekleyin. Kapıyı çocukların
  `contract-progress` updateData çağrıları açar.
- **Onayı `waiting-approval-doc`'tan önce göndermeyin.** `login-approvals-done` yalnız o state'te
  müsait; `login-ready` tetiklenmeden gelen onay hiçbir yere düşmez. `StartAndSpawnAsync` bu yüzden
  önce `waiting-approval-doc`'u bekler.
- **`pre-approve` / `login-finalize` `RunAsync` ile gönderilir, durum kodu assert edilmez.** Bir red,
  sonraki bekleme adımında zaman aşımı olarak görünür — önce reddi arayın.
- **Paylaşılan sayaç kullanmayın.** Eşzamanlı çocuk geri çağrıları aynı snapshot'tan okur;
  `count + 1` artışları kaybeder. Mapping'ler bu yüzden yalnız kendi anahtarının delta'sını döner.
- Mapping'ler hedef sürümü sabitler (`contract-flow` / `online-flow` `1.1.0`); fixture değişikliği
  patch bump ister ve mapping'lerdeki `SetVersion` da güncellenmelidir.
- `TEST-SCENARIOS.md` satırındaki gerekçe git geçmişinde kayıtlı değil (dipnot 1).
