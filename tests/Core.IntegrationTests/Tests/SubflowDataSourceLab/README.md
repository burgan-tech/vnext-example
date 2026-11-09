# subflow-data-source-lab

## Ne kontrol ediyor

Bir parent'ın içinde **aktif bir `S` SubFlow** varken poll eden bir istemcinin **hangi instance'ın
verisini** okuması gerektiğini ve runtime'ın bunun için ne verdiğini:

| İddia | Beklenen |
|---|---|
| Parent'ın state function'ı çocuğa iner | `state` / `view.hasView` / `transitions` çocuğun; `status` çocuk beklerken `A` |
| `data.href` parent'ı gösterir | Gövde parent'ın verisi (`parentMarker` var, `childMarker` yok) |
| `activeCorrelations[]` kaydının `href`'i | **Çocuğun data function'ı** (`/core/workflows/sds-child/instances/{childId}/functions/data`), `subFlowType "S"`, `isCompleted false` |
| Çocukta `$self` geçişi (`touch`) | Çocuğun verisi ve ETag'i değişir; parent'ın data function'ı eski ETag'e **304** döner (474774 / vnext#1084'ün koşulu); parent **state** ETag'inin davranışı **okunur ve raporlanır**, assert edilmez (dokümante değil) |
| Çocuğun state ilerlemesi (`next`) | Parent state ETag'i hareket eder (v0.0.79 notu: korelasyon mutasyonu) |
| Çocuk biter (`finish`) | Parent `parent-after`'a geçer, aktif `S` korelasyon kalmaz, parent verisi çocuğun çıktısını (`childResult`, `childCounter`) taşır ve eski ETag'e **200** döner |

Çocuğun manuel geçişleri **parent instance'ına** gönderilir; runtime aktif SubFlow'a yönlendirir
(`ForwardToActiveSubflowStep`). Testler de istemcinin yaptığı gibi yalnız parent'ı adresler.

## Neden var

Amorphie Feature 474774 (Tayfun Yılmaz, 2026-10-14) ve vnext#1084: istemci instance verisini data function'dan
eTag ile okur; runtime data function **her zaman parent'ın** verisini döner, subflow içindeyken veri çocukta
değişir, parent'ın verisi değişmediği için data function 304'te kalır ve çocuğun verisi view'a ulaşmaz.
İstemci tarafı karşılığı WMP 482207: `vnext-client-workflow-manager` motoru state cevabından sonra aktif `S`
korelasyon varsa veriyi **o kaydın `href`'inden** okuyacak. Bu lab, istemcinin dayanacağı runtime sözleşmesini
test olarak yazar ve istemci SDK'sının fixture kaydını üretir (`api-tests/subflow-data-source-lab/probe.py --record`).

## Neden ayrı bir akış

`subflow-orchestration` platformun referans deseni (`VNEXT-BUILD-PLAN.md`: değiştirilmez) ve hiçbir state'inde
view yok; `subflow-override-lab` tek bir iddiayı (override) ölçer ve 12 testi o sahneye bağlı. Burada gereken
şey farklı: **view'lı** bir `S` çocuk, her seviyede **ayırt edilebilir veri** ve çocuğun verisini state'ini
değiştirmeden oynatan bir geçiş. Yalnız script task, long-poll yok, MockLab gerekmez.

## Akışlar

Üretici: `core/Workflows/subflow-data-source-lab/build-subflow-data-source-lab.py` (view'lar
`core/Views/subflow-data-source-lab/`, task `core/Tasks/subflow-data-source-lab/sds-script-task.json`).

```
sds-parent (F)  parent-initial ─auto─▶ parent-wait (view) ─enter-child─▶ parent-child (S → sds-child)
                 ─auto (çocuk bitince)─▶ parent-after (view) ─finish-parent─▶ parent-done
sds-child  (S)  child-initial [childMarker, childCounter=0] ─auto─▶ child-wait (view)
                 ─touch ($self: childCounter+1, lastTouchAt)      ← veri değişir, state değişmez
                 ─next─▶ child-wait-2 (view) ─finish─▶ child-done
```

Veri: parent `parentMarker="parent"`, `parentCounter=0` (start mapping); çocuk `childMarker="child"`,
`childCounter` (+ parent'tan `parentMarker`); çocuk çıktısı parent'a `childResult=true` ile birleşir.
View'lar pseudo-ui `Column` + `Text("$instance.<alan>")`: ekran hangi instance'ın verisini gösterdiğini okutur.

## Testler

`SubflowDataSourceLabTests` (5):

- `TheParentStateDescendsToTheChild_AndTheCorrelationHrefIsTheChildsData`
- `TheParentsDataIsTheParents_TheHrefsDataIsTheChilds`
- `ATouchChangesOnlyTheChildsData` — parent data 304; parent state ETag davranışı konsola yazılır (`[sds] …`)
- `ANextMovesTheParentStateEtag`
- `AfterFinishTheParentResumes_WithTheChildsOutputMerged`

Aynı beş adım Python'da: `python3 api-tests/subflow-data-source-lab/probe.py [--publish] [--record <dir>]`
(.NET SDK olmayan makinede koşar; `--record` her cevabı status + ETag + gövde olarak JSON yazar).

## Nasıl koşulur

Ön koşul: infra + runtime lokal build ayakta (`etc/docker/run-docker.sh up core`), `VNEXT_BASE_URL`
`test.runsettings` içinde. MockLab **gerekmez**. Publish'i test SDK'sı yapar (`EnableDomainPublish`).

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~SubflowDataSourceLab" -v minimal
```

## Bilinen açıklar

- Parent **state** ETag'inin çocuğun `$self` geçişinde hareket edip etmediği runtime dokümanında yok; test
  okur ve raporlar. Hareket etmiyorsa çocuk verisindeki bu tür değişiklikler long-poll eden istemciye hiç
  ulaşmaz — bu runtime konusudur (474774'e not).
- vnext#1084'ün sunucu düzeltmesi (parent data ETag'ine çocuk extension çıktısının katılması) yayına girince
  `ATouchChangesOnlyTheChildsData`'daki **304 → 200** olur; test o gün güncellenir, bu beklenen değişimdir.
- `activeCorrelations[].href` runtime dokümanındaki alan tablosunda yazmıyor (`built-in.md`); kodda var
  (`InstanceQueryAppService.BuildInstanceStateOutputAsync`, Şubat 2026'dan beri). Dokümante edilmesi istenir.

## Koşu kaydı

Henüz koşulmadı (2026-11-09: lab yazıldı; runtime lokal build + koşu sırada).
