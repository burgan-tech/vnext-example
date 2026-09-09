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
| **error-boundary-lab** | Error boundary çözümlemesi: `CompiledBoundaryChain` Task→State→Global seviye baskınlığı · seviye içi sıra (`EffectivePriority` ASC → specificity DESC; default wildcard'ın 999'a düşmesi) · aksiyonlar abort/retry/rollback/notify/ignore/log · `TaskExecutionEngine` retry döngüsü (`1 + maxRetries`) ve tükenince `ResolveExcluding(Retry)` fallback'i · `BoundaryOutcomeHandler` → fault vs `RequestNextTransition` · `FinalizeTransitionStep`'in boundary transition'ı bitince incident'ı resolve etmesi · `InstanceIncidents` tablosu + denormalize `HasActiveIncident` · state function `incident` bloğu (link tabanlı, ResponseShapeVersion v9, ETag materyali) · `GET .../instances/{id}/incidents/active` (404 = açık arıza yok) · `GET .../instances/{id}/incidents` sayfalama · `metadata.incident` (state bloğuyla aynı şekil) · `POST .../retry` (400 `Instance:100027`, yeniden fault, veriyle kurtarma) · `queryRoles` kapısının incident geçmişine de uygulanması | vnext `feature/incident-table` (issue #865) incident'ları jsonb'den kendi tablosuna taşıdı ve iki yeni client yüzeyi ekledi; ayrıca error boundary bu repoda hiç senaryo olarak yoktu ve `POST .../retry` hiçbir testte çağrılmıyordu (2026-09-06) | `Tests/ErrorBoundaryLab` (6 sınıf, 31 test) | — (bilinçli: davranış/çözümleme senaryosu, eşzamanlılık iddiası yok) | ✅ **Aktif — 31/31 yeşil** (lokal runtime `feature/incident-table`, art arda iki koşu, ~1 dk 46 sn; ilk koşu 2026-09-06, üç davranışsal kusurun düzeltilmesinden sonra 2026-09-07'de yeniden) |
| **chain-busy** | Accept-time subflow chain reserve · Busy-as-mutex · `$self` shared transition vs `updateData` lifecycle sınırı · start `initial → initial` semantiği · cancel propagasyonu (in-process ↕ distributed) · scheduled transition re-arm | `updateData`-only self-target profil sınırını pinlemek — `target: $self` "hook'ları atla" demek değil (2026-08-17) | `Tests/ChainBusy` (5 sınıf) | `api-tests/chain-busy/chain-busy-behaviour-test.py`, `chain-busy-accept-test.py` | ✅ Aktif |
| **script-race-lab** | Script engine: paylaşılan `AssemblyLoadContext`'te çift-derleme yarışı · `scripts.helpers` · subflow output mapping · parent kalıcı fault riski | `Script_XXXX already loaded` / `FileLoadException` yarışının fixture'ı; fix'siz runtime'da kaybedenler parent'ı kalıcı fault'lar (2026-08-18) | `Tests/ScriptRaceLab` | `api-tests/script-race-lab/race-load.py` (yük), `publish.py` | ✅ Aktif |
| **data-integrity-lab** | InstanceData v2: anında persist · lock altında kimlik · sıralı/paralel task yazımları · DataHash dedup (task + updateData) · versiyon satırı bütünlüğü | `feature/busy-as-mutex-locking` + InstanceData v2 geliştirmesini uçtan uca ölçmek (2026-08-13) | `Tests/DataIntegrityLab` | `api-tests/data-integrity-lab/integrity-lab-test.py` | ⚠️ Kısmen kırmızı — `run-parallel` konteynerli ortamda settle olmuyor (120s'te doğrulandı, hang) |
| **subflow-orchestration** | 3 seviyeli subflow (parent → child → grandchild) · `updateData`'nın auto transition'ı tetiklemesi · aktif subflow'lu parent'ta `updateData` data-only kısa devresi · parent `$self` shared transition · eşzamanlı updateData tutarlılığı | Platformun subflow referans akışı; `feature/busy-as-mutex-locking` F1/F1a/F8 fix'lerinin doğrulaması (2026-04-28) | `Tests/SubflowOrchestration` | `api-tests/subflow-orchestration/updatedata-concurrency-test.py` | ✅ Aktif |
| **subflow-terminal-relay-lab** | Subflow terminal relay + outbox wakeup sinyali (event publish modes): child tamamlanınca parent'a **post-commit relay** (aynı-domain hedef gecikme ~0) · outbox+Inbox durable yedek + wakeup sinyaliyle hızlandırma · relay gap histogramının (p50/p95/p99/max) client polling DEĞİL, sunucu taraflı `InstanceTransitions` zaman damgalarından (child `FinishedAt` → parent'ın resume sonrası ilk otomatik transition `StartedAt`) hesaplanması · `sys_queues` outbox/inbox satır maliyetinin ölçülmesi | `docs/superpowers/plans/2026-08-29-event-publish-modes.md` planının C2 görevi (vnext repo) — relay gecikmesini client polling yerine sunucu taraflı zaman damgalarıyla, açık eşiklerle (p99 ≤ 250 ms hedef, p95 > 1 s FAIL, stuck > 30 s FAIL) ölçmek (2026-08-30) | — (mevcut `Tests/SubflowOrchestration` akışını yeniden kullanır, yeni entegrasyon test sınıfı eklenmedi) | `api-tests/subflow-orchestration/terminal-relay-load.py` | 🆕 Yazıldı, henüz koşulmadı — çalıştırma runtime altyapısı ayrı hazırlandıktan sonra yapılacak |
| **substate-relay** | Post-commit event relay (registration-based `IPostCommitEventRelay<TEvent>` opt-in, `PostCommitRelayDispatcher`) uygulandıktan sonra `InstanceSubStateChangedEvent`'in fast-path'i · parent `effectiveState`'inin **tüm ata zinciri** boyunca güncellenmesi (depth ≥ 2: `SubflowStateService`'in ikinci relay çağrı noktası) · `currentState` ↔ `effectiveState` ayrımı · instance query `state` alias'ının `EffectiveState`'e çözülmesi (dışarıdan gözlemlenebilir tek tüketici) · sıralama guard'ı: `SubFlowStateChangedAt` monotonluğu, artık terminal yollarla **aynı** per-sub-item kilidi (`vnext:{domain}:{flow}:{parentId}:sub:{subId:N}`) altında · stale teslimat downgrade yapmaz, fresh teslimat uygulanır | Council `2026-09-08-substate-postcommit-relay` (Chair onayı 2026-09-09). Preprod'da (2026-09-08, instance `6aa217b9…`) child state değişikliğinin parent'a ulaşması Dapr Redis pub/sub havuzu tükendiği için **6 dk 30 sn** sürdü. Uygulama kararı iki noktada genişletti: depth ≥ 2 fast-path ve `SubflowStateService`'in kilitsiz olan TOCTOU'sunun kapatılması — ikisinin de regresyon koruması yoktu (2026-09-09) | `Tests/SubflowOrchestration/SubStateRelayTests.cs` (5 test) + `Tests/SubflowOrchestration/README.md` | — (bilinçli: gecikme iddiası APM span'lerinden doğrulanır, tek-örnekli timing assert'i kanıt değil) | ✅ **Aktif — 5/5 yeşil** (lokal runtime `feature/post-commit-event-relay`, 2026-09-09; APM'de `PostCommit.EventRelay` span'leri iç içe, aynı-domain 6.4–14.6 ms) |
| **contract-signing** | SubProcess (fire-and-forget) · `$self` auto loop ile N instance spawn · task ile kurulan çok-akışlı zincir (SubFlow korelasyonu **değil**) · instance data üzerinden zincir takibi · start mapping guard'sızlığında fault | Korelasyon yerine **task ile** kurulan akış zincirinin davranışını kapsamak <sup>1</sup> (2026-08-13) | `Tests/ContractSigning` | — | ✅ Aktif |
| **future-pay** | SubFlow state'leri (auto-complete + açık korelasyon) · parent resume · şema doğrulama (transition + master schema) · transition erişilebilirlik sırası | Kredi kullandırım senaryosu üzerinden çok-akışlı SubFlow davranışı (2026-06-12) | `Tests/FuturePay` | — | ⚠️ Bilinçli kapsam boşluğu — `sign-contract` sonrası collateral subflow + parent resume bacağı domain'de fault'landığı için assert edilmiyor |
| **money-transfer** | Rule-driven branching (auto transition) · scheduled/timeout timer arm · transition schema reddi · terminal HTTP task sonucunun instance data'ya yazılması | Tek akışlık referans süreç: kural, zamanlayıcı ve HTTP task kombinasyonu (2026-06-12) | `Tests/MoneyTransfer` | — | ✅ Aktif |
| **account-opening** | Wizard state tipi · çok dallı ürün seçimi · auto gate'lerin geri gönderimi · rol bazlı state function erişimi (`403`) · cancel/exit well-known transition'ları | Template ile gelen ilk referans akış (2025-11-21) | `Tests/AccountOpening` | — | ⚠️ Bilinçli kırmızı — konteynerli ortamda start, `account-type-selection` onEntry task'larında (`notify-state`, `set-or-get-cache`) fault'luyor; testler doğru, boşluğun sinyali olarak kırmızı tutuluyor |
| **soap-task-test** | `SoapTask` tipi · SOAP mapping (`SendVipSmsMapping.csx`) · başarı/hata rule'ları ile dallanma | SOAP task tipini uçtan uca doğrulamak <sup>1</sup> (2026-08-13) | — (yalnız `.http`) | — | ⚠️ Integration test yok — kapsam açığı |
| **l1-cache-lab** | Component cache versiyon çözümü: `latest` + artifact/major range (`"1"`) referansları · generation-anahtarlı L1 (in-process) cache'in publish görünürlüğü · pinned instance `flowVersion` kararlılığı · publish-only aktivasyon (re-initialize'sız) · view/task referanslarının sıcak cache'de anında yeni versiyona dönmesi | Runtime'a eklenen L1 component cache'in (vnext `feature/component-cache-l1`, 2026-08-20) "versiyon cache'de kaldı" riskini uçtan uca çürütmek; CD sözleşmesinin (publish bitince yeni sürüm MUTLAKA geçerli) regresyon bekçisi | — (bilinçli: publish-akışı doğruluk senaryosu; api-test yeterli) | `api-tests/l1-cache-lab/l1-cache-behaviour-test.py` (`--minor N` ile tekrar koşulabilir) | ✅ Aktif — 18/18 (2026-08-20, lokal L1 runtime) |
| **role-matrix-lab** | Yetkilendirme yüzeylerinin tutarlılığı: root vs state `queryRoles` (state EZER) · `transition.roles` allowlist / blacklist / predefined (`$InstanceStarter`) · `availableIn` rol daraltması (**AND**) · well-known transition'ların (`cancel`/`updateData`/`exit`) configured key + `kind` ile listelenmesi · master şemada `x-roles` alan budaması · `authorize` function'ının üç hedefi (transitionKey / functionKey / queryRoles) · custom function'da rol denetiminin **kaldırılmış** olması | Provider bazlı caller-role çözümü (`default` \| `morph-idm`) + custom function rol gate'inin kaldırılması; rol setinin KAYNAĞI değişirken grant motorunun davranışının değişmediğini pinlemek (2026-08-19, `feature/caller-role-provider`) | `Tests/RoleMatrixLab` (5 sınıf, 59 test) | — (bilinçli: doğruluk senaryosu, eşzamanlılık değil) | 🆕 Yazıldı, henüz koşulmadı |
| **secret-cache-lab** | `ScriptBase.GetSecretAsync` üzerinden in-process secret bundle cache (`ScriptSecretCache`) · bundle başına tek Vault fetch (single-flight) · cache'in request'ler arası (process-wide) yaşaması · TTL süresince bilinçli staleness · TTL dolunca canlı değere tazelenme · script task (type 7) içinden secret erişimi | Script secret fonksiyonlarının her çağrıda vault'a gitmesi yük altında vault'u darboğaza sokuyordu; `Scripting:SecretCache` (TTL 30 sn) geliştirmesinin hem kazancını hem de bayatlık sınırını uçtan uca pinlemek (vnext `claude/scriptbase-secret-cache-y86e03`, 2026-08-20) | — (bilinçli: doğrulama Vault audit log'u + saat ölçümüne dayanıyor, SDK assertion yüzeyinde yok) | `api-tests/secret-cache-lab/secret-cache-behaviour-test.py` | ✅ Aktif — 12/12 (2026-08-20, lokal runtime, TTL 30 sn) |
| **fan-out-documents** | `FanOutTask` (TaskType 21) inline mode · `itemsPath` ile koleksiyon çözümü + `ItemKey` türetimi (`id` → `key` → index) · `IFanOutMapping.ItemInputHandler` ile klonlanmış iç task'ın per-item mutasyonu (HTTP url) · `allSettled` join politikası ve `{resultKey}Summary{total,succeeded,failed,timedOut}` üzerinden auto transition dallanması (`RunAutomaticTransitionsStep`, order 90) · **tek-yazim degismezi**: N item → 1 InstanceData sürümü (`SuppressDataApply` + atılan branch context, tek yazım noktası batch'in çıktı adımı) · **`IFanOutMapping.OutputHandler` geri-düşüşü**: mapping yalnız `ItemInputHandler`'ı override eder, çıktıyı runtime'ın `BuildDefaultOutput`'u üretir · iki seviyeli bulkhead (batch-yerel `maxDegreeOfParallelism` × süreç geneli `Workflow:FanOut:MaxConcurrentItems`) · item journal anahtarları `{fanOutTaskKey}#{index}` · sonuçların `join.ordered`'dan bağımsız olarak her zaman index sıralı dönmesi | Runtime'a eklenen FanOutTask'ın (vnext `feature/fanout-task-design`, 2026-08-21) **tek-yazim degismezini** regresyona karşı sabitlemek: bastırma bozulursa fan-out tek bir aggregate üzerinde yarışan N eş zamanlı yazıcıya döner ve tasarımın var oluş sebebi kaybolur. İkincil olarak `allSettled` + özet + auto transition kısmi-başarısızlık kalıbının çalışır bir örneğini pinler (2026-08-21) | `Tests/FanOut` (`FanOutDocumentsTests`, 4 test) | `api-tests/fan-out-documents/fanout-load.py` (bulkhead tavanı + yük altında tek-yazım + straggler oranı) | ✅ 4/4 yeşil (2026-08-22, `ad72158b`) + yük testi PASS: kuyruksuz profil 6/6 (`--instances 4 --items 3 --max-dop 3`), doygun varsayılan profil 5/5 (BULKHEAD bilinçli SKIP — aşağıya bakın). `npm run validate` TaskType 21'i hâlâ reddediyor (`@burgan-tech/vnext-schema@0.0.52` enum'u `"20"`de bitiyor; şema paketi release bekliyor) — publish yolu validate'ten geçmediği için engel değil: SDK `LocalDomainPublisher` component JSON'ını doğrudan `POST /api/v1/definitions/publish`'e atıyor. Item journal assertion'ı **bilinçli olarak yok**: satırlar yalnız monitoring host'unda (4203) görünüyor, SDK stack'i onu başlatmıyor — `fanout-load.py --monitor-url` ile opt-in. **2026-08-22 düzeltmesi:** `DOC-SLOW` straggler route'u `api/fan-out/documents/process-slow` adresindeydi ve MockLab route'ları **PREFIX** ile eşlediği için `documents/process` mock'u tarafından yutuluyordu — yani gecikme hiç uygulanmıyordu ve `fanout-load.py`'nin **straggler oranı metriği jitter ölçüyordu**. Route `api/fan-out/slow-documents/process`'e taşındı, mapping güncellendi, akış 1.0.2'ye bump edildi (integration testler 4/4 yeşil kaldı). Yük testi bundan sonra ilk kez anlamlı sayı üretti ve **iki metrik hatası ortaya çıktı, ikisi de düzeltildi**: (1) `BULKHEAD` metriği doygunlukta **geçersiz** — `sum(durationMs)/wall` "uçuşta geçen süre" varsayıyor ama `FanOutTaskExecutor` item stopwatch'ini slot beklemelerinden **önce** başlattığı için `durationMs` kuyruk süresini de içeriyor (runtime bu yüzden span'e ayrıca `vnext.fanout.item.queue_wait_ms` basıyor); iddia artık yalnızca kuyruksuz profilde kuruluyor (`items <= max-dop` **ve** `instances*items <= ceiling`), aksi halde sebebiyle SKIP. (2) `STRAGGLER` eşiği (`<= 4.0`) yutulmuş route'a kalibreliydi; gerçek straggler ile oran **tasarım gereği ~10**. Eşik 15.0'a çıkarıldı ve **iki taraflı** yapıldı: yeni `STRAGGLER-VAR` tabanı **mutlak** (`en yavaş item >= 1200ms`), çünkü `max/p50` oranı presence detektörü olarak gürültülü — hiç `DOC-SLOW` yokken bile 9.44 üretti |
| **fan-out-config-matrix** | `FanOutTask` (TaskType 21) **konfigüre edilebilir yüzeyi**: dört `join.policy` (`all` / `allSettled` / `quorum` / `firstSuccess`) verdict'lerinin **iki** yanında da · `join.minSuccess` tutan / tutmayan · `FanOutJoinEvaluator` **boş-batch** kuralı (`all`+`allSettled` vacuously başarılı, `quorum`+`firstSuccess` eşiği geçemediği için başarısız) · `mode: "durable"` reddi (`FanOutTask.Configure`, rezerve) · **item bazlı `errorBoundary`** (`ignore` verdict'i çevirir: `join: all` altında başarısız item batch'i düşürmez; `retry` tükenmesi kendi item'ında kalır) · `execution.maxDegreeOfParallelism`'in gerçek eşzamanlılığı sıkıştırması (eşleştirilmiş kontrol kolu, tek fark tavan) · `itemTimeoutSeconds` ↔ `batchTimeoutSeconds` ayrımı: `FanOut:ItemTimeout` vs `FanOut:BatchTimeout` + `summary.timedOut`'un yalnız **batch** deadline'ında yükselmesi · başarısız join'in task'ı düşürüp instance'ı Faulted etmesi (akışta bilinçli olarak **hiç** errorBoundary yok) | FanOutTask'ın config yüzeyi uçtan uca **hiç** doğrulanmamıştı — unit testler geçiyor ve bir production domain'i yalnız mutlu yolu kullanıyordu; `join.policy` değişince, eşik tutmayınca, koleksiyon boş gelince, item boundary devredeyken ya da tavan gerçekten sıkıştırınca runtime'ın ne yaptığını hiçbir integration test görmüyordu (vnext `feature/fanout-task-design`, 2026-08-21) | `Tests/FanOut` (`FanOutConfigMatrixTests`, 16 test) | — (bilinçli: doğruluk/konfigürasyon senaryosu; eşzamanlılık iddiaları hata kodu + sayı üzerinden, duvar saati yok) | ✅ **Aktif — 18/18 yeşil** (2026-08-22, lokal runtime `ad72158b`, iki koşu üst üste; `--filter FanOut` bütünü 22/22). Dört join politikası verdict'lerinin iki yanında da, `minSuccess`, boş-batch kuralı, `itemTimeoutSeconds`, `batchTimeoutSeconds`, eşit-deadline sınırı, `maxDegreeOfParallelism`'in gerçek eşzamanlılığı sıkıştırması, item bazlı `retry` kapsaması ve `mode: durable` reddi **uçtan uca doğrulandı**. Bulunan ve **runtime'da düzeltilen** iki defect: **F1** (`b80be176`) uçuşta iptal edilen item kendi fan-out nedeni yerine `Task:Unknown:<taskKey>:TaskCanceledException` taşıyordu — üç iptal nedeninde de (item deadline 0/1→**1/1**, batch deadline 1/2→**2/2**, early-stop 1/4→**4/4**); **F2** (`ad72158b`) `Configure`-time authoring hatası opak `500` yerine artık alanı adlandıran `400` (paylaşılan `ComponentValidatorProcessor`'da olduğu için tüm task tiplerini kapsıyor). Kendi filed ettiğim **"timedOut yükselticisi" iddiası ölçülüp GERİ ÇEKİLDİ** — `itemTO <= batchTO` zorunlu + `Classify` önce item deadline'ına baktığı için o şekil yapısal olarak imkânsız. Üç **fixture/tasarım** düzeltmesi: boundary yokken başarısız onEntry task'ı fault'lamıyor (fault temelli gözlem beş case'i sessizce geçiriyordu → global `rollback` + `case-failed`); **MockLab PREFIX eşlemesi** yavaş mock'u yutuyordu (`api/fan-out/slow-documents/process`'e taşındı); yük testinde **BULKHEAD metriği doygunlukta geçersiz** (`durationMs` kuyruk süresini içeriyor) ve **straggler eşiği bozuk fixture'a kalibreliydi**. Açık: **F3** item bazlı `ignore` semantiği, **C1** `minSuccess` non-quorum'da sessiz yoksayma (ikisi de karar bekliyor, test kırmızısı değil). Kanıt: [`docs/fanout-configurable-surface-findings.md`](docs/fanout-configurable-surface-findings.md). `npm run validate` 10 fan-out task bileşenini de reddediyor — `fan-out-documents` ile aynı bilinen şema açığı (enum `"20"`de bitiyor); publish şema validasyonunu baypas ettiği için engel değil |
| **payload-modes** | Request sözleşmesi ↔ şema doğrulaması: payload-mode tespiti (`PayloadModeDetector` + `FormUrlEncodedJsonElementInputFormatter`) · standart zarf (`key`/`tags`/`stage`/`attributes`) ↔ serbest payload ayrımı · `startTransition.schema` **ve** `transition.schema` yollarının ikisi birden · zarf alanlarının iş verisine sızmaması (şemasız transition'da sessiz veri kirlenmesi) · `x-vnext-payload-mode: raw` override'ı · `attributes` eşleşmesinin case-insensitive olması | Şema tanımlı bir transition/start'ta payload'ın **hangi biçimde** gönderildiği validasyon sonucunu değiştiriyordu: mod tespiti tek bir case-sensitive `attributes` property'sine bakıyordu, oysa zarfın her alanı opsiyoneldir — `attributes` içermeyen geçerli bir zarf serbest payload sanılıp **tümüyle** `attributes` altına sarılıyor ve şema iş payload'ı yerine `key`/`tags` alanlarını doğruluyordu (`additionalProperties: false` şemalarda *"All values fail against the false schema"* 400'ü). Şemasız transition'da aynı hata sessizdi: zarf instance data'ya yazılıyordu (2026-08-22, vnext `PayloadEnvelope` ortak zarf sözlüğü) | `Tests/PayloadModes` (2 sınıf, 24 test) | — (bilinçli: request sözleşmesi doğruluk senaryosu, eşzamanlılık iddiası yok) | ✅ **Aktif — 24/24 yeşil** (2026-08-22, lokal runtime). Düzeltme öncesi runtime'a karşı **tam 4 test kırmızı** (start: envelope-only + PascalCase `Attributes`; transition: envelope-only + şemasız transition'da `key`'in instance data'ya yazılması) — regresyon iğnesi doğrulandı. Kullanıcının bildirdiği üç kanonik biçim (`{key,attributes}`, `{attributes}`, serbest gövde) düzeltme öncesinde de geçiyordu; testler "üçü de aynı sonucu üretir" sözleşmesini sabitler. Akış **hiç task içermez** — MockLab/execution host/worker bağımlılığı yok. **Aynı geliştirme altında ikinci bir defect düzeltildi:** kök düzeyindeki `required` hatası hiyerarşik ağaç düzleştirilirken düşüyordu (`JsonSchemaValidationMapper.FlattenErrors` bir düğümün *kendi* hatalarını, çocukları varsa atıyordu — `additionalProperties:false` + iç içe obje olan her şemada kök hatası TEK hataydı), istemci `"errors":{}` ile **hangi alanın hatalı olduğunu öğrenemiyordu**; boş hata listesi ayrıca yanıtı RFC7807 ProblemDetails'e düşürerek iki farklı gövde biçimi yaratıyordu. Artık tek biçim + alan düzeyinde `members`/`message` |
| **script-perf-lab** | Script compile cache hit yolu (`CSharpEvaluator._typeCache`) · `scripts.helpers` çok üyeli helper set (A7) · instance-data append zinciri (`JsonData.Merge`/`NormalizedJson`, B9 O(n²) profili) · `FanOutTask` inline branch klonu (`CreateParallelBranch`, B6) · Katman 0 metrikleri (`script_compilations_total{result}`, `script_execution_duration_seconds{script_type}`) | Katman 0 ölçüm altyapısının makro baseline'ı — Katman 1-3 compiler/serialization optimizasyonlarının gerçek-yük önce/sonra referansı (2026-08-23, vnext `feature/script-perf-katman0`) | `Tests/ScriptPerfLab` (1 test) | `api-tests/script-perf-lab/perf-load.py` (soğuk/sıcak faz + p50/p95/p99 + /metrics snapshot) | ✅ Aktif — K1+K2 önce/sonra kayıtlı; COW+canonicalizer 37/37 integration + kill-switch canlı testli (2026-08-23) |
| **schedule-after-auto** | Pipeline epilogue sıralaması (`LifecycleOrder.Auto` 80 → `LifecycleOrder.Schedule` 90) · `ScheduleTransitionsStep`'in `Directives.NextTransition` guard'ı (auto kazanan varsa **hiç** arm etmez: Dapr job yok, `InstanceJob` satırı yok) · state function'ın `kind: "scheduled"` girdileri + `executeAtUtc` · auto kazanan yokken scheduled transition'ın eskisi gibi arm edilip **gerçekten ateşlenmesi** · `CancelScheduledJobsStep` (39) churn'ünün ortadan kalkması | Eski sıralamada Schedule (80) timer'ı arm ediyor, Auto (90) kazanan seçiyor, zincirlenen hop da o timer'ı hemen siliyordu — auto'nun kazandığı her hop'ta boşuna enqueue + persist + cancel. Sıralama takas edildi (vnext `feature/schedule-after-auto`, plan `docs/superpowers/plans/2026-09-02-schedule-after-auto.md`, 2026-09-03). Senaryo hem yeni davranışı hem de "auto kazanmazsa hiçbir şey değişmedi" tarafını pinler; iki sıralama **dinlenme durumunda ayırt edilemediği** için auto hop'unun `onExecute`'u bilinçli ~2.5 sn gecikir ve test o pencerede armed girdinin **hiç** oluşmadığını gözler | `Tests/ScheduleAfterAuto` (`ScheduleAfterAutoTests`, 2 test) | — (bilinçli: sıralama/doğruluk senaryosu; eşzamanlılık iddiası yok) | ✅ **Aktif — 2/2 yeşil** (2026-09-03, lokal runtime `702a03b6`, iki koşu üst üste, ~24 sn) |
| **cross-domain-lab** | Cross-domain transport: `ServiceDiscovery:Provider=dapr` (`DaprDomainDiscoveryProvider`, registry `appId` override → `vnext-app-partner`) · Dapr service invocation shell (`DaprRemoteTransport`) · cross-domain **SubFlow** start / `internal/subflow-forward` / parent resume (`ResumePipelineAsync`) · trigger task'ları **11 Start · 12 DirectTrigger · 13 GetInstanceData · 14 SubProcess · 15 GetInstances (`SetFilterSpec`) · 19 GetInstance** (`useDapr:true`, `config.domain:"partner"`) · fonksiyon descent'i `state` / `view` / `schema` / `authorize` (partner rol filtresi) / `data?extensions=` (`RemoteInstanceQueryAppService`) · `data` gövdesinin parent'ta kalması (pinlenmiş runtime kararı) | Cross-domain adres çözümlemesi Discovery HTTP'sinden Dapr Name Resolution'a taşındı (vnext `feature/dapr-name-resolution`, 2026-09-03); repoda hiç cross-domain örnek yoktu. İkinci domain (`partner/`, `vnext.partner.config.json`) ve üç-domain lokal lab (`labs/cross-domain/`) bu senaryoyla geldi. Plan: `labs/cross-domain/VNEXT-BUILD-PLAN.md` | `Tests/CrossDomainLab` (`SubflowDescentTests` 6, `TriggerTaskTests` 5) — `VNEXT_PARTNER_BASE_URL` yoksa **skip** | — (yük testi sonraki faz) | ✅ **Aktif — 11/11 yeşil** (2026-09-03, lab: üç domain de lokal `dapr-nr` imajları + Dapr 1.18.0, ~1.7 dk). Rollback tatbikatı `VNEXT_LAB_DISCOVERY_PROVIDER=http` ile de 11/11 (2026-09-04; `Remote*` düz HTTP `vnext-app-partner:5000`, `useDapr` task'ları Dapr'da). **Discovery endpoint cache doğrulaması (2026-09-09, `http` provider, 11/11 yeşil):** core tek bulk okumayla 3 domain'i cache'e yazdı (`50002`), partner marker'ı görüp **hiç** bulk okuma yapmadı ama 13 çözümlemesinin 13'ünü paylaşılan cache'ten aldı — kümede pencere başına tek okuma. Elastic APM `Discovery.Resolve/*`: 79 span, cache 78 / registry 1; L2'den bir kayıt silinip L1 süresi beklendiğinde aynı domain **registry 100.7 ms → cache 0.07-0.6 ms**. `POST utilities/discovery/refresh` açık pencerenin içinde senkron yeniden okudu (`outcome: Refreshed`). Not: lab template'inin `AdditionalSources`'ında `BBT.Workflow.Pipeline` yok — eklenmezse `Discovery.Resolve` span'i sessizce düşer; template ayrıca yalnız OpenObserve'e export eder, Elastic için collector'a `otlp/elastic` + elasticsearch/apm-server gerekir. Bilinen: vnext-schema 0.0.52 `useDapr`'ı yalnız task 15/19'da tanır → `core/Tasks/cross-domain-lab/` 11/12/13/14 dosyaları `npm run validate`'te "then schema" hatası verir (runtime alanı okur, alan bilinçli korunuyor); `partner/` validate kapsamı dışında |

<sup>1</sup> Gerekçe git geçmişinde kayıtlı değil (commit mesajı `updated`); senaryonun kendi
içeriğinden çıkarıldı. Doğrusunu bilen varsa bu satırı düzeltsin.

---

## Senaryo Detayları

Her senaryonun ayrıntısı kendi README'sinde / test sınıfının XML özetinde durur. Öne çıkanlar:

### error-boundary-lab

İki workflow (`error-boundary-lab`, `error-boundary-lab-global`) ve bir hub state üzerinden her
boundary vakası ayrı transition. İkiye bölünmesinin sebebi yapısal: workflow seviyesi bir boundary
**her** task için `HasAnyBoundary`'yi true yapar, dolayısıyla "hiçbir yerde boundary yok" kontrolü
global boundary ile aynı akışta yaşayamaz. State seviyesi vakaları "zone" state'lerin `onEntries`'ine
konur — runtime'ın baktığı state boundary'si `instance.CurrentState` üzerindedir ve OnExecute (30)
ChangeState'ten (50) önce, OnEntry (60) sonra koşar.

Hata enjeksiyonu iki kaynaktan: fırlatan script task (dış bağımlılık yok) ve MockLab
(`api/eb-lab/fail-500`, `fail-503`, `flaky` = 500,500,200). MockLab kapalıyken yalnız iki retry testi
skip olur.

**Bu senaryonun bulup düzelttiği altı kusur** (hepsi vnext tarafında, `feature/incident-table`):

1. `MoveInstanceIncidentsToTable` migration'ının iç foreign key'i `principalSchema: "public"` idi.
   `MultiSchemaNpgsqlMigrationsSqlGenerator`, `CreateTableOperation`'ın **iç** FK'lerini yeniden
   yazmadığı için her flow şemasının FK'si `public."Instances"`'ı gösterdi ve backfill 13 şemada
   `23503` ile düştü. Düzeltme: `principalSchema: null` (repo'nun `Initial`'dan beri konvansiyonu).
2. `LoadActiveIncidentsAsync`'in no-tracking dalı, satırları **başka bir DbContext'in izlediği**
   aggregate'in EF navigation'ına ekliyordu; o context commit ederken onları yeni çocuk sanıp tekrar
   INSERT ediyor, retry isteği `PK_InstanceIncidents` ihlaliyle patlıyor ve yanıt gövdesi yarıda
   kesiliyordu. Düzeltme: yüklenen satırlar aggregate'in **detached** listesinde tutuluyor, detached
   bir incident'ın resolve'u `IInstanceIncidentRepository.ResolveAllAsync` ile açıkça yazılıyor.
3. Blok incident içeriğini gömüyordu: state function en sıcak okuma yolunda incident tablosunu
   okuyor, geçmiş endpoint'inin verisini kopyalıyor ve bir bayatlık deliği taşıyordu (bir state'te A
   resolve edilip B açılınca hiçbir fingerprint üyesi kımıldamadığı için client 304'te kalıp A'yı
   göstermeye devam ediyordu). Düzeltme: blok artık yalnız bayrak + iki link taşıyor
   (`ResponseShapeVersion` v9), `metadata.incident` de aynı şekle geçti; state function ve instance
   GET'i hiç incident sorgusu atmıyor, liste görünümündeki batch sorgu kalktı (2026-09-07).
4. Bir `abort` **iki** incident yazıyordu: boundary'nin verdict'i ve pipeline'ın fault için yazdığı
   `Pipeline` katmanlı ikinci satır. Task step'i incident'ı save'den **sonra** eklediği için
   `MarkInstanceFaultedAsync`'in taze UoW'daki yeniden yüklemesi `HasActiveIncident`'ı hâlâ false
   görüyor ve fallback satırını yazıyordu. Sonuç: faulted instance'ta `incident.active` boundary
   verdict'i taşımıyordu. Düzeltme: üç task step'i incident'ı **save'den önce** kaydediyor
   (2026-09-07).
5. Yeniden fault eden retry `Active` olarak yerleşiyordu. Retry isteği aggregate'i ambient request
   scope'unda **tracked** yükleyip `Unfault()` uyguluyor, fault ise `RequiresNew` bir scope'ta
   koşuyordu; istek sonundaki ambient commit kendi bayat Active'ini F'in üzerine yazıyor ve instance
   bir daha retry edilemiyordu (`Instance:100027`). Düzeltme: no-tracking yükleme +
   `IInstanceRepository.TryUnfaultAsync` CAS'ı (2026-09-07).
6. Kurtulan instance hâlâ aktif incident bildiriyordu. `Unfault()` yalnız en yeni incident'ı
   resolve ediyordu; `HasActiveIncident` fingerprint materyali olduğu için bayat sinyal long-poll
   eden client'a da yansıyordu. Düzeltme: `Instance.ResolveOpenIncidents()` açık kümenin tamamını
   kapatır, detached retry yolunda karşılığı `ResolveAllAsync` (2026-09-07).

Hâlâ **ölçülüp pinlenen** iki davranış (`retryCount` her zaman 0; `ignore`/`log` incident yazmaz ve
hook'un kalanını atlar — ikincisi doğru davranış olarak onaylandı) ve "değişirse ne yapılmalı"
notları `tests/Core.IntegrationTests/Tests/ErrorBoundaryLab/README.md` içinde.

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

### substate-relay
`subflow-orchestration` ile aynı akışları kullanır ama iddiası farklı: burada state machine değil
**parent'ın `effectiveState`'i** ölçülür. `currentState` parent'ın kendi state'idir ve
`parent-subflow-state`'ten hiç çıkmaz; `effectiveState` ise zincirin en derin aktif halkasının
state'ini taşır. Kritik test iki hoplu olan: torunun state'i önce çocuğa, çocuk da kendisi bir
subflow olduğu için oradan parent'a yayılmak zorunda — `TransitionRunner`'daki relay yalnız kendi
hop'unun event'lerini gördüğü için bu ikinci hop `SubflowStateService`'in kendi relay çağrısıyla
olur.

Gecikme burada **assert edilmez**: outbox yolu da eninde sonunda aynı doğru değeri yazar, sadece
daha yavaş. Fast-path'in gerçekten koştuğunun kanıtı APM'dedir — tek trace içinde iç içe geçmiş
`PostCommit.EventRelay` → `SubFlow.StateChange` → `PostCommit.EventRelay` → `SubFlow.StateChange`
span'leri (`Tests/SubflowOrchestration/README.md`'de sorgusu ve ölçülen değerleri var).

İki stale/fresh testi doğrudan sıralama guard'ını sürer: `changedAt`'i kontrol edebilmek için
internal `sub/state` uç noktasına elle POST atarlar. Guard'ın davranışı değişirse sessiz bir
downgrade'e kapı açılır, bu yüzden bu ikisinin kırmızıya dönmesi merge öncesi incelenmelidir.

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

Eşzamanlılık ve timeout iddiaları yalnız hata kodu ve sayı üzerinden; **duvar saati ölçülmüyor**
(tek istisna: fixture'ın önkoşulunu — mock'un gerçekten yavaş olduğunu — doğrulayan guard). `mdop`
iddiası eşleştirilmiş bir çift: aynı config, aynı item'lar, tek fark tavan (1 vs 4); ikisi de
`batchTimeoutSeconds 3` ile koşuyor, çünkü 2s'de kırılgan olan **kontrol kolunun kendisiydi**.

> **Fixture tuzağı (2026-08-22).** MockLab route'ları **PREFIX** ile eşliyor: yavaş mock
> `documents/process-slow`'da dururken `documents/process` mock'u onu yutuyordu (anlamsız bir
> `documents/process-XYZQQ` path'i bile hızlı mock'un body'sini döndürüyordu). Gecikme "uygulanmıyor"
> değildi — yavaş mock hiç cevap veren taraf olmuyordu. Bu haldeyken `mdop` kontrol kolu **boş yere
> geçiyordu**. Route `api/fan-out/slow-documents/process`'e taşındı; guard artık hem body imzasını hem
> süreyi kontrol ediyor.

Detay ve triyaj:
[`tests/Core.IntegrationTests/Tests/FanOut/README.md`](tests/Core.IntegrationTests/Tests/FanOut/README.md)
· [`docs/fanout-configurable-surface-findings.md`](docs/fanout-configurable-surface-findings.md)

### payload-modes
Test edilen şey akış değil, **request sözleşmesi**. Bir istemci aynı iş payload'ını üç biçimde
gönderebilir — zarf + metadata (`{key, attributes}`), yalnız zarf (`{attributes}`), ya da serbest
gövde — ve üçü de aynı şemaya karşı doğrulanıp instance'a aynı veriyi yazmalıdır. Akış bu yüzden
kasıtlı olarak **hiç task içermez**: ölçülen tek şey payload'ın nereye çözüldüğü.

Kritik nokta, şemadaki `additionalProperties: false`. Zarf alanları (`key`/`tags`/`stage`) iş
verisi değildir; mod tespiti şaşınca zarf `attributes` altına sarılır ve şema iş payload'ı yerine
zarfı doğrular. `additionalProperties` açık olmasa bu hata **sessizce** geçerdi — nitekim şemasız
bir transition'da tam olarak öyle oluyor ve `key` instance data'ya iş verisiymiş gibi yazılıyor;
senaryonun dördüncü kırmızı testi bunu yakalar.

Detay: [`tests/Core.IntegrationTests/Tests/PayloadModes/README.md`](tests/Core.IntegrationTests/Tests/PayloadModes/README.md)

### schedule-after-auto
Tek state (`gate`) hem koşullu bir automatic transition hem de 8 saniyelik bir scheduled transition
taşır; hangisinin kazandığını **epilogue sıralaması** belirler. Auto koşulu sağlandığında
`ScheduleTransitionsStep` hiçbir şey arm etmez (state function'da `kind: "scheduled"` girdisi
oluşmaz); sağlanmadığında timer arm edilir, `executeAtUtc` ile listelenir ve ateşlenir.

Senaryonun kritik detayı: eski (Schedule → Auto) sıralama timer'ı arm edip zincirlenen hop'ta
`CancelScheduledJobsStep` ile hemen siliyordu, yani **iki sıralama dinlenme durumunda ayırt
edilemez**. Bu yüzden auto hop'unun `onExecute`'u bilinçli olarak ~2.5 sn gecikir ve test o pencere
boyunca poll ederek "armed girdi hiçbir an görülmedi" iddiasını kurar. Delay kaldırılırsa yol 1
sessizce totolojiye düşer.

Detay: [`tests/Core.IntegrationTests/Tests/ScheduleAfterAuto/README.md`](tests/Core.IntegrationTests/Tests/ScheduleAfterAuto/README.md)

---

### cross-domain-lab
`core/xd-parent` bir zincir yürütür: `xd-subflow` (stateType 4, `process.domain: partner`) partner'da
`xd-child`'ı başlatır; child `child-approve` (şema + `xd-approver` rolü) ile parent ÜZERİNDEN
tamamlanır ve parent resume eder. Sonraki her manuel transition tek bir cross-domain task tipi taşır
(14 → `xd-worker`, 11 → `xd-remote`, 12 `remote-advance`, 19+13 okuma, 15 `attributes.testId`
filtresi), böylece kırmızı bir test tek bir task tipine işaret eder. Descent testleri parent'a
sorup partner içeriğini bekler: `view` → `xd-child-review-view`, `schema` → `xd-child-approve`,
`authorize` → child'ın rol kararı (200/403), `data?extensions=xd-child-ext` → partner extension'ı,
gövde parent verisi (bilinçli pinlenmiş; runtime bunu değiştirirse test kırılır).

Kritik detaylar: parent aktif subflow boyunca **yapısal olarak Busy** → testler gözlenen (leaf)
state'i bekler; `child-approve` rol kısıtlı olduğu için state fonksiyonu **rolle** okunur (rolsüz
okuma yalnız cancel girdilerini gösterir — bu bir hata değil, rol filtresidir). Partner bileşenleri
harici-stack modunda SDK hook'u çağrılmadığı için `CrossDomainLabFixture`'da yayınlanır. Lab tarafında
`nameformat` çözücüsü daprd 1.16.x'te yok; `appconfig` açıkça `mdns` pinler.

Detay: [`tests/Core.IntegrationTests/Tests/CrossDomainLab/README.md`](tests/Core.IntegrationTests/Tests/CrossDomainLab/README.md)
· lab: [`labs/cross-domain/README.md`](labs/cross-domain/README.md)

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
`tests/Core.IntegrationTests/test.runsettings` içinde `VNEXT_BASE_URL` **set olmalı** — repoda
halihazırda `http://localhost:4201` olarak commit'lidir, yorumdan çıkarılacak bir şey yoktur; sadece
doğrula. Farklı bir port/offset için commit'li dosyayı düzenleme: yanına git-ignore'lu
`test.runsettings.local` koy (csproj ve SDK onu tercih eder; öncelik: gerçek ortam değişkeni >
`test.runsettings.local` > `test.runsettings`). Set değilse SDK image'lı Testcontainers stack'ini kaldırır —
geliştirme testinde istenmeyen şey budur.

```xml
<VNEXT_BASE_URL>http://localhost:4201</VNEXT_BASE_URL>
```

Öncesinde vNext çalışma alanında altyapı + 4 app ayakta olmalı (orchestration, execution, inbox,
outbox — hepsi `--launch-profile http` ile), migration varsa db-migrator bir kez koşmalı.

### Python davranış / yük testleri

Senaryonun `api-tests/<senaryo>/` klasöründe dururlar. Çalışan bir runtime'a ihtiyaç duyarlar;
`--publish` bayrağı bileşenleri publish eder. Hepsi `--base-url` alır (varsayılan: `VNEXT_BASE_URL`
ortam değişkeni, o da yoksa `http://localhost:4201`) — farklı bir domain/offset'e koşturmak için
`VNEXT_BASE_URL=http://localhost:4211 python3 ...` ya da `--base-url http://localhost:4211`.

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
| Abort iki incident yazıyor | Faulted instance'ta `incident.active` pipeline satırıdır ve `boundaryAction` taşımaz; boundary verdict'i bir önceki satırdadır | `MarkInstanceFaultedAsync`'teki `!HasActiveIncident` guard'ı taze bir UoW'da yeniden yüklüyor, boundary'nin incident'ı orada görünmüyor. `Tests/ErrorBoundaryLab` pinliyor |
| `incident.retryCount` her zaman 0 | Retry sayısı client'a hiç ulaşmıyor | Engine, retry policy'yi boundary aksiyon sonucuna iliştirmiyor; deneme sayısı yalnız instance verisinden okunabiliyor |
| `ignore`/`log` incident yazmıyor ve hook'un kalanını atlıyor | Dokümante edilen "informational, resolved incident" niyeti gerçekleşmiyor; aksiyondan sonraki task'lar koşmuyor | Devam-tipi sonuç boundary aksiyonu iliştirmeden dönüyor, pipeline step incident yazan dala girmiyor |
| Kurtulan instance hâlâ aktif incident bildiriyor | Başarılı retry sonrası `hasActiveIncident` true kalıyor (abort iki incident bırakıyor, `Unfault()` yalnız birini resolve ediyor) | "Neden takıldı?" ekranı sağlıklı instance'ta bayat sebep gösterir |
| Yeniden fault eden retry'dan sonra statü yanıtla çelişiyor | Retry gövdesi `"status":"F"` derken instance `Active` yerleşiyor ve artık `retry` edilemiyor (`Instance:100027`) | Ambient scope'un bayat Active'i, `RequiresNew` scope'un yazdığı Faulted'ı eziyor (retry yolunda cross-UoW last-writer-wins) |
