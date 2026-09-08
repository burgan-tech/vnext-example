# vNext Build Plan — Cross-Domain Lab (`cross-domain-lab`)

> Durum: **UYGULANDI** — 2026-09-03, `dotnet test --filter CrossDomainLab` 11/11 yeşil lab'a karşı
> (onay: kullanıcı, 2026-09-03 "onaylıyorum"; kararlar §9'a işlendi). Uygulama notları §11.
> Konum: `vnext-example/labs/cross-domain/VNEXT-BUILD-PLAN.md` — kökteki `VNEXT-BUILD-PLAN.md`
> başka bir çalışmaya (otp-auth) ait. Demirleme kaynağı banka sistemleri değil **vNext runtime kodu**
> (`/Users/U0B006/Documents/repos/burgan-tech/vnext`, branch `feature/dapr-name-resolution`).

## 0. Özet & Kapsam

**Ne:** `core` domain'inde koşan bir parent akışın, `partner` domain'indeki bileşenleri **Dapr Name
Resolution + Service Invocation** üzerinden tükettiği referans senaryo. `ServiceDiscovery:Provider=dapr`
yolunun uçtan uca regresyon fixture'ı.

**Neden:** Cross-domain adres çözümlemesi Discovery registry HTTP'sinden Dapr'a taşındı; vnext-example'da
hiçbir cross-domain örnek yok (`config.domain` her yerde `core`, `useDapr` hiç geçmiyor).

**Nerede:** `/Users/U0B006/Documents/repos/burgan-tech/vnext-example`. Parent: mevcut `core` domain,
klasör `cross-domain-lab`. Child: yeni `partner/` kökü + `vnext.partner.config.json`. Lab altyapısı
`labs/cross-domain/` (yazıldı, doğrulandı).

**Lab topolojisi (ayakta):** core :4201 `vnext-app-core` · partner :4211 `vnext-app-partner` ·
discovery :4231. core/partner açılışta discovery'ye kaydolur; `DaprDomainDiscoveryProvider` registry
`appId`'sini kullanır (lab app-id'leri konvansiyondan farklı → `PreferRegistryAppId=true`).

**Kapsam içi:** (1) SubFlow start + forward + parent resume, (2) SubProcess task 14, (3) GetInstance 19,
(4) GetInstances 15 (filtre), (5) StartTrigger 11, (6) DirectTrigger 12, (7) GetInstanceData 13,
(8) subflow'dayken state · data(extensions) · authorize · schema · view descent'i.

**Kapsam dışı:** cross-domain `context.Related`; yük testi; cluster'a özgü doğrulamalar
(`{{.Namespace}}`, mTLS, NetworkPolicy); hata enjeksiyonu (`ERR_DIRECT_INVOKE`) — ikinci faz.

## 1. Kabul Kriterleri

- **AC-01** `enter-subflow` → partner'da `xd-child` oluşur; parent state fn `state=child-review`,
  `activeCorrelations[0].domain=partner`.
- **AC-02** Parent üzerinden `view` → `xd-child-review-view` (yalnız partner'da var).
- **AC-03** Parent üzerinden `schema?transitionKey=child-approve` → `xd-child-approve`, `required` ⊇ `approvedBy`.
- **AC-04** Parent üzerinden `data?extensions=xd-child-ext` → extension partner'dan; gövde parent verisi.
- **AC-05** Parent üzerinden `authorize?transitionKey=child-approve` → `xd-approver` 200 / başka rol 403
  (karar partner'daki transition rollerinden; parent `overrides` kullanılmaz).
- **AC-06** `PATCH xd-parent/{id}/transitions/child-approve {approvedBy}` → child `child-completed`;
  parent `xd-after-subflow`, `attributes.childCompleted=true`.
- **AC-07** `spawn-subprocess` → `attributes.workerInstanceId`; partner `xd-worker` `worker-done`;
  parent beklemeden `xd-after-spawn`.
- **AC-08** `start-remote` (11, sync) → `attributes.remoteInstanceId`; partner `xd-remote` `remote-waiting`.
- **AC-09** `trigger-remote` (12) → partner `remote-done`.
- **AC-10** `read-remote` (19+13) → `remoteState=remote-done`, `remoteData.testId=testId`.
- **AC-11** `list-remote` (15) → `remoteCount>=1`, her kayıt aynı `testId`.
- **AC-12** `Discovery.Resolve/partner` span: `vnext.discovery.provider=dapr`, `vnext.dapr.app_id=vnext-app-partner` (manuel, OpenObserve).
- **AC-13** `dotnet test --filter CrossDomainLab` lab'a karşı yeşil; `VNEXT_PARTNER_BASE_URL` yoksa skip.

## 2. Bileşen Envanteri

| Yol | Tür | Not |
|---|---|---|
| `vnext.partner.config.json` | config | `domain:"partner"`, `componentsRoot:"partner"` |
| `partner/Workflows/cross-domain-lab/xd-child.json` (+`src/`) | workflow S | child-initial → child-review(view) → child-completed; `child-approve` (schema, roles `xd-approver`) |
| `partner/Workflows/cross-domain-lab/xd-remote.json` (+`src/`) | workflow F | remote-initial → remote-waiting → remote-done; `remote-advance` |
| `partner/Workflows/cross-domain-lab/xd-worker.json` (+`src/`) | workflow P | worker-initial → worker-done |
| `partner/Views/cross-domain-lab/xd-child-review-view.json` | view | pseudo-ui, `loadData:true` |
| `partner/Schemas/cross-domain-lab/xd-child-approve.json` | schema | `required:["approvedBy"]` |
| `partner/Extensions/cross-domain-lab/xd-child-ext.json` (+`src/`) | extension | type 2 (instance), scope 1 |
| `core/Tasks/cross-domain-lab/xd-spawn-worker.json` | task 14 | `domain:partner, flow:xd-worker, useDapr:true` |
| `core/Tasks/cross-domain-lab/xd-start-remote.json` | task 11 | + `sync:true` |
| `core/Tasks/cross-domain-lab/xd-trigger-remote.json` | task 12 | + `transitionName:remote-advance` |
| `core/Tasks/cross-domain-lab/xd-get-remote-instance.json` | task 19 | |
| `core/Tasks/cross-domain-lab/xd-get-remote-data.json` | task 13 | |
| `core/Tasks/cross-domain-lab/xd-list-remote.json` | task 15 | `page:1, pageSize:10` |
| `core/Workflows/cross-domain-lab/xd-parent.json` (+`src/*.csx`) | workflow F | §3 |
| `tests/Core.IntegrationTests/Tests/CrossDomainLab/*` | test | fixture + base + 2 sınıf + README |

## 3. Akış Tasarımı

```
xd-parent (core, F)
xd-initial(1) ─auto→ xd-hub(2)
  ─enter-subflow(manual)→ xd-subflow(4; subFlow S → partner/xd-child; ISubFlowMapping)
  ─auto (child bitince)→ xd-after-subflow(2)
  ─spawn-subprocess(manual; onExecute task 14)→ xd-after-spawn(2)
  ─start-remote(manual; task 11)→ xd-after-start(2)
  ─trigger-remote(manual; task 12)→ xd-after-trigger(2)
  ─read-remote(manual; task 19 + task 13)→ xd-after-read(2)
  ─list-remote(manual; task 15)→ xd-completed(3/1)
cancel → xd-cancelled(3/3)
```
Her adım manuel: test her cross-domain yeteneği izole assert eder. Mapping'ler instance data'ya yazar:
`childCompleted, workerInstanceId, remoteInstanceId, remoteState, remoteData, remoteCount`.
`testId` start payload'ından gelir; partner akışları da `testId`'yi data'ya yazar (filtre anahtarı).

## 4. Veri / Şema

- Parent start body: `{ testId }`. Child start body (InputHandler): `{ testId, parentInstanceId }`.
  Child OutputHandler → `{ childCompleted:true, childApprovedBy }`.
- `xd-child-approve` şeması: `{ approvedBy: string(1..64) }`, `required:[approvedBy]`.
- Remote/worker start body: `{ testId }`.

## 5. Görünüm / UI

`xd-child-review-view` (pseudo-ui, `loadData:true`) — yalnız descent kanıtı; içeriği minimal.

## 6a. Demirleme — Runtime kanıtları

| İddia | Kaynak |
|---|---|
| Task tipleri 11/12/13/14/15/19 ve config anahtarları (`domain flow version key sync body useDapr transitionName instanceId page pageSize sort filter`) | `vnext/src/BBT.Workflow.Domain/Definitions/Tasks/TaskEnums.cs`, `*Task.cs` |
| `filter` JSON'da yalnız string; yapısal filtre mapping'te `SetFilterSpec` | `GetInstancesTask.cs:225-240`; emsal `core/Workflows/money-transfer/src/GetIbanHistoryMapping.csx` |
| SubFlow state: `stateType:4`, `subFlow.type "S"`, `process{key,domain,version,flow}`, `mapping` | emsal `core/Workflows/subflow-orchestration/subflow-orchestration-parent.json` |
| SubProcess emsali (task 14, `SetKey/SetSync(false)`, çıktı `context.Body.data.value.id`) | `core/Tasks/contract-signing/start-subprocess.json`, `src/LoginStartContractMapping.csx` |
| Descent noktaları state/view/schema/authorize; data gövdesi inmez, extensions iner | `InstanceQueryAppService.cs` (~570, ~878, ~2368, ~2531), `AuthorizeAppService.cs:61-130` |
| App-id çözümleme: overrides → registry appId → konvansiyon | `DaprDomainDiscoveryProvider.cs:68-134` |
| `LocalDomainPublisher.ReplaceDomain` `config`/`process`'e girmez; version `{v}-pkg.{cfg}+{domain}`, prefix çözümleme | `vnext-integration/.../LocalDomainPublisher.cs`, `CacheSet.cs:317` |
| Harici-stack modunda `OnAfterEnvironmentReadyAsync` çağrılmaz | `tests/.../Infrastructure/VNextTestEnvironment.cs:41` |

## 6b. Demirleme — Dış sistemler

Banka sistemi yok. Tek dış bağımlılık lab (`labs/cross-domain/lab.sh verify` yeşil, 2026-09-03).

## 7. Test Senaryoları

`Tests/CrossDomainLab/`: `CrossDomainLabFixture` (partner publish, `IAsyncLifetime`),
`CrossDomainLabTestBase : WorkflowTestBase` (`PartnerApi`, skip guard, raw fonksiyon/authorize helper'ları),
`SubflowDescentTests` (AC-01..06), `TriggerTaskTests` (AC-07..11). Doğrulama SELECT'i yok (DB'ye
doğrudan bakılmaz; instance API'leri yeterli).

## 8. Uygulama Adımları

1. `vnext.partner.config.json`; `partner/` bileşenleri: `xd-child`, `xd-remote`, `xd-worker` workflow'ları,
   `xd-child-review-view`, `xd-child-approve`, `xd-child-ext`.
2. `core/Tasks/cross-domain-lab/*` (6 task).
3. `core/Workflows/cross-domain-lab/xd-parent.json` + `src/*.csx` (subflow mapping, 6 task mapping'i, auto rule).
4. `test.runsettings` → `VNEXT_PARTNER_BASE_URL`; `CrossDomainLabFixture`; `CrossDomainLabTestBase`.
5. `SubflowDescentTests`, `TriggerTaskTests`.
6. Lab'a karşı koştur: `dotnet test --filter CrossDomainLab` (önce `labs/cross-domain/lab.sh verify`).
7. Dokümanlar: `Tests/CrossDomainLab/README.md`, `TEST-SCENARIOS.md` satırı, `labs/cross-domain/README.md` linki.
8. Lab ekleri (kullanıcı notu): Dapr `appconfig` Configuration'ı (nameResolution + tracing) compose
   override ile sidecar'lara `--config` olarak verilir; `useDapr`/`domain` task config'i toolkit
   şablonlarında olmadığı için elle yazılır ve runtime DTO'larına karşı doğrulanır.
9. Manuel: OpenObserve'da `Discovery.Resolve/partner` etiketleri (AC-12).

## 9. Açık Sorular — CEVAPLANDI

1. `data` gövde descent'i → **runtime değişikliği yok**, mevcut davranış pinlenir (AC-04).
2. `partner/` `npm run validate` kapsamı dışında → **kabul**, README'de not.
3. Task 11 yanıt şekli → uygulama sırasında `StartTask` executor'ından doğrulanır (mini-kapı gerekmez,
   yalnız mapping'in okuduğu yol değişir).
4. Authorize forward → partner child transition `roles:[xd-approver]`, parent `overrides` yok.

## 10. Bilinen Tuzaklar

- Task/onEntry mapping'leri aynı transition'da task-journal çakışması → her task ayrı transition'da.
- `LocalDomainPublisher` state-level `task.domain` referanslarını yayınlanan domain'e çevirir → core
  task'ları core'da kalır; cross-domain sadece `config.domain`/`process.domain` ile ifade edilir.
- `useDapr:true` yoksa `DirectTrigger`/`Start` executor'ları HTTP `baseUrl`'e düşer; lab'da o da çalışır
  ama Dapr yolu ölçülmez → her task config'inde `useDapr:true` zorunlu.
- Parent auto transition child bitince `xd-after-subflow`'a geçmeli: `RunAutomaticTransitionsStep`
  resume yolunda (order 80) koşar; rule `childCompleted == true`.
- DbMigrator DI (`IDomainDiscoveryResolver`) — çözüldü, imaj güncel olmalı.

## 11. Uygulama Notları (2026-09-03)

- **Sapma yok**, plan birebir uygulandı; §9 kararları geçerli. Ek: vnext-schema 0.0.52 `useDapr`'ı
  yalnız task 15/19 için tanıyor → 11/12/13/14 task dosyaları `npm run validate`'te "must match then
  schema" veriyor. Runtime her tipte `useDapr` okuyor (`*Task.cs` `TryGetProperty`); alan bilinçli
  korundu (kullanıcı notu: "toolkit henüz bilmiyor, ek olarak sen ekle"). Şema takibi: vnext-schema.
- Script `code` alanları `labs/cross-domain/encode-scripts.py` ile doldurulur (VS Code eklentisi yok).
- Test tarafında `Xunit.SkippableFact` eklendi (SDK xunit v2, `Assert.Skip` yok).
- Lab: `nameformat` daprd 1.16.x'te yok → `appconfig` açık `mdns`; vnext `etc/*/dapr/config.yaml`
  de aynı düzeltmeyi aldı.
- Tek test düzeltmesi: `child-approve` rol kısıtlı olduğu için state fonksiyonu `xd-approver` ile
  okunuyor (rolsüz okuma yalnız cancel girdilerini gösterir — doğru davranış).
- Task 11 yanıtında `id` bulundu (`remoteInstanceId` dolu); `remoteKey` yedeği kullanılmadı.
