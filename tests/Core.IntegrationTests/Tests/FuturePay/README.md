# future-pay

Kredi kullandırım senaryosu: üç akıştan kurulu bir süreç — parent `loan-disbursement` ve her biri kendi
instance'ını başlatan iki SubFlow state'i (`credit-bureau-inquiry`, `collateral-establishment`). Suite
iki sınıftır: `FuturePayTests` çok akışlı SubFlow zincirini ve şema doğrulamasını, `SyncResponseExtensionsTests`
ise extension'ların yalnız **okumada** çalıştığını (runtime 0.0.93) pinler. Workflow anahtarı test
sınıflarında `loan-disbursement`'tır; senaryo adı `future-pay` klasöründen gelir.

## Neyi denetliyor

- **SubFlow state (`type: S`) → child auto-complete → parent resume**: `submit-application` sonrası
  büro alt akışı kendiliğinden biter, parent `bureau-completed` (auto, `triggerKind: 10`) ile
  `assessment-pricing`'e geçer; alt akışın output mapping'i `creditBureau.kkbScore`'u parent'a yazar.
- **Açık korelasyon**: `approve` sonrası parent `collateral-establishment` / `B`'de kalır (child yaşadıkça
  Busy), `functions/state`'in gözlenen state'i ise çocuğun `contract-signing` / `A`'sıdır.
- **Transition şema doğrulaması**: `loan-application` (beş zorunlu alan, `additionalProperties:false`)
  ve `loan-rejection` (`rejectionReason` zorunlu) 400 ile reddeder, instance yerinde kalır.
- **Transition erişilebilirliği**: `approve` yalnız `approval`'da tanımlı; `assessment-pricing`'te ≥400.
- **Extension kapsamı**: `sync=true` start/transition yanıtında `extensions` anahtarı durur ama hep `{}`;
  aynı `customer-profile-enrichment` instance GET'te ve `functions/data`'da çalışır; kaldırılan
  `?extensions=` query parametresi eski client'ı 400'lemez.

## Akış

```
loan-disbursement (F) v1.0.1   extension: customer-profile-enrichment (type 3, scope 1 → GET api/core/customer/profile)
start-application ─▶ application-intake (Initial)
  submit-application (manual, schema loan-application, onExecute validate-application) ─▶
credit-bureau-inquiry (SubFlow S → credit-bureau-inquiry v1.0.0)
  │   child: inquiry (onEntry inquire-kkb, inquire-findeks — HTTP) ─auto─▶ completed
  bureau-completed (auto) ─▶ assessment-pricing        queryRoles: core.kredi-tahsis, core.operasyon
  submit-assessment (manual, schema loan-assessment, onExecute score-and-limit, price-loan) ─▶
approval (onEntry compute-required-approver)    roles: $.data.approval.requiredApproverRole | core.operasyon
  ├─ approve (manual, schema loan-approval-decision) ─▶ collateral-establishment (SubFlow S → collateral-establishment)
  │     child: contract-signing ─ sign-contract (manual) ─▶ collateral-registration ─auto─▶ completed
  │   collateral-completed (auto) ─▶ disbursement
  │   execute-disbursement (auto, onExecute release-block, transfer-to-account — HTTP) ─▶ disbursed (Finish/Success)
  └─ reject (manual, schema loan-rejection) ─▶ rejected (Finish/Error)
```

## Testler

| Test | Kanıtladığı | Not |
|---|---|---|
| `SubmittingAnApplication_RunsTheBureauSubflowAndLandsOnAssessment` | Büro alt akışı koştu, çıktısı parent'a map'lendi (`creditBureau.kkbScore`) | 120 sn bütçe |
| `SubmittingAnApplication_RejectsAPayloadMissingRequiredFields` | Şema reddi 400, `application-intake` / `A` | |
| `Approving_StartsTheCollateralSubflowAsAnOpenCorrelation` | Parent `collateral-establishment` / `B`, aktif subflow kaydı var, gözlenen state çocuğun `contract-signing` / `A`'sı | Bacağın sonu bilinçli olarak assert edilmez |
| `Rejecting_TerminatesTheApplicationWithoutStartingCollateral` | `reject` → `rejected` / `C`, aktif subflow yok | |
| `Rejecting_RequiresARejectionReason` | `rejectionReason`'sız `reject` 400 | |
| `ApprovalStep_IsNotReachableBeforeAssessmentIsSubmitted` | `approve` `assessment-pricing`'te reddedilir | |
| `SyncStart_CarriesAnEmptyExtensionsMap` | Sync start yanıtında `extensions: {}` | |
| `SyncTransition_CarriesAnEmptyExtensionsMap_WhileTheInstanceGetStillEnriches` | Sync transition `{}`; instance GET `customerProfileEnrichment.customerProfile.customerId`'yi taşır | İki taraflı iddia |
| `DataFunction_StillRunsTheExtension` | `functions/data` extension'ı çalıştırır | |
| `ALegacyCallerStillSendingTheExtensionsQueryParameter_IsNotRejected` | `?extensions=` ile start 200, `extensions` boş | |

## Bağımlılıklar ve ön koşullar

| Bağımlılık | Durum |
|---|---|
| Runtime | `core`, `VNEXT_BASE_URL` (`test.runsettings`: `http://localhost:4201`), lokal build (extension kapsamı ≥ 0.0.93 davranışı) |
| System package | Gerekmez — bütün referanslar `core/` altında |
| MockLab | **Gerekir** — `future-pay-collection.json`: `POST api/core/credit-bureau/inquire-kkb`, `POST api/core/credit-bureau/inquire-findeks`, `GET api/core/customer/profile` (extension); `release-block` / `transfer-to-account` rotaları seed'de var ama bu bacak koşulmuyor |
| Dapr | Akışa özgü bileşen yok (SubFlow başlatma runtime'ın kendi post-commit işi) |
| Caller rolleri / header'lar | `role` + `x-roles` = `core.kredi-tahsis,core.operasyon` (`assessment-pricing` / `approval` `queryRoles` ve transition `roles`'u bunlara göre yazılmış) |
| Cross-domain | Gerekmez — iki alt akış da `domain: core` |
| morph-idm provider | Gerekmez |

## Nasıl çalıştırılır

```bash
dotnet test tests/Core.IntegrationTests \
  --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~FuturePay"
```

Python scripti yok; elle sürmek için `api-tests/future-pay/loan-disbursement.http`.

## Dikkat noktaları / bilinen istisnalar

- **Bilinçli kapsam açığı:** `sign-contract` sonrası bacak — collateral alt akışının bitmesi ve parent'ın
  `disbursement`'a resume'u — assert edilmez; bu bacak domain paketinde henüz izole edilmemiş bir
  sebeple fault ediyor (`TEST-SCENARIOS.md` › Bilinen Kapsam Açıkları).
- **SubFlow state'indeki parent Busy'dir.** `approve` için `RunAcceptedAsync` kullanmayın (Busy'den
  çıkmayı bekler); test `RunAsync` + aktif subflow beklemesi kullanır.
- **`purpose` şemada opsiyonel ama gövdede olmalı**: intake mapping'i null yazar, master şema onu reddeder.
- **Extension testleri iki taraflıdır**: yalnız "yazma yanıtı boş" iddiası bozuk ya da publish edilmemiş
  bir extension'da da geçerdi; her vaka aynı extension'ın okumada çalıştığını da kanıtlar.
- Alt akışın ilk state'inin adı `contract-signing`'dir — `ContractSigning` suite'iyle ilgisi yoktur.
