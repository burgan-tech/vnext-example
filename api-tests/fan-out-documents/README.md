# fan-out-documents — FanOutTask (TaskType 21) uçtan uca senaryosu

Instance verisindeki bir koleksiyonu çalışma anında çözüp, referans verilen **iç task**'ı her item
için **paralel** koşturan ve item sonuçlarını join politikasıyla **TEK bir task sonucuna ve TEK bir
instance-data yazımına** indiren fan-out primitifini doğrular.

## Neyi denetliyor

Tek cümlelik iddia: **N item paralel koşar, N kere yazmaz.** Bunun etrafındaki dört davranış:

1. **Inline fan-out + per-item input binding** — `itemsPath: "$.documents"` ile koleksiyon çözülür,
   `IFanOutMapping.ItemInputHandler` klonlanmış `HttpTask`'ın URL'ini item başına değiştirir.
2. **`allSettled` join politikası** — kısmi başarısızlık hata değil **veri**dir; FanOut task'ın
   kendisi başarılı sayılır, akış özet üzerinden dallanır.
3. **Tek-yazim degismezi (single-write invariant)** — batch'in tamamı **bir** InstanceData sürümü
   üretir, item sayısı kadar değil.
4. **İki seviyeli bulkhead** — batch-yerel `execution.maxDegreeOfParallelism` (burada 3) ve süreç
   geneli `Workflow:FanOut:MaxConcurrentItems` (varsayılan 64) birlikte tavan koyar.

## Neden var

vNext runtime'ına **FanOutTask (TaskType 21)** eklendi (`feature/fanout-task-design`, **2026-08-21**).
Bu senaryo o geliştirmenin regresyon bekçisi olarak aynı gün yazıldı.

Korunması gereken asıl şey **tek-yazim degismezi**. Tasarımın tamamı bunun için var: her item kendi
izole branch context'inde (`ScriptContext.CreateParallelBranch()`) ve kendi DI scope'unda
(`IServiceScopeFactory.CreateAsyncScope()` — EF change tracker thread-safe değil) tam task
engine'den geçer, ama `TaskEngineExecutionOptions.SuppressDataApply = true` ile. Item'ın branch
context'i **atılır**, `MergeParallelBranch()` ile geri birleştirilmez. Batch'in tek yazım noktası
`OutputHandler`'dır. Bu bastırma bozulursa fan-out, tek bir aggregate üzerinde yarışan N eş zamanlı
yazıcıya dönüşür — ve o noktada senaryodaki diğer her garanti değersizdir. Mutlu yol testleri bunu
yakalamaz; sürüm aritmetiği yakalar.

İkinci sebep: **kısmi başarısızlık dallanmasının platform tarafından karara bağlanmaması.**
`allSettled` + `{resultKey}Summary` + auto transition kalıbı runtime'ın önerdiği yol ama her seferinde
bir **akış tasarımı tercihi**. Bu senaryo o kalıbın çalışır bir örneğini de sabitler.

Üçüncü sebep: **`itemAlias` ve `join.ordered` bugün no-op.** Her ikisi de config'de kabul edilir,
saklanır, klonlanır — ama executor okumaz. Senaryo `ordered`'a güvenmeyip sonuçların **her zaman**
index sırasında geldiğini ayrıca assert eder, ki ileride durable mode bunu sessizce değiştiremesin.

## Akış şeması

```
start-fan-out-documents  (onExecute: fanout-stamp-task / FanOutStartMapping)
        │                 → documentCount, versionBeforeFanOut
        ▼
documents-received (Initial)
        │  process-documents  (manual)
        ▼
documents-processing (Intermediate)
        │  onEntry order 1 : fan-out-documents-task  ← TaskType 21, allSettled, maxDop 3
        │                    ↳ N × process-document-task (HTTP → MockLab), paralel
        │                    ↳ OutputHandler: documentResults + documentResultsSummary
        │                                     + versionSeenByFanOut        ◄── TEK YAZIM
        │  onEntry order 2 : fanout-stamp-task / FanOutStampAfterMapping
        │                    ↳ versionAfterFanOut                          ◄── ölçüm noktası
        │
        │  auto (order 90, onEntry'den SONRA):
        ├── failed > 0  ──► documents-partial-failure (Finish / Error)
        └── failed == 0 ──► documents-completed       (Finish / Success)
```

**Kritik adım: onEntry sırası.** `order 1` (fan-out) ile `order 2` (damga) arasında hiçbir şey
çalışmaz — transition yok, state değişimi yok, başka task yok. Tek-yazim assertion'ı tam olarak bu
iki damga arasındaki farktır. Damgayı bir transition'a ya da başka bir state'e taşırsanız araya
giren her yazım farkı sessizce büyütür ve senaryonun **en önemli** iddiası gürültüye dönüşür.

## Tek-yazim degismezi nasıl ölçülüyor

Orchestration host'unda instance-data **sürüm geçmişini** listeleyen bir uç **yok**:
`GET .../instances/{id}` yalnız birleşik `attributes` döner, state function'da sürüm bilgisi yoktur,
`GET .../instances/{id}/data` tek bir sürüm döner ve gövdede sürüm string'i taşımaz. Sürüm geçmişi
yalnızca **monitoring host**'unda (`/api/v1/monitor/.../data` → `versionHistory[]`, port 4203) ve
testing SDK'sının container stack'i o host'u ayağa kaldırmıyor.

Bu yüzden **akış kendi sürüm işaretlerini raporluyor**:

| Damga | Nerede okunuyor | Anlamı |
| --- | --- | --- |
| `versionBeforeFanOut` | start transition task'ı | batch öncesi taban |
| `versionSeenByFanOut` | fan-out `OutputHandler` | batch'in **üzerine yazacağı** sürüm (P) |
| `versionAfterFanOut` | hemen sonraki onEntry task'ı | batch'in **ürettiği** sürüm |

Her okuma `Instance.LatestData.Version` üzerinden ve **ilgili task kendi çıktısını uygulamadan
önce** yapılır. Task sonucu yazımları her zaman patch artırır (`VersionStrategy.IncreasePatch`),
dolayısıyla:

```
patch(versionAfterFanOut) - patch(versionSeenByFanOut) == 1      ✔ tek yazım
                                                       == N      ✘ item başına yazım
```

Bağımsız ikinci kanıt — public data ucundan sonda. Bu uç var olmayan bir sürüm için **404 vermez**,
**latest'a da düşmez**: `200` döner ve gövdedeki `data` **null** olur. Test bunu okur:

- batch'in yazdığı sürüm (`versionAfterFanOut`) **çözülmeli**,
- head (`+1`, damga task'ının kendi satırı) **çözülmeli**,
- head'in ötesi (`+2`) **çözülmemeli**.

Runtime'ın sürüm eşleşmesi **prefix toleranslıdır** (`"1"` ve `"1.0"` en yüksek satıra çözülür) —
sondalar bu yüzden her zaman tam üç parçalı sürüm gönderir.

## Bilinen kapsam açığı — item journal

Her item kendi `InstanceTask` satırına `{fanOutTaskKey}#{index}` anahtarıyla yazılır
(`FanOutTaskExecutor`: `JournalTaskKey = $"{task.Key}#{item.Index}"`). Bu satırlar **yalnızca
monitoring host**'unun `GET /api/v1/monitor/{domain}/workflows/{workflow}/instances/{id}/tasks`
ucundan (`taskDefinitionKey`) görünür; SDK ne o host'u başlatıyor ne de ucu sarmalıyor.

Integration test bu assertion'ı **uydurmak yerine içermiyor**. Doğrulamak için:

```bash
python3 api-tests/fan-out-documents/fanout-load.py --monitor-url http://localhost:4203
```

veya `core/Workflows/fan-out-documents/fan-out-documents.http` içindeki monitoring isteği.

## Nasıl çalıştırılır

### Ön koşullar

```bash
cd etc/docker && ./run-docker.sh        # altyapı + MockLab (vnext deposunda)
dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host --launch-profile http  # 4201
dotnet run --project execution/BBT.Workflow.Execution.HttpApi.Host --launch-profile http          # 4202
```

MockLab seed'i `etc/docker/config/seed/fan-out-documents-collection.json` içermeli
(`api/fan-out/documents/process` + gecikmeli `.../process-slow`).

> **Runtime sürümü.** FanOutTask henüz release edilmedi. Container image'ı **eski kodu taşır**;
> senaryoyu lokalde derlenen runtime'a karşı koşun. Integration testler için
> `tests/Core.IntegrationTests/test.runsettings` içinde `VNEXT_BASE_URL`'i açın.

### Integration testler

```bash
dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~FanOut"
```

### Yük testi

```bash
pip3 install --user nothing   # bağımlılık yok: yalnız Python 3 standart kütüphanesi

python3 api-tests/fan-out-documents/fanout-load.py --publish
python3 api-tests/fan-out-documents/fanout-load.py \
    --instances 20 --items 10 --slow-per-instance 3 --fail-per-instance 1 \
    --ceiling 64 --max-dop 3 --tolerance 1.15 --straggler-threshold 4.0 --timeout 420
python3 api-tests/fan-out-documents/fanout-load.py --monitor-url http://localhost:4203
```

| Parametre | Varsayılan | Anlamı |
| --- | --- | --- |
| `--instances` | 12 | eş zamanlı instance sayısı (M) |
| `--items` | 8 | instance başına doküman (N) |
| `--slow-per-instance` | 2 | MockLab'in 1500 ms gecikmeli route'una giden item; straggler ölçümü buna dayanır |
| `--fail-per-instance` | 1 | MockLab 500 dönen item; `allSettled` altında batch yine başarılı olmalı |
| `--ceiling` | 64 | `Workflow:FanOut:MaxConcurrentItems` değeri |
| `--max-dop` | 3 | task bileşenindeki `execution.maxDegreeOfParallelism` |
| `--tolerance` | 1.15 | bulkhead ölçüm toleransı |
| `--straggler-threshold` | 4.0 | `max/p50` üst sınırı |
| `--monitor-url` | yok | verilirse item journal doğrulanır |

Akışı değiştirdiğinizde JSON'u elle düzenlemeyin — üretici script'i çalıştırın ve sürümü bir üst
değere alın (aynı sürüm 409 / `100002` ile dedupe edilir):

```bash
python3 core/Workflows/fan-out-documents/build-fan-out-documents.py
```

## Ölçüm yaklaşımı ve başarısızlık eşikleri

MockLab'in dokümante edilmiş bir **istek-log ucu yok**, dolayısıyla "MockLab'de gözlenen tepe eş
zamanlılık" doğrudan okunamıyor ve uydurulmuyor. Yük testi bunun yerine runtime'ın kendi kaydettiği
per-item `durationMs` değerlerinden **zaman-ağırlıklı ortalama** hesaplıyor:

```
efektif_eşzamanlılık = Σ(item süresi) / batch duvar saati
```

Her item süresi uçuşta olan bir downstream çağrıyı temsil ettiği için bu, gerçek eş zamanlılığın
zaman-ağırlıklı ortalamasıdır. **Ortalama ≤ tepe** olduğundan yorum asimetriktir: ortalamanın tavanı
aşması **kesin** bir ihlaldir, tavanın altında kalması **güçlü ama mutlak olmayan** kanıttır. Kesin
tepe ölçümü monitoring/trace tarafındaki `FanOut.Item` span'lerinden (`vnext.fanout.item.queue_wait_ms`)
okunur.

| Kontrol | Eşik | Aşılırsa ne demek |
| --- | --- | --- |
| `BULKHEAD` | `efektif_eşzamanlılık ≤ min(ceiling, M × maxDop) × tolerance` | süreç geneli bulkhead tutmuyor; M instance × maxDop kadar çağrı downstream'e biniyor |
| `TEK-YAZIM` | her instance için `patch(after) − patch(seen) == 1` | per-item `SuppressDataApply` bozulmuş; fan-out N yazıcıya dönmüş |
| `SAĞLIK` | Faulted yok, hepsi terminal state'te | `allSettled` join'i item hatasını task hatasına çeviriyor ya da batch takılıyor |
| `STRAGGLER` | `max(item süresi) / p50 ≤ 4.0` | tek yavaş item batch'i domine ediyor; `itemTimeoutSeconds` / maxDop ayarına bakın |

Çıkış kodu: hepsi geçerse `0`, biri düşerse `1`.

## Beklenen sonuç

```
  PASS  START      -> 12/12 instance basladi
  PASS  SAGLIK     -> settled=12 faulted=0 takili=0 sure=...s
  PASS  BULKHEAD   -> ... <= ...
  PASS  TEK-YAZIM  -> 0 instance tek-yazim degismezini bozdu
  PASS  STRAGGLER  -> ... <= 4.0
  SKIP  ITEM-JOURNAL  -> --monitor-url verilmedi
SONUC: PASS
```

Integration test tarafında dört test: mutlu yol (5/5, tam sonuç kümesi ve özet), tek-yazım (mutlu
yol), tek-yazım (kısmi başarısızlık — asıl regresyon riski burada), kısmi başarısızlık dallanması
(`failed: 2`, hata kodları, `documents-partial-failure`).

## Akışı kurarken dikkat edilenler

- **Zero-script path bu senaryoda yeterli DEĞİL.** Mapping'siz fan-out yalnızca item'ın branch
  context'inin `Body`'sini set eder; bir `HttpTask`'ın URL'i klonlanmış task üzerinde yaşar, script
  body'sinde değil. Kendi config'i item başına değişmesi gereken her iç task (HTTP url, SOAP
  envelope, Dapr method) **`ItemInputHandler` zorunlu kılar**.
- **Mapping vermek varsayılan çıktı paketlemesini devre dışı bırakır.** `IFanOutMapping.OutputHandler`
  için varsayılan implementasyon yok (yalnız `ItemSelector`'da var). Bu yüzden
  `FanOutDocumentsMapping.OutputHandler` runtime'ın varsayılan şeklini **bilerek birebir taklit eder**
  (`documentResults` + `documentResultsSummary{total,succeeded,failed,timedOut}`), ki assertion'lar
  dokümante edilmiş varsayılan sözleşmeye karşı geçerli kalsın. `failedDocumentIds` ve
  `versionSeenByFanOut` senaryo enstrümantasyonudur, varsayılan şeklin parçası değildir.
- **`ItemKey` türetimi.** `FanOutItemsResolver.ExtractItemKey`: item'ın `id` string alanı, yoksa
  `key`, yoksa index. Dokümanlarımız `{ id, url }` olduğu için `ItemKey` doğrudan doküman id'sidir —
  mapping'de dynamic ile eşelenmeye gerek yok.
- **MockLab'de `delayMs` yalnızca mock seviyesinde var, rule seviyesinde yok.** Bilinçli straggler
  bu yüzden ayrı bir route (`.../process-slow`) ve mapping DOC-SLOW id'lerini oraya yönlendiriyor.
- **Task journal tekilliği** `(TransitionId, TaskId)` üzerindedir ve `order` anahtarın parçası
  değildir. `fanout-stamp-task` iki kez kullanılıyor ama **farklı transition'larda** (start ve
  `process-documents`), aynı transition içinde değil. Fan-out'un kendi item satırları zaten
  `{taskKey}#{index}` ile ayrışıyor.
- **`npm run validate` bu senaryoda TaskType 21'i reddeder.** `@burgan-tech/vnext-schema@0.0.52`'nin
  `task-definition.schema.json` enum'u `"20"`de bitiyor. Bileşen yanlış değil, şema paketi geride —
  ayrı bir release gerekiyor.
