# Test Senaryoları — vNext Davranış Kontrol Noktaları

Bu repo, vNext runtime'ında yapılan geliştirmelerin **gerçek etkisini ve geçerliliğini** ölçmek için
platform ekibi tarafından kullanılır. Unit testler major değişikliklerde yeterli olmadığı için,
temel süreçlerdeki (pipeline, admission, locking, subflow, instance data, script engine, error
boundary) davranışlar burada uçtan uca doğrulanır.

Bu dosya **indeks ve geçmiş kaydıdır**: hangi senaryonun neyi denetlediği, neden eklendiği ve nasıl
çalıştırıldığı buradan görülür. Yeni senaryo eklendiğinde bu tablo **aynı commit'te** güncellenir;
geçerliliğini yitiren senaryo **silinmez**, `deprecated` işaretlenip sebebi yazılır.

---

## Feature Matrisi

| Senaryo | Test Edilen vNext Feature Seti | Neden Eklendi | Integration Test | Python Test | Durum |
|---|---|---|---|---|---|
| **chain-busy** | Accept-time subflow chain reserve · Busy-as-mutex · `$self` shared transition vs `updateData` lifecycle sınırı · start `initial → initial` semantiği · cancel propagasyonu (in-process ↕ distributed) · scheduled transition re-arm | `updateData`-only self-target profil sınırını pinlemek — `target: $self` "hook'ları atla" demek değil (2026-08-17) | `Tests/ChainBusy` (5 sınıf) | `api-tests/chain-busy/chain-busy-behaviour-test.py`, `chain-busy-accept-test.py` | ✅ Aktif |
| **script-race-lab** | Script engine: paylaşılan `AssemblyLoadContext`'te çift-derleme yarışı · `scripts.helpers` · subflow output mapping · parent kalıcı fault riski | `Script_XXXX already loaded` / `FileLoadException` yarışının fixture'ı; fix'siz runtime'da kaybedenler parent'ı kalıcı fault'lar (2026-08-18) | `Tests/ScriptRaceLab` | `api-tests/script-race-lab/race-load.py` (yük), `publish.py` | ✅ Aktif |
| **data-integrity-lab** | InstanceData v2: anında persist · lock altında kimlik · sıralı/paralel task yazımları · DataHash dedup (task + updateData) · versiyon satırı bütünlüğü | `feature/busy-as-mutex-locking` + InstanceData v2 geliştirmesini uçtan uca ölçmek (2026-08-13) | `Tests/DataIntegrityLab` | `api-tests/data-integrity-lab/integrity-lab-test.py` | ⚠️ Kısmen kırmızı — `run-parallel` konteynerli ortamda settle olmuyor (120s'te doğrulandı, hang) |
| **subflow-orchestration** | 3 seviyeli subflow (parent → child → grandchild) · `updateData`'nın auto transition'ı tetiklemesi · aktif subflow'lu parent'ta `updateData` data-only kısa devresi · parent `$self` shared transition · eşzamanlı updateData tutarlılığı | Platformun subflow referans akışı; `feature/busy-as-mutex-locking` F1/F1a/F8 fix'lerinin doğrulaması (2026-04-28) | `Tests/SubflowOrchestration` | `api-tests/subflow-orchestration/updatedata-concurrency-test.py` | ✅ Aktif |
| **contract-signing** | SubProcess (fire-and-forget) · `$self` auto loop ile N instance spawn · task ile kurulan çok-akışlı zincir (SubFlow korelasyonu **değil**) · instance data üzerinden zincir takibi · start mapping guard'sızlığında fault | Korelasyon yerine **task ile** kurulan akış zincirinin davranışını kapsamak <sup>1</sup> (2026-08-13) | `Tests/ContractSigning` | — | ✅ Aktif |
| **future-pay** | SubFlow state'leri (auto-complete + açık korelasyon) · parent resume · şema doğrulama (transition + master schema) · transition erişilebilirlik sırası | Kredi kullandırım senaryosu üzerinden çok-akışlı SubFlow davranışı (2026-06-12) | `Tests/FuturePay` | — | ⚠️ Bilinçli kapsam boşluğu — `sign-contract` sonrası collateral subflow + parent resume bacağı domain'de fault'landığı için assert edilmiyor |
| **money-transfer** | Rule-driven branching (auto transition) · scheduled/timeout timer arm · transition schema reddi · terminal HTTP task sonucunun instance data'ya yazılması | Tek akışlık referans süreç: kural, zamanlayıcı ve HTTP task kombinasyonu (2026-06-12) | `Tests/MoneyTransfer` | — | ✅ Aktif |
| **account-opening** | Wizard state tipi · çok dallı ürün seçimi · auto gate'lerin geri gönderimi · rol bazlı state function erişimi (`403`) · cancel/exit well-known transition'ları | Template ile gelen ilk referans akış (2025-11-21) | `Tests/AccountOpening` | — | ⚠️ Bilinçli kırmızı — konteynerli ortamda start, `account-type-selection` onEntry task'larında (`notify-state`, `set-or-get-cache`) fault'luyor; testler doğru, boşluğun sinyali olarak kırmızı tutuluyor |
| **soap-task-test** | `SoapTask` tipi · SOAP mapping (`SendVipSmsMapping.csx`) · başarı/hata rule'ları ile dallanma | SOAP task tipini uçtan uca doğrulamak <sup>1</sup> (2026-08-13) | — (yalnız `.http`) | — | ⚠️ Integration test yok — kapsam açığı |
| **l1-cache-lab** | Component cache versiyon çözümü: `latest` + artifact/major range (`"1"`) referansları · generation-anahtarlı L1 (in-process) cache'in publish görünürlüğü · pinned instance `flowVersion` kararlılığı · publish-only aktivasyon (re-initialize'sız) · view/task referanslarının sıcak cache'de anında yeni versiyona dönmesi | Runtime'a eklenen L1 component cache'in (vnext `feature/component-cache-l1`, 2026-08-20) "versiyon cache'de kaldı" riskini uçtan uca çürütmek; CD sözleşmesinin (publish bitince yeni sürüm MUTLAKA geçerli) regresyon bekçisi | — (bilinçli: publish-akışı doğruluk senaryosu; api-test yeterli) | `api-tests/l1-cache-lab/l1-cache-behaviour-test.py` (`--minor N` ile tekrar koşulabilir) | ✅ Aktif — 18/18 (2026-08-20, lokal L1 runtime) |
| **role-matrix-lab** | Yetkilendirme yüzeylerinin tutarlılığı: root vs state `queryRoles` (state EZER) · `transition.roles` allowlist / blacklist / predefined (`$InstanceStarter`) · `availableIn` rol daraltması (**AND**) · well-known transition'ların (`cancel`/`updateData`/`exit`) configured key + `kind` ile listelenmesi · master şemada `x-roles` alan budaması · `authorize` function'ının üç hedefi (transitionKey / functionKey / queryRoles) · custom function'da rol denetiminin **kaldırılmış** olması | Provider bazlı caller-role çözümü (`default` \| `morph-idm`) + custom function rol gate'inin kaldırılması; rol setinin KAYNAĞI değişirken grant motorunun davranışının değişmediğini pinlemek (2026-08-19, `feature/caller-role-provider`) | `Tests/RoleMatrixLab` (5 sınıf, 59 test) | — (bilinçli: doğruluk senaryosu, eşzamanlılık değil) | 🆕 Yazıldı, henüz koşulmadı |
| **secret-cache-lab** | `ScriptBase.GetSecretAsync` üzerinden in-process secret bundle cache (`ScriptSecretCache`) · bundle başına tek Vault fetch (single-flight) · cache'in request'ler arası (process-wide) yaşaması · TTL süresince bilinçli staleness · TTL dolunca canlı değere tazelenme · script task (type 7) içinden secret erişimi | Script secret fonksiyonlarının her çağrıda vault'a gitmesi yük altında vault'u darboğaza sokuyordu; `Scripting:SecretCache` (TTL 30 sn) geliştirmesinin hem kazancını hem de bayatlık sınırını uçtan uca pinlemek (vnext `claude/scriptbase-secret-cache-y86e03`, 2026-08-20) | — (bilinçli: doğrulama Vault audit log'u + saat ölçümüne dayanıyor, SDK assertion yüzeyinde yok) | `api-tests/secret-cache-lab/secret-cache-behaviour-test.py` | ✅ Aktif — 12/12 (2026-08-20, lokal runtime, TTL 30 sn) |
| **fan-out-documents** | `FanOutTask` (TaskType 21) inline mode · `itemsPath` ile koleksiyon çözümü + `ItemKey` türetimi (`id` → `key` → index) · `IFanOutMapping.ItemInputHandler` ile klonlanmış iç task'ın per-item mutasyonu (HTTP url) · `allSettled` join politikası ve `{resultKey}Summary{total,succeeded,failed,timedOut}` üzerinden auto transition dallanması (`RunAutomaticTransitionsStep`, order 90) · **tek-yazim degismezi**: N item → 1 InstanceData sürümü (`SuppressDataApply` + atılan branch context, tek yazım noktası batch'in çıktı adımı) · **`IFanOutMapping.OutputHandler` geri-düşüşü**: mapping yalnız `ItemInputHandler`'ı override eder, çıktıyı runtime'ın `BuildDefaultOutput`'u üretir · iki seviyeli bulkhead (batch-yerel `maxDegreeOfParallelism` × süreç geneli `Workflow:FanOut:MaxConcurrentItems`) · item journal anahtarları `{fanOutTaskKey}#{index}` · sonuçların `join.ordered`'dan bağımsız olarak her zaman index sıralı dönmesi | Runtime'a eklenen FanOutTask'ın (vnext `feature/fanout-task-design`, 2026-08-21) **tek-yazim degismezini** regresyona karşı sabitlemek: bastırma bozulursa fan-out tek bir aggregate üzerinde yarışan N eş zamanlı yazıcıya döner ve tasarımın var oluş sebebi kaybolur. İkincil olarak `allSettled` + özet + auto transition kısmi-başarısızlık kalıbının çalışır bir örneğini pinler (2026-08-21) | `Tests/FanOut` (`FanOutDocumentsTests`, 4 test) | `api-tests/fan-out-documents/fanout-load.py` (bulkhead tavanı + yük altında tek-yazım + straggler oranı) | ✅ 4/4 yeşil + yük testi 5/5 PASS (lokal runtime'a karşı, 2026-08-21). `npm run validate` TaskType 21'i hâlâ reddediyor (`@burgan-tech/vnext-schema@0.0.52` enum'u `"20"`de bitiyor; şema paketi release bekliyor) — publish yolu validate'ten geçmediği için engel değil: SDK `LocalDomainPublisher` component JSON'ını doğrudan `POST /api/v1/definitions/publish`'e atıyor. Item journal assertion'ı **bilinçli olarak yok**: satırlar yalnız monitoring host'unda (4203) görünüyor, SDK stack'i onu başlatmıyor — `fanout-load.py --monitor-url` ile opt-in |
| **fan-out-config-matrix** | `FanOutTask` (TaskType 21) **konfigüre edilebilir yüzeyi**: dört `join.policy` (`all` / `allSettled` / `quorum` / `firstSuccess`) verdict'lerinin **iki** yanında da · `join.minSuccess` tutan / tutmayan · `FanOutJoinEvaluator` **boş-batch** kuralı (`all`+`allSettled` vacuously başarılı, `quorum`+`firstSuccess` eşiği geçemediği için başarısız) · `mode: "durable"` reddi (`FanOutTask.Configure`, rezerve) · **item bazlı `errorBoundary`** (`ignore` verdict'i çevirir: `join: all` altında başarısız item batch'i düşürmez; `retry` tükenmesi kendi item'ında kalır) · `execution.maxDegreeOfParallelism`'in gerçek eşzamanlılığı sıkıştırması (eşleştirilmiş kontrol kolu, tek fark tavan) · `itemTimeoutSeconds` ↔ `batchTimeoutSeconds` ayrımı: `FanOut:ItemTimeout` vs `FanOut:BatchTimeout` + `summary.timedOut`'un yalnız **batch** deadline'ında yükselmesi · başarısız join'in task'ı düşürüp instance'ı Faulted etmesi (akışta bilinçli olarak **hiç** errorBoundary yok) | FanOutTask'ın config yüzeyi uçtan uca **hiç** doğrulanmamıştı — unit testler geçiyor ve bir production domain'i yalnız mutlu yolu kullanıyordu; `join.policy` değişince, eşik tutmayınca, koleksiyon boş gelince, item boundary devredeyken ya da tavan gerçekten sıkıştırınca runtime'ın ne yaptığını hiçbir integration test görmüyordu (vnext `feature/fanout-task-design`, 2026-08-21) | `Tests/FanOut` (`FanOutConfigMatrixTests`, 16 test) | — (bilinçli: doğruluk/konfigürasyon senaryosu; eşzamanlılık iddiaları hata kodu + sayı üzerinden, duvar saati yok) | ⚠️ **17 test · 12 geçti · 5 kaldı** (2026-08-21, lokal runtime `ccfcec01`). Dört join politikası, `minSuccess` ve **boş-batch kuralının tamamı DOĞRU**; başarısız join sonuç setini yine yazıyor. Kalan 5: **2 gerçek defect** — **F1** early-stop ile iptal edilen item'lar dokümante edilen `FanOut:ItemCancelled` yerine `Task:Unknown:<taskKey>:TaskCanceledException` taşıyor (aynı batch'te iki farklı kod); **F2** `mode: "durable"` publish'te reddediliyor ama `400` validation yerine opak `500` ile — ve **3 ortam**: MockLab `delayMs`'i uygulamıyor (1500ms yerine ~13-46ms), bu yüzden `itemTimeoutSeconds` / `batchTimeoutSeconds` / `mdop` **uçtan uca doğrulanamadı** (aynı arıza `fan-out-documents` yük testinin straggler oranını da anlamsız kılıyor). Ayrıca **tasarım düzeltmesi:** boundary tanımlı değilken başarısız onEntry task'ı instance'ı fault'lamıyor — ilk revizyonun fault temelli gözlemi beş case'i sessizce geçiriyordu; global `rollback` boundary + `case-failed` ile değiştirildi. Kanıt/reprodüksiyon: [`docs/fanout-configurable-surface-findings.md`](docs/fanout-configurable-surface-findings.md). `npm run validate` 9 task bileşenini de reddediyor — `fan-out-documents` ile aynı bilinen şema açığı |

<sup>1</sup> Gerekçe git geçmişinde kayıtlı değil (commit mesajı `updated`); senaryonun kendi
içeriğinden çıkarıldı. Doğrusunu bilen varsa bu satırı düzeltsin.

---

## Senaryo Detayları

Her senaryonun ayrıntısı kendi README'sinde / test sınıfının XML özetinde durur. Öne çıkanlar:

### chain-busy
`chain-busy-root` (A) → `chain-busy-middle` (B) → `chain-busy-leaf` (C). Zincir tamamen auto
transition ile kurulur; A ve B açık korelasyon boyunca **yapısal olarak** Busy, C `leaf-waiting`'de
Active. Ata seviyelerin Busy'si bilgi taşımaz — client'ın gördüğü tek sinyal C'dir. Her
onEntry/onExit/onExecute bir sayaç task'ı çalıştırır; `leaf-waiting`'de asla ateşlenmeyen 30
dakikalık bir scheduled transition ARMED bırakılır (`executeAtUtc` değişmediyse re-arm olmamış
demektir). Her şey public API'den doğrulanır, DB erişimi gerekmez.

`ChainBusySharedTransitionTests` ile `ChainBusyUpdateDataTests` **birlikte** sınırı pinler: aynı
state'lere karşı biri lifecycle'ın koştuğunu, diğeri koşmadığını iddia eder. **Birini diğeri olmadan
değiştirmek bu sınırı sessizce siler.** Detay: [`tests/Core.IntegrationTests/Tests/ChainBusy/README.md`](tests/Core.IntegrationTests/Tests/ChainBusy/README.md)

### script-race-lab
Parent `scripts.helpers` bildirir; subflow output mapping'i helper set'inin paylaşılan, singleton
ömürlü `AssemblyLoadContext`'inde derlenir. Parent ve child tamamen otomatik olduğu için N paralel
start, aynı emit penceresinde aynı assembly adının N kez derlenmesi demektir. Fix'siz runtime'da
kaybedenler `FileLoadException` alır ve parent **kalıcı** fault'lanır (`Instance:100030`); fix'li
runtime'da 30/30 `C` beklenir.

### data-integrity-lab
Önemli olan state machine değil **veri**: paralel dallar kendi scope'larından yazar, kayıp veya
mükerrer yazım başarısız transition olarak değil **eksik key** olarak görünür.

### subflow-orchestration
chain-busy'den farkı: zincir **gate'li**. Parent, yeterli sayıda `updateData` gelene kadar
`parent-collect`'te bekler. Bu yüzden "updateData akışı İLERLETİR" iddiasının doğrulandığı yer
burasıdır — kabul edilen updateData veri yazar, state'in auto transition'ları taze veriye karşı
yeniden değerlendirilir.

### fan-out-documents
Önemli olan paralellik değil **yazım sayısı**. `documents-processing` state'inin onEntry'sinde
batch iki sürüm damgası arasında sarılı koşar: `order 1` önce-damgası, `order 2` fan-out batch'i
(N doküman → N paralel HTTP task), `order 3` sonra-damgası. Aralarında hiçbir şey yoktur —
transition yok, state değişimi yok — ve tek-yazım iddiası tam olarak bu iki damga arasındaki patch
farkıdır: **2 olmalı** (biri önce-damgasının kendi yazımı, biri batch'in), `1 + N` değil. onEntry
sırasına bir şey eklemek assertion'ı "geçiyor" bırakır ama hiçbir şeyi denetlemez hâle getirir.

İkinci iddia: fan-out mapping'i **yalnızca** `ItemInputHandler`'ı override eder. `OutputHandler`
vnext `4bd8941b` ile opsiyonel oldu (default interface implementation `null` ⇒ runtime'ın
varsayılan paketlemesi), senaryo da handler'ı **sildi** — böylece `documentResults` /
`documentResultsSummary` üzerindeki tüm assertion'lar runtime'ın kendi çıktısını denetliyor ve
testlerin yeşil olması geri-düşüşün uçtan uca çalıştığının kanıtı oluyor.

Orchestration host'unda sürüm geçmişini listeleyen bir uç olmadığı için (yalnız monitoring host'unda,
4203) akış kendi sürüm işaretlerini instance verisine yazar; `data?version=` sondası bunu bağımsız
olarak doğrular (var olmayan sürüm 404 değil, `200` + `data: null` döner). Aynı sebeple item journal
(`{fanOutTaskKey}#{index}`) assertion'ı integration testte **yoktur** — uydurulmadı, yük testinde
`--monitor-url` ile opt-in. Detay:
[`api-tests/fan-out-documents/README.md`](api-tests/fan-out-documents/README.md)

### fan-out-config-matrix
`fan-out-documents`'ın kardeşi, ama ölçtüğü şey **konfigürasyon**. Matrisin değiştirdiği her şey
(`join.policy`, `join.minSuccess`, `mode`, `execution.*`, item bazlı `errorBoundary`) FanOut **task
bileşeninin** config'inde yaşıyor: bileşen başına statik, çağıranın runtime'da besleyebileceği bir
yol yok. Dolayısıyla config ekseni **zorunlu olarak** varyant başına bir task bileşeni. Toplanan
kısım akış: tek `fan-out-config-matrix` akışı, tek dispatcher state, case başına bir manuel
transition; item karışımı start body'sinden geldiği için o **parametrize**. Sonuç 1 akış + 9 task
bileşeni + 1 test sınıfı — 9 neredeyse-aynı akış yerine.

Join verdict'i **hangi terminal state'e varıldığı** olarak gözlemleniyor: başarılı join → koşulsuz
auto transition → `case-settled`; başarısız join → global `rollback` boundary → `case-failed`.
İkisi de pozitif assert ediliyor.

> **Ölçülmüş tuzak.** İlk revizyon boundary tanımlamıyor ve başarısız join'i **Faulted instance**
> olarak okuyordu. Yanlış: boundary yokken başarısız onEntry task'ı **hiç işleme alınmıyor**, auto
> transition yine ateşleniyor. Kontrol deneyi (`documents`'a array yerine string → resolver fırlatır,
> yani sert `Result.Fail`) yine `case-settled`'a vardı. Beş başarısız-join case'i sessizce
> geçiyordu. Boundary'nin yokluğu "fault" değil "**işleme alınmaz**" demek.

Eşzamanlılık ve timeout iddiaları yalnız hata kodu ve sayı üzerinden; **duvar saati ölçülmüyor**.
`mdop` iddiası eşleştirilmiş bir çift: aynı config, aynı item'lar, tek fark tavan (1 vs 4). Tek
çevresel hassasiyet MockLab straggler'ının 1500ms'i ile 2s batch deadline arasındaki marj; seri kol
bu yüzden sayıları **sınır** olarak assert ediyor. Detay ve triyaj:
[`tests/Core.IntegrationTests/Tests/FanOut/README.md`](tests/Core.IntegrationTests/Tests/FanOut/README.md)
· [`docs/fanout-configurable-surface-findings.md`](docs/fanout-configurable-surface-findings.md)

---

## Çalıştırma

### Integration testler

Konteynerli ortam (varsayılan) — SDK postgres/redis/vault/dapr/orchestrator/execution/mocklab
ayağa kaldırır, db-migrator'ı koşturur ve `core/**` bileşenlerini publish eder:

```bash
dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~ChainBusy"
```

**Lokalde derlenen runtime'a karşı (geliştirme sırasında doğru olan yol).** Henüz release edilmemiş
bir geliştirme image üzerinden test edilemez — image eski kodu taşır.
`tests/Core.IntegrationTests/test.runsettings` içinde `VNEXT_BASE_URL`'i aç:

```xml
<VNEXT_BASE_URL>http://localhost:4201</VNEXT_BASE_URL>
```

Öncesinde vNext çalışma alanında altyapı + 4 app ayakta olmalı (orchestration, execution, inbox,
outbox — hepsi `--launch-profile http` ile), migration varsa db-migrator bir kez koşmalı.

### Python davranış / yük testleri

Senaryonun `api-tests/<senaryo>/` klasöründe dururlar. Çalışan bir runtime'a ihtiyaç duyarlar;
`--publish` bayrağı bileşenleri publish eder.

```bash
python3 api-tests/script-race-lab/race-load.py --publish --parallel 30 --timeout 240
python3 api-tests/data-integrity-lab/integrity-lab-test.py --publish --iterations 6 --threshold 4 --burst 4
python3 api-tests/subflow-orchestration/updatedata-concurrency-test.py --iterations 20 --threshold 8 --burst 6
python3 api-tests/chain-busy/chain-busy-behaviour-test.py --publish --iterations 3
python3 api-tests/chain-busy/chain-busy-behaviour-test.py --list   # case listesi
python3 api-tests/fan-out-documents/fanout-load.py --publish --instances 20 --items 10 --ceiling 64
```

> Kök [`README.md`](README.md)'de ayrıca **JMeter** tabanlı bir yük testi bölümü var. Buradaki Python
> scriptleri onun yerine geçmez; senaryoya özel, **davranış doğrulayan** yük/eşzamanlılık
> testleridir.

---

## Yeni Senaryo Eklerken

1. `core/Workflows/<senaryo>/` altına akışı kur.
2. `tests/Core.IntegrationTests/Tests/<Senaryo>/` altına integration testi yaz. Test sınıfının XML
   özetinde **neyi denetlediğini ve neden var olduğunu** yaz — bilinen kırmızıları ve bilinçli
   kapsam boşluklarını gerekçesiyle birlikte belirt.
3. Yük/eşzamanlılık ölçülecekse Python scriptini `api-tests/<senaryo>/` altına koy; bağımlılıklar,
   parametreli çalıştırma komutu, ölçülen metrik ve **başarısızlık eşiği** dokümante edilsin.
4. Senaryonun kendi `README.md`'sini ekle (ne denetliyor / neden var / akış şeması / nasıl
   çalıştırılır / başarı kriteri).
5. **Bu dosyadaki tabloya aynı commit'te satır ekle.** "Test edilen feature seti" kolonunu vNext'in
   gerçek kavramlarıyla yaz (pipeline step, profil, subflow lifecycle, admission, locking, state
   function, instance data …) — genel ifade yazma.

---

## Bilinen Kapsam Açıkları

| Açık | Etki | Not |
|---|---|---|
| `soap-task-test` için integration test yok | SOAP task davranışı regresyona açık | Yalnız `.http` ile elle doğrulanıyor |
| `role-matrix-lab` henüz koşulmadı; `morph-idm` provider'ı ile hiç denenmedi | Yeni yetkilendirme yüzeyleri uçtan uca doğrulanmış değil | Aether'a `ICurrentUser.Position` eklendikten sonra `CallerRoleProvider:Provider = "morph-idm"` ile koşulmalı — asıl doğrulama odur |
| `future-pay` collateral subflow + parent resume bacağı | Bu leg'in resume davranışı assert edilmiyor | Domain'deki fault izole edildiğinde kapatılmalı |
| `account-opening` konteynerli ortamda kırmızı | Wizard/branch kapsamı fiilen ölçülmüyor | `notify-state` / `set-or-get-cache` onEntry task'ları fault'luyor |
| `data-integrity-lab` `run-parallel` hang | Paralel task veri bütünlüğü ölçülmüyor | 120s'te settle olmadığı doğrulandı |
