# Tests/FanOut — FanOutTask (TaskType 21) integration testleri

Bu klasörde **iki** test sınıfı var, iki ayrı akışa karşı:

| Sınıf | Akış | Neyi denetliyor |
| --- | --- | --- |
| `FanOutDocumentsTests` | `fan-out-documents` | **Tek** konfigürasyonun (`allSettled`) mutlu yolu, kısmi başarısızlık dallanması ve **tek-yazım değişmezi**. |
| `FanOutConfigMatrixTests` | `fan-out-config-matrix` | **Konfigüre edilebilir yüzey**: dört `join.policy`, `join.minSuccess`, boş koleksiyon, `mode: durable` reddi, item bazlı `errorBoundary`, `maxDegreeOfParallelism`, `itemTimeoutSeconds` ↔ `batchTimeoutSeconds` ayrımı. |

`fan-out-documents` senaryosunun tam anlatımı (akış şeması, neden var, MockLab seed'i, yük testi,
eşikler): [`api-tests/fan-out-documents/README.md`](../../../../api-tests/fan-out-documents/README.md).

---

# 1. FanOutDocumentsTests — tek konfigürasyon, tek-yazım değişmezi

## Testler

| Test | İddia |
| --- | --- |
| `FiveDocuments_AllSucceed_ReachCompletedWithTheFullResultSet` | 5 doküman, hepsi başarılı; `documentResults` 5 satır, **index sırasında**; `{resultKey}Summary` = `{5,5,0,false}`; akış `documents-completed`. |
| `TheWholeBatch_ProducesExactlyOneInstanceDataVersion` | Batch **tek** InstanceData sürümü üretti. |
| `TheWholeBatch_ProducesExactlyOneInstanceDataVersion_EvenWhenItemsFail` | Aynısı kısmi başarısızlık altında. **Asıl regresyon riski burada:** her başarısız item'ın hatasını anında persist eden bir implementasyon mutlu yolu bozmadan degişmezi bozar. |
| `TwoOfFiveFail_RoutesToPartialFailure_AndTheFailedRowsCarryErrorCodes` | `allSettled` altında instance Faulted **değil**, `documents-partial-failure`'a dallanır; özet `failed: 2`; başarısız satırların hepsi bir hata kodu taşır. |

## Tek-yazim degismezi nasıl assert ediliyor

Orchestration host'unda sürüm geçmişi ucu **yok** (yalnız monitoring host'unda, port 4203 —
SDK stack'i onu başlatmıyor). Bu yüzden akış kendi sürüm işaretlerini instance verisine yazıyor:

Batch, `documents-processing` state girişinde iki damga task'ı arasında **sarılı** koşuyor:

- `versionBeforeFanOutBatch` — onEntry **order 1**; batch'ten hemen önceki `LatestData.Version`.
- *(order 2 = batch)*
- `versionAfterFanOut` — onEntry **order 3**; batch'ten hemen sonraki sürüm.

Aralarında başka hiçbir şey çalışmaz (transition yok, state değişimi yok). Her task sonucu bir
patch artırdığı için iki damga arasında tam **iki** yazıma izin var — önce-damgasının kendi
sonucu, ve tüm batch:

```
patch(versionAfterFanOut) - patch(versionBeforeFanOutBatch) == 2     ✔  (1 damga + 1 batch)
                                                            == 1 + N ✘  item başına yazım
```

Ayrıca bağımsız bir sonda: `GET .../instances/{id}/data?version=X` var olmayan sürüm için **404
vermez**, latest'a **düşmez** — `200` + `data: null` döner. Test patch hattının tamamını sayar:
önce-damgasının satırı (`+1` sabitini **varsaymak yerine doğrular**), batch'in satırı, head, ve
head'in bir ötesinin **olmadığı**.

> **Neden ayrı bir önce-damgası var?** Bu işi eskiden fan-out mapping'inin `OutputHandler`'ı
> yapıyordu. O üye artık opsiyonel (vnext `4bd8941b`: default interface implementation `null`
> döner ⇒ runtime'ın varsayılan paketlemesi devreye girer) ve senaryo bu geri-düşüşü fiilen test
> edebilmek için handler'ı override etmeyi bıraktı. Varsayılan paketleme senaryo enstrümantasyonu
> taşıyamaz, damga da bu yüzden kendi task'ına taşındı. **Handler'ı geri eklemeyin** — eklerseniz
> testler runtime'ın çıktısını değil senaryonun kendi ürettiği çıktıyı doğrulamış olur.

> **Bu iki testi ayrı ayrı değiştirmeyin.** `documents-processing` onEntry sırası
> (damga → fan-out → damga) ölçümün tamamıdır. Araya bir task, bir transition ya da bir state
> değişimi sokan her düzenleme farkı sessizce büyütür ve assertion "geçiyor" görünmeye devam
> ederken hiçbir şeyi denetlemez.

## Kapsam açığı — item journal (bilinçli)

Item'lar `{fanOutTaskKey}#{index}` anahtarlı `InstanceTask` satırlarına yazılır
(`FanOutTaskExecutor`: `JournalTaskKey = $"{task.Key}#{item.Index}"`), ama bu satırlar yalnızca
monitoring host'unun `.../instances/{id}/tasks` ucundan (`taskDefinitionKey`) görünür. SDK ne o
host'u başlatıyor ne de ucu sarmalıyor, bu yüzden assertion **uydurulmadı — yok**.

Opt-in doğrulama:

```bash
python3 api-tests/fan-out-documents/fanout-load.py --monitor-url http://localhost:4203
```

---

# 2. FanOutConfigMatrixTests — konfigüre edilebilir yüzey

## Neden var

FanOutTask'ın **konfigürasyonu** uçtan uca hiç doğrulanmamıştı. Unit testler geçiyordu ve bir
production domain'i mutlu yolu kullanıyordu; ama `join.policy` değiştiğinde, `minSuccess`
tutmadığında, koleksiyon boş geldiğinde, item bazlı `errorBoundary` devredeyken ya da
`maxDegreeOfParallelism` gerçekten sıkıştırdığında runtime'ın ne yaptığını hiçbir integration test
görmüyordu. Bu sınıf o boşluğu kapatıyor (2026-08-21).

## Tasarım kararı — 1 akış, N task bileşeni

Matrisin değiştirdiği her şey (`join.policy`, `join.minSuccess`, `mode`, `execution.*`, item bazlı
`errorBoundary`) **FanOut task bileşeninin kendi config'inde** yaşıyor. Bileşen başına statiktir ve
çağıranın runtime'da besleyebileceği bir yol **yok** — dolayısıyla config ekseni zorunlu olarak
varyant başına bir task bileşeni demek.

Toplanabilen kısım akış: **tek** bir `fan-out-config-matrix` akışı, tek dispatcher state
(`matrix-ready`) ve case başına bir manuel transition. Her transition'ın hedef state'inin
onEntry'sinde **yalnızca** o case'in fan-out batch'i var. Item karışımı (hangi doküman başarılı /
başarısız / yavaş) start body'sinden geldiği için **çağıran tarafından parametrize**. Sonuç:
1 akış + 9 task bileşeni + 1 test sınıfı — 9 neredeyse-aynı akış yerine.

## Join verdict'i nasıl gözlemleniyor

Case state'inde tek bir onEntry task'ı (batch) ve `case-settled`'a giden **koşulsuz** tek bir auto
transition var; akışta ayrıca `case-failed`'a yönlendiren **tek bir global errorBoundary** bulunuyor:

| Join | Instance |
| --- | --- |
| başarılı | auto transition ateşlenir → `case-settled` |
| başarısız | global boundary (`rollback` → `to-case-failed`) → `case-failed` |

**İki taraf da POZİTİF assert ediliyor.** Bu önemli, çünkü ilk revizyon böyle değildi.

> ### Ölçülmüş tuzak — fault'a bakmayın
>
> İlk revizyon akışta **hiç** errorBoundary tanımlamıyordu ve başarısız join'i **Faulted instance**
> olarak gözlemliyordu. Bu **yanlış**: boundary tanımlı değilken başarısız bir onEntry task'ı
> **hiç işleme alınmıyor** ve koşulsuz auto transition yine ateşleniyor.
>
> Kontrol deneyi: `documents`'a array yerine **string** vermek `FanOutItemsResolver`'ı fırlatıyor —
> yani elde edilebilecek en sert hata, `Result<TaskInvocationResult>.Fail` — ve instance buna rağmen
> `case-settled`'a ulaştı, `caseResults` hiç yazılmadan. Yani **beş başarısız-join case'i sessizce
> geçiyordu.**
>
> Boundary'nin yokluğu "hata instance'ı fault'lar" değil, "**hata işleme alınmaz**" demek.
> Bu akışta başarısızlığı asla başarının yokluğundan çıkarmayın.

`action: "abort"` bir transition taşıyamıyor — validator *"Transition must not be specified when
Action is Abort."* diyor, çünkü transition'sız abort **fault yolunun kendisi**. Transition taşıyan
aksiyon `rollback`.

Ayrıca aynı yolla öğrenilen iki yazarlık kuralı (builder script'inde de yazılı):

- **Her `triggerType: 1` transition bir `rule` taşımak ZORUNDA.** "Tek başına auto transition, rule'ı
  her zaman true dönüyorsa geçerlidir" ifadesi *true dönen bir rule* demek, *rule'ın yokluğu* değil.
  Rule'sız publish 400 + `"Auto transition '…' must have a rule defined."` veriyor — dolayısıyla
  `src/CaseSettledRule.csx`.
- **Case transition'ı ateşlemeden önce start transition'ın settle etmesi gerekiyor**, yoksa runtime
  409 (Busy) dönüyor. `RunCaseAsync` önce non-Busy bekliyor.

`WaitForTerminalAsync` beklenen terminal state'i beklerken **ötekini** görürse anında patlıyor: ikisi
de terminal olduğu için yanlış olana düşmek bütçeyi yakıp "timeout" olarak raporlanırdı ve gerçek
verdict gizlenirdi.

## Test matrisi ve SONUÇLAR

**17 test · 14 geçti · 3 kaldı** (2026-08-22, lokal runtime `ccfcec01`).
`--filter FanOut` bütünü: **21 test · 18 geçti · 3 kaldı** (kardeş `FanOutDocumentsTests` 4/4).

Kalan 3'ün **tamamı gerçek runtime defect'i** ve bilinçli kırmızı — **ortam engeli kalmadı**.
İkisi **aynı** defect'in (F1) iki farklı iptal nedenini pinliyor.

| Test | Case transition | Item karışımı | İddia | Sonuç |
| --- | --- | --- | --- | --- |
| `JoinAll_EveryItemSucceeds_SettlesWithAFullResultSet` | `run-join-all` | 3 başarılı | `all` tam başarıda geçer; özet `{3,3,0}`, `timedOut` false | ✅ |
| `JoinAll_OneItemFails_FailsTheJoin` | `run-join-all` | 2 başarılı + 1 `DOC-FAIL` | `all` **atomik**: tek başarısızlık batch'i düşürür → `case-failed`; **ve sonuç seti yine yazılır** | ✅ |
| `JoinAll_EmptyCollection_SucceedsVacuously` | `run-join-all` | `[]` | Boş batch `all` altında **vacuously başarılı** (yazarlık tuzağı — bilinçli pinlendi) | ✅ |
| `JoinAllSettled_EmptyCollection_Succeeds` | `run-join-all-settled` | `[]` | `allSettled` her zaman başarılı; boş batch özel bir durum değil | ✅ |
| `JoinQuorum_MinSuccessMet_SettlesEvenThoughItemsFailed` | `run-join-quorum` | 2 başarılı + 2 `DOC-FAIL` | `minSuccess: 2` tutuyor → içinde başarısızlık **olan** batch başarılı | ✅ |
| `JoinQuorum_MinSuccessNotMet_FailsTheJoin` | `run-join-quorum` | 1 başarılı + 2 `DOC-FAIL` | Eşiğin bir altı → `case-failed`. Üstteki testten **tek farkı item karışımı** | ✅ |
| `JoinQuorum_EmptyCollection_FailsBecauseZeroCannotMeetAThreshold` | `run-join-quorum` | `[]` | `all` ile asimetri: quorum'un boş-batch özel durumu yok, 0 başarı eşiği geçemez | ✅ |
| `JoinFirstSuccess_OneItemSucceeds_SettlesDespiteTheFailures` | `run-join-first-success` | 1 başarılı + 2 `DOC-FAIL` | Bir başarı yeter | ✅ |
| `JoinFirstSuccess_NoItemSucceeds_FailsTheJoin` | `run-join-first-success` | 2 `DOC-FAIL` | Hiç başarı yok → `case-failed` | ✅ |
| `JoinFirstSuccess_EmptyCollection_FailsLikeQuorumWithMinSuccessOne` | `run-join-first-success` | `[]` | `firstSuccess` ≡ `quorum(minSuccess=1)`; aynı girdide **asla** ayrışmamalı | ✅ |
| `PerItemErrorBoundary_Retry_ContainsExhaustionToItsOwnItem` | `run-item-boundary-retry` | 2 başarılı + 1 `DOC-FAIL` | `retry(maxRetries 2)` tükenen item **bir** başarısız satır olur; kardeşleri ve batch'i düşürmez | ✅ |
| `PerItemErrorBoundary_Ignore_DoesNotConvertAFailedItemIntoASuccess` | `run-item-boundary-ignore` | 2 başarılı + 1 `DOC-FAIL` | **Karakterizasyon (F3):** wildcard `ignore` başarısız item'ı başarılıya **çevirmiyor** — hâlâ `failed`, hâlâ `all`'ı düşürüyor | ✅ |
| `EarlyStop_CancelledItems_CarryTheDocumentedFanOutItemCancelledCode` | `run-join-first-success` | 5 başarılabilir | Early-stop ile iptal edilen item'lar `FanOut:ItemCancelled` taşımalı | ❌ **F1 defect** |
| `DurableMode_IsRefusedWithAnActionableValidationError` | — (doğrudan publish) | — | `durable` reddedilmeli **ve** sebebi actionable olmalı (4xx) | ❌ **F2 defect** |
| `BatchTimeout_SerialBatch_StampsBatchTimeoutAndMarksTheBatchTimedOut` | `run-batch-timeout-serial` | 3 × `DOC-SLOW` | `mdop 1`, itemTO 2s / batchTO 3s: en az bir item `FanOut:BatchTimeout`, `timedOut` **true** | ✅ |
| `MaxDegreeOfParallelism_RaisedCeiling_LetsEveryItemFinishInsideTheSameBudget` | `run-parallel-baseline` | 3 × `DOC-SLOW` | Üsttekinin **kontrol kolu**: aynı config, aynı item'lar, tek fark `mdop 4` → 3/3 başarılı | ✅ |
| `ItemTimeout_StampsItemTimeoutOnTheStraggler_AndLeavesTheBatchNotTimedOut` | `run-item-timeout` | 2 hızlı + 1 `DOC-SLOW` | `itemTO 1s` / `batchTO 30s`: doğru item düşer, `timedOut` **false** kalır (**ikisi de doğru**) — ama kod `FanOut:ItemTimeout` olmalı | ❌ **F1 defect** |

**F1** — **Uçuşta iptal edilen item kendi fan-out nedenini değil iç task'ın çıplak
`Task:Unknown:<taskKey>:TaskCanceledException`'ını taşıyor.** Üç iptal nedeninin **hepsinde** var
(item deadline 0/1 doğru, batch deadline 1/2, early-stop 1/4); ayrım, item'ın iç task'a girmiş olup
olmaması — hiç başlamamış item doğru kodu alıyor. E1 çözülmeden önce yalnız early-stop ölçülebildiği
için defect daha dar sanılmıştı.

> **Yükseltici:** `summary.timedOut`, `FanOutTaskExecutor`'da item kodlarından türetiliyor
> (`Any(ErrorCode == BatchTimeout)`). Batch deadline'ında kesilen **tüm** item'lar uçuştaysa
> (`mdop >= item sayısı` — sık görülen şekil) hepsi kodu kaçırır ve `timedOut` gerçekten timeout olmuş
> bir batch için **`false`** okunur. `BatchTimeout_SerialBatch` bugün geçiyor çünkü `mdop 1`
> hiç-başlamamış bir item garantiliyor.

**F2** — `mode: "durable"` publish'te **reddediliyor** (iyi haber: rezerve mod canlı definition
olamıyor) ama `400` validation yerine **opak `500`** ile. `FanOutTask.Configure`'ın
`ArgumentException`'ı component-validation yoluna map edilmemiş.

> **E1 (ÇÖZÜLDÜ) — MockLab route'ları PREFIX ile eşliyor.** Yavaş mock `documents/process-slow`
> adresindeydi ve `documents/process` mock'u tarafından **yutuluyordu** (kanıt: `documents/process-XYZQQ`
> gibi anlamsız bir path bile hızlı mock'un body'sini döndürüyordu). Yani `delayMs` "uygulanmıyor"
> değildi — yavaş mock hiç **cevap veren taraf olmuyordu**. Route kardeş bir segmente taşındı:
> **`api/fan-out/slow-documents/process`** (ölçüm: 1.735s, `pages:120, slow:true`).
> `docker restart` yetmez, container **recreate** edilmeli (`up -d --force-recreate mocklab`).

> `AssertStragglerRouteIsActuallySlowAsync` guard'ı bu üç testin başında **duruyor ve kalıyor**. İki
> kontrol yapıyor: (1) body'de yavaş mock'un imzası (`"slow"`) — zamana bakmayan, prefix yutulmasını
> yakalayan kontrol; (2) süre ≥ 1s — eşik, straggler'lı case'lerin en büyük `itemTimeoutSeconds`'ı,
> "biraz gecikme oldu" değil. **En kritik olduğu yer `mdop` kontrol kolu**: gecikme yokken her item
> milisaniyede bitiyor, her tavan aynı sonucu veriyor ve o test **boş yere geçiyordu** — ilk koşularda
> tam olarak bu oldu.

Ayrıntılı kanıt, reprodüksiyon ve severity:
[`docs/fanout-configurable-surface-findings.md`](../../../../docs/fanout-configurable-surface-findings.md)

## Eşzamanlılık ve timeout iddiaları — duvar saati YOK

`mdop` çifti ve timeout case'leri **yalnızca gözlemlenebilir sonuç** üzerinden assert ediliyor:
hata kodları ve sayılar. Hiçbir testte geçen süre ölçülmüyor.

Tasarımın dayandığı çevresel önkoşul: straggler route'unun **1500ms** gecikmesi. `itemTimeoutSeconds`
tam saniye ve ≥ 1 olmak zorunda olduğundan 1500ms, minimum deadline'ı aşabilen en küçük değer — bu
yüzden tek bir yavaş mock üç case'in tamamına hizmet ediyor.

| Case | mdop | itemTO | batchTO | Neden |
| --- | --- | --- | --- | --- |
| item timeout | 4 | **1s** | 30s | 1500ms item deadline'ını %50 aşıyor; batch'in 28.5s payı var, yani `timedOut` **false** kalmalı — iki kodu ayıran şey bu |
| batch timeout (seri) | **1** | 2s | **3s** | Her item tek başına 1500ms < 2s, yani item deadline'ı ateşlenemez; **sadece** batch deadline kesebilir. Seri: ~1.5s, ~3.0s, ~4.5s → 3. item tartışmasız deadline'ın ötesinde, dolayısıyla assertion sınırdaki 2. item'a **bağlı değil** |
| mdop kontrol kolu | **4** | 2s | **3s** | Aynı item'lar, tek fark tavan: 3 eşzamanlı 1500ms item 3s deadline'ın içinde ~1.5s'de biter |

`batchTimeoutSeconds` **iki kolda birlikte** 2 → 3 yapıldı (task bileşenleri 1.1.0'a bump edildi).
2s'de paralel kolun payı 3 eşzamanlı HTTP çağrısı + item başına engine yükü için sadece 500ms'ti —
yani **kırılgan olan kol kontrol koluydu**, en kötü yer. 3s'de payı üçe katlanıyor, seri kolun ayırt
edici aralığı da genişliyor (3s deadline vs 4.5s seri iş).

> İkisi **birlikte** hareket etmeli; tek farkları `maxDegreeOfParallelism` olmalı, yoksa iddia
> eşzamanlılık iddiası olmaktan çıkar.

- **Seri kol** sayıları **sınır** olarak assert ediyor (`failed >= 1`, en az bir
  `FanOut:BatchTimeout`), tam sayı olarak değil — deadline ateşlendiğinde hangi item'ın uçtuğunu bir
  sonuç assertion'ının bildiğini varsayması doğru olmazdı.
- **Paralel kontrol kolu** 3/3 başarılı bekliyor.

Guard'daki `Stopwatch` sınıftaki **tek** saat ölçümü ve runtime'ı değil **mock'u** ölçüyor:
fixture'ın önkoşulunun geçerli olduğunu doğruluyor. Runtime davranışına dair hiçbir iddia duvar
saatine dayanmıyor.

## Neden `mode: durable` bileşeni diskte değil

Kasten geçersiz bir bileşen `core/Tasks/` altında dururken SDK'nın `LocalDomainPublisher`'ı onu
**her fixture başlangıcında** publish etmeye çalışır ve `FAIL` satırı sonsuza kadar gerçek bir
regresyon gibi okunur. Bu yüzden probe bileşeni testin içinden `POST /api/v1/definitions/publish`
ile gönderiliyor. Sürüm her koşuda tekil (`9.0.{saniye}`) — 409 "already exists" asla test edilen
reddin yerine geçmesin diye.

## Kapsam açığı — retry **deneme sayısı** (bilinçli)

Item bazlı retry'ın kaç deneme yaptığı yalnızca `{fanOutTaskKey}#{index}` anahtarlı `InstanceTask`
satırında görünüyor; o da yalnız monitoring host'unda (4203) ve ne SDK stack'i ne lokal dev stack'i
onu başlatıyor. MockLab'in sequential-response özelliği de **mock başına**, item başına değil —
eşzamanlılık altında "bir kere patla sonra başar" ifade edemiyor. Bu yüzden retry testi **deneme
sayısını uydurmuyor**; ölçebildiği ve yük taşıyan iddiayı assert ediyor: retry tükenmesi kendi
item'ında **kalıyor**.

## Bilinen ortam kısıtı — `npm run validate`

`@burgan-tech/vnext-schema` 0.0.52 task enum'unu 20'de kesiyor, `attributes.type: "21"` reddediliyor.
Bu klasörün **dokuz** task bileşeni de, mevcut `fan-out-documents-task.json` de bu yüzden
validate'te kırmızı. **Beklenen** ve ayrı bir iş olarak takip ediliyor; publish şema validasyonunu
baypas ettiği için runtime yolu çalışıyor. Bileşenleri bayat şemayı memnun etmek için eğip bükmeyin.

---

## Çalıştırma

```bash
# iki sınıf birlikte
dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~FanOut"

# yalnız konfigürasyon matrisi
dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~FanOutConfigMatrix"
```

FanOutTask henüz release edilmedi; container image eski kodu taşır. Lokalde derlenen runtime'a karşı
koşmak için `tests/Core.IntegrationTests/test.runsettings` içinde `VNEXT_BASE_URL`'i
(`http://localhost:4201`) açın ve vNext çalışma alanında altyapı + **4 app'i** ayağa kaldırın
(`--launch-profile http`). `VNEXT_BASE_URL` boşsa SDK kendi Testcontainers stack'ini **image'lardan**
kaldırır — image eski runtime'ı taşıdığı için bu her sonucu sessizce geçersiz kılar.

Akış JSON'u üretmek (`.csx` değiştiyse **VERSION'ı bump edin**):

```bash
python3 core/Workflows/fan-out-config-matrix/build-fan-out-config-matrix.py
```
