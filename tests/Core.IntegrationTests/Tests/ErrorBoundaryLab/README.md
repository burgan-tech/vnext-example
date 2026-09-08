# error-boundary-lab — error boundary çözümlemesi, incident yüzeyleri, retry

## Neyi denetliyor

Üç iddia:

1. **Error boundary çözümlemesi.** Bir task hatası hangi boundary tarafından karşılanır
   (Task → State → Global), aynı seviyede hangi kural kazanır (`EffectivePriority` ASC →
   specificity DESC), ve her aksiyon (`abort`/`retry`/`rollback`/`notify`/`ignore`/`log`) instance'ı
   nereye götürür. Verdict, incident'ın `boundaryLevel` / `boundaryAction` alanlarından **doğrudan**
   okunur — dolaylı bir yan etkiden çıkarılmaz.
2. **Incident yüzeyleri.** State function'ın `incident` bloğu ve `GET .../instances/{id}` içindeki
   `metadata.incident` **birebir aynı** şekli taşır ve **içerik değil link** verir:
   `{ hasActiveIncident, active: { href }, history: { href } }`. `active` yalnız açık arıza varken
   görünür ve `GET .../instances/{id}/incidents/active`'i gösterir; kapalıyken o endpoint 404
   (`Instance:100037`) döner ki bu normal bir yanıttır. Geçmiş `GET .../instances/{id}/incidents`
   ile sayfalanır. Hepsi vnext `feature/incident-table` (issue #865) ile geldi.
3. **Retry.** `POST .../instances/{id}/retry` faulted olmayan instance'ı reddeder, hatayı silahlı
   bırakan retry yeniden fault eder, `{"attributes":{"shouldFail":false}}` ile yapılan retry aynı
   task'ı başarıya çevirir; ETag retry sonrası değişir (304 → 200).

## Neden var

`feature/incident-table` incident'ları `Instances.Incidents` jsonb kolonundan `InstanceIncidents`
tablosuna taşıdı, `HasActiveIncident`'ı denormalize kolon yaptı, state function'a `incident` bloğunu
(`ResponseShapeVersion` v8) ve yeni geçmiş endpoint'ini ekledi. Bunlar unit + Postgres testleriyle
doğrulandı; **kontratı yalnız çalışan bir instance doğrular**. Ayrıca vnext-example'da error boundary
hiç senaryo olarak yoktu (`TEST-SCENARIOS.md` girişinde kapsamda sayılıyordu ama satırı yoktu) ve
`POST .../retry` hiçbir testte çağrılmıyordu.

Bu paket koşulurken **beş gerçek kusur** ortaya çıktı ve hepsi düzeltildi (aşağıdaki tabloya
bakın). İkisi çökme seviyesindeydi (migration FK şeması, incident'ın başka bir context tarafından
yeniden INSERT edilmesi), üçü davranışsaldı (abort'un iki incident yazması, yeniden fault eden
retry'ın Active olarak yerleşmesi, kurtulan instance'ın hâlâ aktif incident bildirmesi).

## Akış şeması

İki workflow üretilir (`build-error-boundary-lab.py`). İkiye bölünmesinin sebebi: workflow seviyesi
bir boundary **her** task için `HasAnyBoundary`'yi true yapar, dolayısıyla "hiçbir yerde boundary yok"
kontrolü global boundary ile aynı akışta yaşayamaz.

```
error-boundary-lab                         error-boundary-lab-global
  start → ready (hub)                        start → g-ready (hub)   [global: 503 → rollback]
    case-task-abort ─────────► F               g-case-global-rollback ──► rolled-back  (Global)
    case-task-rollback ──────► rolled-back     g-case-state-over-global ► notified     (State)
    case-within-level-order ─► rolled-back     g-case-unhandled-no-match► F (eşleşme yok)
    case-within-level-priority► notified
    case-retry-exhaust ──────► F  (retry tükendi → abort)
    case-retry-recover ──────► landed (3. denemede 200)
    case-ignore / case-log ──► landed
    case-unhandled ──────────► landed (boundary yok → hata işlenmez)
    case-retry-endpoint ─────► F  (retry endpoint testleri)
    case-state-notify ───► zone-state-notify  ─► notified (State)
    case-precedence-… ───► zone-precedence    ─► F        (Task, State'i yener)
    case-secured ────────► zone-secured (queryRoles) ─► F
```

**Zone state'ler tesadüf değil:** runtime'ın baktığı state boundary'si `instance.CurrentState`
üzerindedir. OnExecute (30) ChangeState'ten (50) önce, OnEntry (60) sonra koşar — yani bir
transition'ın task'ı **kaynak** state'in, zone'un `onEntries` task'ı **hedef** state'in boundary'sini
görür. State seviyesini deterministik yerleştirmenin tek yolu budur.

Hata enjeksiyonu iki kaynaktan: `.csx` içinde fırlatan script task (dış bağımlılık yok) ve MockLab
(`api/eb-lab/fail-500`, `fail-503`, `flaky` = 500,500,200).

## Nasıl çalıştırılır

Runtime **lokal derlenmiş** olmalı — bu özellikler henüz release edilmedi, container image'ı eski
kodu taşır.

```bash
# 1) altyapı (vnext çalışma alanı) — bbt-development ağını da yaratır
cd ../vnext/etc/docker && ./run-docker.sh

# 2) migration (InstanceIncidents tablosu). DbMigrator kendi Dapr sidecar'ını kapatır:
#    her koşudan ÖNCE sidecar'ı ayağa kaldırın.
docker compose up -d vnext-db-migrator-dapr
cd ../.. && dotnet run --project workers/BBT.Workflow.DbMigrator --launch-profile DbMigrator

# 3) dört host, her biri ayrı terminalde
dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host --launch-profile http
dotnet run --project execution/BBT.Workflow.Execution.HttpApi.Host --launch-profile http
dotnet run --project workers/BBT.Workflow.Workers.Inbox --launch-profile http
dotnet run --project workers/BBT.Workflow.Workers.Outbox --launch-profile http

# 4) MockLab (bu repo) — seed yalnız koleksiyon YENİYSE içeri alınır
cd ../vnext-example && docker compose up -d       # gerekirse: docker compose down -v && docker compose up -d

# 5) testler
cd tests/Core.IntegrationTests
dotnet test --settings test.runsettings --filter "FullyQualifiedName~ErrorBoundaryLab"
```

| Değişken | Anlamı |
| --- | --- |
| `VNEXT_BASE_URL` | Konteyner başlatmayı atlar, verilen orchestrator'a bağlanır (`test.runsettings` içinde `http://localhost:4201`). |
| `MOCKLAB_BASE_URL` | MockLab admin API'si; varsayılan `http://localhost:3001`. Ulaşılamazsa MockLab'a bağlı iki test **skip** olur, gerisi koşar. |

Bileşen değiştirdiyseniz: `python3 core/Workflows/error-boundary-lab/build-error-boundary-lab.py`
ve `VERSION`'ı **yükseltin** — yayınlanmış sürüm değişmezdir, aynı sürüme publish 409 döner ve
runtime eski gömülü script'i sunmaya devam eder.

## Beklenen sonuç

| Sınıf | Doğruladığı |
| --- | --- |
| `IncidentLifecycleTests` | Incident alanları (`errorCode`/`errorLayer`/`statusCode`/`task`/`traceId`), bloğun **link** taşıdığı ve ilan edilen `active.href`'in gerçekten cevap verdiği (test URL'i yeniden kurmaz, ilan edileni takip eder), state bloğu ile `metadata.incident`'in aynı olduğu, sayfalama (`hasNext`), retry'ın 400/200 davranışı, ETag 304→200. |
| `BoundaryLevelTests` | Task/State/Global verdict'leri; Task'ın daha spesifik State kuralını yenmesi; State'in Global'i gölgelemesi; "boundary var ama eşleşmiyor" kontrolü. |
| `WithinLevelOrderingTests` | Spesifik kuralın default wildcard'ı yenmesi, düşük priority sayısının kazanması, boundary transition'ı biten incident'ın **resolve** edilmesi. |
| `RetryPolicyTests` | `1 + maxRetries` çağrı (`httpAttempts` instance verisinden), tükenince fallback kuralın uygulanması, 3. denemede kurtarma (incident **yok**). MockLab gerekir. |
| `ContinueActionsTests` | `ignore`/`log`/boundary-yok: pipeline devam eder, incident **yok**, ve hook'un kalan task'ları **koşmaz**. |
| `SecuredIncidentsTests` | `queryRoles` kapısı: rolsüz çağıran state function, `/incidents` ve `/incidents/active` üçünden de **403** alır (404 değil — yoksa "arıza yok" ile "bilmeye yetkin yok" ayırt edilemez); rollüsü 200. |

## Ölçülen davranış (tasarlanan değil)

Bu testler runtime'ın **bugün ne yaptığını** sabitler. Aşağıdakiler düzeltilirse testler bilerek
kırmızıya döner — o zaman README, `TEST-SCENARIOS.md` ve testler birlikte güncellenmelidir.

1. **`retryCount` her zaman 0.** Alan, boundary aksiyonunun retry policy'sinden doldurulur; engine
   bu policy'yi aksiyon sonucuna hiç iliştirmez. Deneme sayısı yalnız instance verisinden
   (`httpAttempts`) okunabilir — testler oradan okur.
2. **`ignore`/`log` incident yazmaz ve hook'un kalanını atlar.** Bu **doğru davranış** olarak
   onaylandı (2026-09-07), ölçülüp pinlenmesinin sebebi dokümante edilen niyetin ("informational,
   zaten resolved bir incident") kodla çelişmesi: engine devam-tipi sonucu boundary aksiyonu
   iliştirmeden döndürdüğü için pipeline step'i incident yazan dala hiç girmez. Ayrıca koordinatör
   hatalı task'ta durur, sonraki task'lar koşmaz (marker damgası yok).

## Bu paketin bulup düzelttiği kusurlar

| Kusur | Belirti | Düzeltme |
| --- | --- | --- |
| Migration'ın FK'si yanlış şemayı işaret ediyordu | `MoveInstanceIncidentsToTable` içindeki iç FK `principalSchema: "public"` idi; `MultiSchemaNpgsqlMigrationsSqlGenerator` `CreateTableOperation`'ın **iç** foreign key'lerini yeniden yazmaz, böylece her flow şemasının FK'si `public."Instances"`'ı gösterdi ve backfill 13 şemada `23503` ile düştü | `principalSchema: null` (repo'nun `20250523074013_Initial`'dan beri sürdürdüğü konvansiyon) |
| Incident'lar başka bir context tarafından yeniden INSERT ediliyordu | Retry isteği `PK_InstanceIncidents` ihlaliyle ambient UoW commit'inde patlıyor, yanıt gövdesi yarıda kesiliyordu (`ResponseEnded`) | `LoadActiveIncidentsAsync`'in no-tracking dalı satırları artık EF navigation'ına değil, aggregate'in **detached** listesine koyuyor; detached bir incident'ın resolve'u `IInstanceIncidentRepository.ResolveAllAsync` ile açıkça yazılıyor |
| Bir abort **iki** incident yazıyordu | Boundary kendi verdict'ini yazıyor, pipeline fault'un kendisi için ikinci bir `Pipeline` katmanlı satır (`ErrorBoundaryAbort`) daha yazıyordu; faulted instance'ta `incident.active` bu ikinci satır olduğu için `boundaryAction` taşımıyordu | Üç task step'i (`RunOn{Execute,Entry,Exit}TasksStep`) incident'ı artık **save'den önce** kaydediyor, böylece `HasActiveIncident` bayrağı aynı save'le commit ediliyor ve `MarkInstanceFaultedAsync`'in yeniden yüklemesi fallback satırını atlıyor |
| Yeniden fault eden retry `Active` olarak yerleşiyordu | Yanıt `"status":"F"` derken instance Active kalıyordu (ambient scope'ta tracked yüklenen aggregate, iç scope'un yazdığı F'i commit'te eziyor) ve instance bir daha retry edilemiyordu (`Instance:100027`) | `InstanceRetryAppService` aggregate'i **no-tracking** yüklüyor; unfault `IInstanceRepository.TryUnfaultAsync` ile tek `ExecuteUpdateAsync`'lik bir CAS. Whole-graph rewrite da böylece ortadan kalktı |
| Blok incident içeriğini gömüyordu | State function en sıcak okuma yolunda incident tablosunu okuyordu, geçmiş endpoint'inin döndürdüğü veri kopyalanıyordu ve bir bayatlık deliği vardı: bir state'te A resolve edilip B açıldığında hiçbir fingerprint üyesi kımıldamadığı için `If-None-Match` ile doğrulayan client 304'te kalıp A'yı göstermeye devam ediyordu | Blok artık `{ hasActiveIncident, active: { href }, history: { href } }`; `ResponseShapeVersion` v9. State function ve instance GET'i hiç incident sorgusu atmıyor, liste görünümündeki sayfa başına batch sorgu tamamen kalktı, delik de kapandı |
| Kurtulan instance hâlâ aktif incident bildiriyordu | Başarılı retry'dan sonra instance `landed`/`C` olmasına rağmen `hasActiveIncident` true kalıyordu; `HasActiveIncident` fingerprint materyali olduğu için long-poll eden client'a da yansıyordu | `Instance.ResolveActiveIncident` → `ResolveOpenIncidents`: yüklü kümedeki **tüm** açık satırları kapatır ve bayrağı yeniden hesaplar. Detached retry yolunda karşılığı `ResolveAllAsync` |

## Doğrulama durumu

Lokal runtime'a (`feature/incident-table`) karşı art arda iki koşu: **31/31 yeşil**, koşu başına
~1 dk 46 sn. İlk doğrulama 2026-09-06'da ölçülen davranışa göre yapıldı; üç davranışsal kusur
2026-09-07'de düzeltildikten sonra ilgili testler ters çevrildi ve paket yeniden iki kez koşuldu.
MockLab ayakta; kapalıyken `RetryPolicyTests`'in iki testi skip olur.
