# data-integrity-lab

Instance data yazımını zorlamak için kurulmuş akış (InstanceData v2: her task çıktısı anında
persist, kimlik lock altında). Önemli olan state machine değil **veri**: paralel dallar kendi
scope'larından yazar, kayıp ya da mükerrer bir yazım başarısız transition olarak değil **eksik
anahtar** olarak görünür. Akışın sonundaki `lab-collect` bir fan-in kapısıdır ve updateData ile açılır.

## Neyi denetliyor

- **Sıralı task yazımları**: aynı transition'da dört sıralı script task (order 1–4); üç yazıcının her
  biri kendi anahtarını bırakır, dördüncüsü veriyi bayt-aynen echo'lar (DataHash dedup probu).
- **Paralel task yazımları**: aynı order'da dört HTTP task (`lab-probe-task-1..4`), her dal kendi DI
  scope'unda yazar; birleştirme dördünü de korumalıdır.
- **updateData'nın kilitsiz kabulü ve sayaç bütünlüğü**: kabul edilen her `update-lab-progress`
  `labUpdateCount`'a tam bir artış bırakır.
- **updateData'nın auto değerlendirmeyi yeniden koşması**: `lab-collect`'te park eden instance,
  `labUpdateCount >= labThreshold` olduğunda `auto-lab-complete` ile `lab-completed` / `C`'ye geçer.
- **Well-known `cancel`**: `cancel-lab` → `lab-cancelled`.

## Akış

`data-integrity-lab` v1.0.2 — JSON `build-workflow-json.py` ile `src/*.csx`'ten üretilir.

| State | Tip | Çıkış | Trigger | Task (hook) |
|---|---|---|---|---|
| *(start)* | — | `start-data-integrity-lab` → `lab-sequential` | manual | `lab-script-task` / `LabStartMapping` (`labUpdateCount=0`, `labThreshold` gövdeden, yoksa 4) |
| `lab-sequential` | Initial | `run-sequential` → `lab-parallel` | manual | onExecute: `lab-script-task` ×4 (order 1–3 `LabSeqStep1..3`, order 4 `LabDupEchoMapping`) |
| `lab-parallel` | Intermediate | `run-parallel` → `lab-collect` | manual | onExecute: `lab-probe-task-1..4` (hepsi order 1, HTTP) |
| `lab-collect` | Intermediate | `auto-lab-complete` → `lab-completed` | **auto**, `LabThresholdRule` | — |
| `lab-completed` | Finish/Success | — | — | — |
| `lab-cancelled` | Finish/Terminated | — | — | — |
| *(updateData)* | — | `update-lab-progress` → `$self` | manual | `lab-script-task` / `LabUpdateCounterMapping` (+1) |
| *(cancel)* | — | `cancel-lab` → `lab-cancelled` | manual | — |

## Testler

| Test | Kanıtladığı | Not |
|---|---|---|
| `FullLifecycle_ReachesLabCompleted` | Sıralı + paralel + kapı → `lab-completed` / `C` | `RunParallelAndOpenTheGateAsync` kullanır |
| `SequentialTasks_AllLandTheirOwnKeys` | Sıralı adım start gövdesinin ötesinde anahtar yazar | Zayıf iddia: `keys.Count > 1` |
| `ParallelTasks_AllLandTheirOwnKeys` | Paralel adım anahtar sayısını artırır, `lab-completed`'e varılır | Paylaşılan DbContext çakışmasını ilk açığa çıkaran vaka |
| `ConcurrentUpdateData_KeepsEveryAcceptedIncrement` | 5 kabul edilen updateData → `labUpdateCount == 5` | `lab-sequential`'da koşar, kapıya dokunmaz |
| `Cancel_MovesTheLabToCancelled` | `cancel-lab` → `lab-cancelled`, terminal status | |

## Bağımlılıklar ve ön koşullar

| Bağımlılık | Durum |
|---|---|
| Runtime | `core`, `VNEXT_BASE_URL` (`test.runsettings`: `http://localhost:4201`), lokal build |
| System package | Gerekmez — `lab-script-task` ve `lab-probe-task-*` `core/Tasks/data-integrity-lab/` altında |
| MockLab | **Gerekir** — `integration-test-collection.json`: `POST api/test/process` (dört paralel dalın hepsi; `API_BASEURL` → `Example:ApiBaseUrl`, varsayılan `http://localhost:3001`) |
| Dapr | Akışa özgü bileşen yok |
| Caller rolleri / header'lar | Gerekmez (akışta `queryRoles`/`roles` yok) |
| Cross-domain | Gerekmez |
| morph-idm provider | Gerekmez |

Python scripti ayrıca `docker exec vnext-postgres psql` ile `Aether_WorkflowDb`'yi okur — postgres
container'ı bu adla ayakta olmalı.

## Nasıl çalıştırılır

```bash
dotnet test tests/Core.IntegrationTests \
  --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~DataIntegrityLab"

# Davranış/yük: satır matematiği, DataHash dedup, lock çakışması
python3 api-tests/data-integrity-lab/integrity-lab-test.py --publish --iterations 6 --threshold 4 --burst 4
```

## Dikkat noktaları / bilinen istisnalar

- **`lab-collect` bir fan-in kapısıdır, hang değil.** Tek çıkışı `labUpdateCount >= labThreshold`
  kuralı olan bir auto transition. Kural yanlışken instance **Busy'de park eder** — tasarım gereği:
  runtime `ResolveAvailableStep`'te auto transition'ı olan hedef state Busy kalır. Kapıyı açan,
  kilitsiz kabul edilen `update-lab-progress`'tir; her biri auto değerlendirmeyi yeniden koşar.
- **Status'u değil state'i bekleyin.** `run-parallel` için `RunAcceptedAsync` kullanmayın — o Busy'den
  çıkmayı bekler ve kapı açılana kadar sonsuza bekler. `RunParallelAndOpenTheGateAsync`: `run-parallel`
  → `lab-collect` state'ini bekle → sayaç eşiğe ulaşana kadar updateData gönder.
- **Testler eşiği start'ta verir** (`labThreshold = 3`). Gövdede yoksa kural 4 varsayar.
- **"run-parallel hang" kaydı bir test kusuruydu.** 2026-10-05'e kadar iki yaşam döngüsü testi
  updateData göndermeden `lab-completed`'i bekledi ve yedi hafta boyunca "stuck Busy / run-parallel
  hang" olarak kaydedildi. Elle doğrulandı (2026-10-05, lokal runtime): park eden instance'a dört
  updateData → her biri 202 → `lab-completed`.
- **Aynı task aynı transition'da aynı order'la iki dalda kullanılamaz**: task-journal `ExecutionKey`
  `(transition, task, order)` çakışır ve transition fault eder. Dört paralel dal bu yüzden dört ayrı
  task tanımına bağlı.
- JSON'u elle düzenlemeyin (base64 blob'lar): `python3 core/Workflows/data-integrity-lab/build-workflow-json.py`.
  Publish versiyon-değişmezdir; fixture değişikliği patch bump ister.
