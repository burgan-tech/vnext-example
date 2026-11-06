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
- **`metadata.modifiedAt`** (vnext `feature/modified-at-fix`): her çalışan transition onu ilerletir —
  biten instance'ta (`lab-completed`, `lab-cancelled`) değeri `completedAt` ile **aynı an**; state
  değiştirmeyen updateData da ilerletir, gövdesiz updateData'da yalnız task çıktısı veri yazsa bile,
  yalnız `tags` / `stage` değiştiren updateData'da da; hiçbir şey değiştirmeyen updateData onu oynatmaz.
  `modifiedAt` filtresi yeni değeri görür. Öncesinde yalnız Busy flip'leri ilerletiyordu: biten
  instance son transition'ı **başlamadan önceki** zamanı, updateData hiç değişmemiş değeri gösteriyordu.

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
| `FullLifecycle_ReachesLabCompleted` | Sıralı + paralel + kapı → `lab-completed` / `C`; `modifiedAt == completedAt` | `RunParallelAndOpenTheGateAsync` kullanır |
| `SequentialTasks_AllLandTheirOwnKeys` | Sıralı adım start gövdesinin ötesinde anahtar yazar | Zayıf iddia: `keys.Count > 1` |
| `ParallelTasks_AllLandTheirOwnKeys` | Paralel adım anahtar sayısını artırır, `lab-completed`'e varılır | Paylaşılan DbContext çakışmasını ilk açığa çıkaran vaka |
| `ConcurrentUpdateData_KeepsEveryAcceptedIncrement` | 5 kabul edilen updateData → `labUpdateCount == 5`; state değişmeden `modifiedAt` ilerler | `lab-sequential`'da koşar, kapıya dokunmaz |
| `Cancel_MovesTheLabToCancelled` | `cancel-lab` → `lab-cancelled`, terminal status; `modifiedAt == completedAt` | |
| `ModifiedAt_MovesOnATransition` | Active'de dinlenen transition (`run-sequential`) `modifiedAt`'i istekten sonraki bir ana taşır | Taban çizgisi — eski runtime'da da yeşil (Busy bırakma CAS'ı damgalıyordu) |
| `ModifiedAt_MovesOnUpdateData_WithoutAStateChange` | Art arda üç gövdeli updateData: state `lab-sequential`/`A` kalır, `modifiedAt` **her birinde** ilerler | Gövde ve `LabUpdateCounterMapping` task'ı ayrı veri satırı yazar; tekrar, "bir kez damgala" kısayolunu yakalar |
| `ModifiedAt_MovesOnUpdateData_WhoseOnlyChangeIsItsTaskOutput` | Gövdesiz (`{}`) updateData: tek veri satırı task çıktısı; `modifiedAt` yine ilerler | Damgayı yalnız gövdeye bağlayan bir düzeltmenin kaçırdığı vaka |
| `ModifiedAtFilter_FindsTheInstanceOnlyAfterItChanged` | `modifiedAt gt <ilk değer>` filtresi değişiklikten önce boş, sonra instance'ı döner; değişiklik bir updateData (yalnız veri) — eski runtime'da kırmızı | Liste uç noktası, `attributes.testId` ile daraltılmış |
| `ModifiedAt_MovesOnUpdateData_ThatChangesOnlyTagsOrStage_AndNotWhenNothingChanges` | Yalnız zarf alanı değiştiren updateData (önce `tags`, sonra `stage`) veri satırı yazmaz (`entityEtag` aynı) ama `modifiedAt`'i ilerletir; aynı `tags` + `stage` yeniden gönderilince hiçbir şey değişmez, `modifiedAt` de | Task'sız bir updateData gerekir — lab'inki her zaman task çıktısı (+1) yazar — bu yüzden `rs-plain` (row-security-lab'in `rowSecurity`'siz kontrol akışı) kullanılır; `?sync=true` |

## Bağımlılıklar ve ön koşullar

| Bağımlılık | Durum |
|---|---|
| Runtime | `core`, `VNEXT_BASE_URL` (`test.runsettings`: `http://localhost:4201`), lokal build |
| System package | Gerekmez — `lab-script-task` ve `lab-probe-task-*` `core/Tasks/data-integrity-lab/` altında |
| MockLab | **Gerekir** — `integration-test-collection.json`: `POST api/test/process` (dört paralel dalın hepsi; `API_BASEURL` → `Example:ApiBaseUrl`, varsayılan `http://localhost:3001`) |
| Başka akış | `row-security-lab`'in `rs-plain` akışı (task'sız `rs-update` updateData'sı) — yalnız `ModifiedAt_MovesOnUpdateData_ThatChangesOnlyTagsOrStage_AndNotWhenNothingChanges` için; SDK `core/**`'u yayınlarken (veya `wf sync`) onu da yayınlar |
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

## Son koşu — `modifiedAt` (2026-11-06)

vnext `feature/modified-at-fix` @ `4bb9cfca`, lokal derleme, `core` @ `http://localhost:4201`, `DataIntegrityLab` +
`ErrorBoundaryLab.BoundaryLevelTests` + `ChainBusy`: **32/32 yeşil**, art arda iki koşu. Master ile birleşik
`aea96f01`'de (master `4be79ab4`, hop başına DB round-trip'lerini azaltan değişiklik dahil) yeniden: **32/32**,
komşular **59/59**. **Kırmızı kontrolü:** aynı 32 test, vnext `src/` master'a döndürülerek derlenmiş
runtime'a karşı **9 beklenen kırmızı** verdi — biten/iptal/fault instance'ta `modifiedAt ≠ completedAt`; gövdeli,
gövdesiz, art arda ve 5'li updateData'da, yalnız `tags`/`stage` değiştiren updateData'da, `modifiedAt` filtresinde
ve SubFlow bekleyen parent'ın kendi `$self` shared transition'ında (`ChainBusy`) `modifiedAt` hiç değişmedi. Taban
çizgisi `ModifiedAt_MovesOnATransition` iki tarafta da yeşil.

Postgres (`Aether_WorkflowDb`, son koşu penceresi, pencerede instance'ı olan yedi şemanın hepsi): `rs_plain` dışındaki
122/122 instance'ta `ModifiedAt` ≥ son transition'ın `StartedAt`'i; terminal 50/50'de `ModifiedAt = CompletedAt`;
son hop'u updateData olan 8/8 lab instance'ında `ModifiedAt` en yeni veri satırının `EnteredAt`'i. `rs_plain`'de
(2 instance) damga beklendiği gibi `stage` hop'unun penceresinde, son (hiçbir şey değiştirmeyen) hop onu oynatmadı,
veri satırı 1 (yalnız start). Birleşik koşu (`aea96f01`, 32 + 59 test tek pencerede): `rs_plain` dışındaki 145/145
instance'ta `ModifiedAt` ≥ son `StartedAt`, terminal 38/38'de `= CompletedAt`, son hop'u updateData olan 4/4 lab
instance'ında en yeni veri satırının `EnteredAt`'i; `rs_plain` damgası yine `stage` hop'unda.

Komşular aynı runtime'da (long-poll arm, SubFlow handoff/settlement ve `history: none` Finish yolları için regresyon
kontrolü): `AuthorizationChainLab.AckAndParentRetainedTests` + `SubflowOverrideLab.LongPollOverrideTests` +
`SubflowOrchestration` + `HistoryNoneLab` **59/59**; postgres: transition'ı olan 89/89 instance'ta `ModifiedAt` ≥ son
`StartedAt`, terminal 13/13'te `= CompletedAt`, long-poll arm'da bekleyen 1/1 damgalı.

Bu koşular iki şey yakaladı (düzeltildi, sonra koşuldu):
- Job ile yeniden giren bir hop `OwnsStatus = true` sınıflanıyor ama hiçbir şeyi flip etmemiş olabiliyor (SubFlow
  bekleyen parent). Damga artık hop'un sonunda, sonucu bilinen yerde (settlement) uygulanıyor.
- "Transaction başına bir damga" kısayolu denendi ve geri alındı: Npgsql havuzdaki bağlantıda transaction nesnesini
  yeniden kullanıyor, 5'li updateData'nın 3.–5. hop'u `modifiedAt`'i hiç oynatmadı (postgres: `ModifiedAt` en yeni
  satırdan ~0,5 s eski). Testler yalnız "bir kez ilerledi" dediği için yakalamadı; `ModifiedAt_MovesOnUpdateData_WithoutAStateChange`
  artık art arda üç updateData'nın her birinin ilerlettiğini doğruluyor.

Maliyet (OpenObserve, iki master + sekiz branch koşusu — ikisi son ağaçta —, koşu başına 27–31 updateData satırı):
damgalı bir updateData veri satırı INSERT'ünün koşu medyanı 0,64–0,81 ms, master'da aynı INSERT 0,36–0,48 ms — satır
başına ~+0,3–0,4 ms (gövde + task yazan updateData'da ~+0,7 ms). Damgasız INSERT'ler 0,39–0,45 ms, master 0,36–0,39 ms; iki master koşusuyla bu fark
koşudan koşuya kaymadan ayrılamıyor. Ayrıntı ve `pgbench` ölçümleri: vnext `docs/runtime/instance-filtering-and-queries.md`
§ `modifiedAt`.

MockLab: `error-boundary-lab` koleksiyonu eski bir MockLab container'ında yoktu (seed yalnız YENİ koleksiyonu
içeri alır); güncel seed'le taze container'da koşuldu.

## Dikkat noktaları / bilinen istisnalar

- **State değiştirip Busy'de dinlenen hop'ta onEntry'nin yazdığı veri `modifiedAt`'i ayrıca oynatmaz.** Damga order
  50'deki state değişiminde atılır. SubFlow state'ine giren parent'ta onEntry task'ının satırı aynı hop'ta birkaç ms
  sonra gelir ve hop Busy'de biter (bırakma CAS'ı yok). Birleşik koşuda 13 ChainBusy parent'ı, 2–18 ms. Runtime
  sözleşmesi bu: vnext `docs/runtime/instance-filtering-and-queries.md` — transition içinde eklenen veri ayrıca
  damgalanmaz. Parent'ın bir sonraki hop'u yeniden damgalar.
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
