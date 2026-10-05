# money-transfer

Tek akışlık referans süreç: kural güdümlü dallanma (auto transition), scheduled (timer) transition ve
sonucu instance data'ya yazan terminal HTTP task'ı tek akışta birleşir. Suite mutlu yolu, transition
şema reddini, timer'ın arm edildiğini, iptali ve sağlayıcı sonucunun veriye düşmesini pinler.

## Neyi denetliyor

- **Transition şema doğrulaması**: `submit-details` `money-transfer-input` taşır; eksik gövde 400, instance
  `enter-transfer-details`'te kalır.
- **Rule-driven branching**: `evaluate-push-requirement`'in iki auto transition'ı (`require-push` /
  `skip-push`) `isFirstTransfer`'a göre dallanır; `isFirstTransfer`'ı `confirm`'ün onExecute'undaki
  GetInstances task'ı (type 15, aynı akışın aynı `targetIban`'lı instance'ları) yazar.
- **Scheduled transition arm**: `awaiting-push-approval`'a girişte `push-timeout` (triggerType 2, timer
  5 dk) Dapr Jobs API üzerinden arm edilir ve state function'da `kind: "scheduled"` girdisi olarak görünür.
- **Terminal HTTP task sonucu → instance data**: `execute-transfer` cevabı `transferResult.success`
  olarak yazılır; `execution-succeeded` / `execution-failed` kuralları bunu okur.
- **Well-known `cancel`**: `cancel-transfer` → `transfer-cancelled`.

## Akış

`money-transfer` v1.1.2, global error boundary: **abort** (her task hatası instance'ı fault'lar);
`executing-transfer` state'inde `400` için `cancel-transfer`'a notify.

```
start ─▶ enter-transfer-details (Initial)
  submit-details (manual, schema money-transfer-input) ─▶ review-and-confirm
  confirm (manual, onExecute get-iban-history — GetInstances type 15) ─▶ evaluate-push-requirement
    ├─ require-push (auto, RequirePushRule: isFirstTransfer) ─▶ awaiting-push-approval
    │     ├─ approve-push (manual) ─▶ executing-transfer
    │     └─ push-timeout (scheduled, PushTimeoutTimer PT5M) ─▶ transfer-failed
    └─ skip-push    (auto, SkipPushRule)                    ─▶ executing-transfer
executing-transfer  onEntry: 1 execute-transfer (HTTP) · 2 get-accounts-dapr (DaprService type 3)
    ├─ execution-succeeded (auto) ─▶ transfer-completed (Finish/Success)
    └─ execution-failed    (auto) ─▶ transfer-failed    (Finish/Error)
cancel-transfer (manual) ─▶ transfer-cancelled (Finish/Cancelled)
```

## Testler

| Test | Kanıtladığı | Not |
|---|---|---|
| `HappyPath_ReachesTransferCompleted` | `require-push` dalı üzerinden `transfer-completed` / `C` | `approve-push` gönderilir |
| `SubmitDetails_RejectsAPayloadThatViolatesTheTransitionSchema` | Şema reddi 400, state değişmez | |
| `AwaitingPushApproval_ArmsTheTimeoutTimer` | `push-timeout` için `executeAtUtc` görünür | Timer ateşlenmesi beklenmez (5 dk) |
| `Cancel_MovesTheTransferToCancelled` | `review-and-confirm`'den `cancel-transfer` → `transfer-cancelled` | |
| `ExecutingTransfer_RecordsTheProvidersResultInInstanceData` | `transferResult.success == true` instance data'da | |

## Bağımlılıklar ve ön koşullar

| Bağımlılık | Durum |
|---|---|
| Runtime | `core`, `VNEXT_BASE_URL` (`test.runsettings`: `http://localhost:4201`), lokal build |
| System package | Gerekmez — bütün task'lar `core/Tasks/money-transfer/` altında |
| MockLab | **Gerekir** — `money-transfer-collection.json`: `POST api/payments/transfers/execute` (HTTP), `GET api/payments/accounts` (Dapr invocation ile); `validate` / `favorites` rotaları akışın function'ları için, testler çağırmaz |
| Dapr | **MockLab sidecar'ı app-id `mocklab`** (repo kökünde `docker compose up -d` → `mocklab-dapr`) — `get-accounts-dapr` service invocation; **scheduler** (Dapr Jobs API) `push-timeout`'u arm etmek için |
| Caller rolleri / header'lar | Test `morph-core.maker` gönderir ama akışta `queryRoles`/`roles` yok — fiilen gerekmez |
| Cross-domain | Gerekmez (`get-iban-history` aynı domain'de `money-transfer`'i sorgular) |
| morph-idm provider | Gerekmez |

## Nasıl çalıştırılır

```bash
dotnet test tests/Core.IntegrationTests \
  --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~MoneyTransfer"
```

Python scripti yok; elle sürmek için `api-tests/money-transfer/money-transfer.http`.

## Dikkat noktaları / bilinen istisnalar

- **Global abort boundary.** `get-accounts-dapr`'ın Dapr çağrısı başarısız olursa (MockLab sidecar'ı
  yok) instance fault'lar ve mutlu yol `transfer-completed`'e varmaz; `execute-transfer`'ın HTTP
  hatası da aynı şekilde. İlk bakılacak yer `DescribeAsync` çıktısındaki incident.
- **Timer testi scheduler'a bağlıdır.** `AwaitingPushApproval_ArmsTheTimeoutTimer`, Dapr scheduler
  erişilemezse de kırmızıya döner — hata mesajı bunu söyler.
- **Dal seçimi tarihçeye bağlı.** Testler sabit `targetIban` ile `require-push` dalını bekler;
  `isFirstTransfer`, aynı IBAN'lı önceki instance'ları sayan bir GetInstances sorgusundan gelir
  (`priorTransferCount`). Akış `skip-push`'a kayar ve `awaiting-push-approval` hiç görülmezse önce
  instance verisindeki `priorTransferCount`'a bakın (kaynak okumasından çıkarım, koşuda doğrulanmadı).
- **`RunAndSettleAsync` durum kodunu çoğu yerde yok sayar** (`confirm`, `approve-push`, `cancel-transfer`);
  bir red, sonraki state beklemesinde zaman aşımı olarak görünür.
- Publish versiyon-değişmezdir; fixture değişikliği patch bump ister (`1.1.2` → `1.1.3`).
