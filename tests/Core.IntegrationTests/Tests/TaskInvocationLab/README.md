# task-invocation-lab — Local/Remote task invocation routing, error boundary, sonuç şekli parity

## Neyi denetliyor

Üç iddia (vnext issue #1007 — Http, DaprService, Soap, StateStore ve CacheAside artık Orchestration
üzerinde in-process çalışıyor, `Workflow:TaskInvocation` routing config'i her tipi tek tek eskisi
gibi Execution servisine geri döndürebiliyor):

1. **Beş task tipi de in-process (Local) yolda çalışıyor.** Http (tip 6), DaprService (tip 3),
   Soap (tip 16), StateStore (tip 17, set/get round-trip) ve CacheAside (tip 18, miss-then-hit
   round-trip) — hepsi `landed`'a ulaşıyor, incident yok, paylaşılan `TilResultProjection.csx`
   mapping'inin yazdığı `til*` alanları bekleneni taşıyor.
2. **Error boundary, task'ın SONUCUNDAN doğru handler'ı seçiyor.** Üç kusur senaryosu: HTTP 500 →
   task seviyesi Notify (`notified`), MockLab'in 5 saniye geciken route'una karşı 2 saniyelik
   client timeout → task seviyesi Rollback (`rolled-back`), SOAP fault → task seviyesi Abort
   (instance fault olur). Üçü de aynı task referansının `errorBoundary.onError` bloğunda —
   `boundaryLevel` her zaman `Task`.
3. **`TaskInvocationResult` şekli routing moduna göre değişmiyor.** Aynı testler, Orchestration
   host'u önce şu anki varsayılan (beş tip de Local) ile, sonra hepsi Remote'a zorlanmış halde
   koşturulduğunda DEĞİŞMEDEN geçmeli — bkz. aşağıdaki "Nasıl koşulur (iki mod)".
4. **Fonksiyon yanıt cache'i (`IStateStoreCacheGateway`) de aynı seam üzerinde.** `til-cached-echo`
   fonksiyonu `function.cache` yapılandıran TEK bileşen: bu repoda başka hiçbir senaryo bu yolu
   kullanmıyordu, yani gateway ve onun `function-response` bileşen tipiyle yaydığı
   `Cache.Get`/`Cache.Set` span'leri hiçbir koşuda uçtan uca çalışmıyordu.

## Neden var

vnext `1007-…` dalı beş task tipini ayrı bir Execution servisinden Orchestration host'una taşıdı,
her tipi `Workflow:TaskInvocation:Modes` ile tek tek Remote'a döndürebilen bir routing katmanı
arkasında (`docs/runtime/task-invocation-routing.md`). Bu değişiklikten önce vnext-example'da
DaprService, Soap ve StateStore/CacheAside tiplerinin **hiç** bileşeni yoktu — üçü de bu senaryoyla
ilk kez örnekleniyor. `tilTaskType` alanı ayrıca bu dalda routing'e bağlı olarak iki kere yanlış
damgalanmış bir alan (build raporuna bakın) ve `InstanceTasks` günlüğüne serileştiriliyor, bu yüzden
`ResultModelParityTests` özellikle onu hedefliyor.

## Akış şeması

Tek workflow (`task-invocation-lab`, tip `F`), hub state `ready` üzerinde bir case başına bir
transition:

```
task-invocation-lab
  start → ready (hub)
    case-http-ok ──────────► landed             (Http 200)
    case-http-500 ─────────► notified            (Http 500 → Task Notify)
    case-http-slow ────────► rolled-back          (client timeout → Task Rollback, onError ile — onTimeout DEĞİL)
    case-dapr-ok ───────────► landed              (DaprService → mocklab app-id)
    case-soap-ok ───────────► landed              (Soap 1.1 200)
    case-soap-fault ────────► F (Abort)            (SOAP fault üzerinden HTTP 500)
    case-statestore-set ────► landed              (Dapr state store'a yaz)
    case-statestore-get ────► landed              (aynı statik anahtarı oku)
    case-cacheaside ────────► landed              (til-cache-source üzerinden read-through; iki instance = miss + hit)
    cancel-task-invocation-lab ► til-cancelled     (şekil paraleliği için — error-boundary-lab'dan)
```

Workflow'un dışında, domain kapsamlı tek bir fonksiyon:

```
GET /api/v1/core/functions/til-cached-echo     (scope D)
  cache: { key "til:fncache:echo", ttlInSeconds 60 }
    MISS → til-http-ok çalışır, yanıt cache'e yazılır
    HIT  → task'lar HİÇ çalışmaz, cache'teki yanıt aynen döner
```

**`onTimeout` değil `onError`, ve `errorCodes` değil `errorTypes` kullanıldı — ikisi de canlı
runtime'da (postgres `InstanceIncidents` satırları okunarak) doğrulandı, tahminle DEĞİL.**
`errorBoundary.onTimeout` şema-geçerli, publish oluyor, ama `CompiledBoundary.Compile` onu HİÇ
okumuyor — açık şekilde yazılmış bir timeout handler'ı sessizce hiçbir şey yapmıyor. Bir HTTP
client timeout'u boundary'ye ayrı bir "timeout" sinyali olarak hiç ulaşmıyor: `StatusCode: null`'lu
sıradan bir `TaskInvocationResult.Failure` ve `Metadata["ExceptionType"] = "TaskCanceledException"`
olarak geliyor — timeout-şekilli bir kod DEĞİL, çıplak bir .NET tip adı.

Daha da önemlisi: `errorCodes` bu başarısızlıkla HİÇBİR ZAMAN eşleşemez — ne kısa form
(`Task:Http:til-http-slow`), ne tam kompozit form (`Task:Http:til-http-slow:TaskCanceledException`),
ne de çıplak `TaskCanceledException` metni. Retry kuralı olmadığında eşleşme her zaman
`TaskExecutionEngine.HandlePostRetryFailureAsync`'in `ResolveExcluding` çağrısından geçer, ve o yol
`CompiledBoundaryChain.FindMatchExcluding(NormalizedError, …)` üzerinden `error.OriginalCode`
(bu hata için her zaman null — `HttpTaskInvocation` hiç `Metadata["ErrorCode"]` yazmıyor) ve
`error.ExceptionType` ("TaskCanceledException") ile eşleştirir; `error.Code` bu yolda HİÇ
kullanılmaz. `errorCodes` sadece `OriginalCode`/`StatusCode`'a bakar — ikisi de null olduğunda
(client-side timeout) `errorCodes` içine ne yazılırsa yazılsın (`"*"` hariç) eşleşme imkansızdır.
Tek çalışan alan `errorTypes` — `error.ExceptionType`'a bakan `MatchesExceptionType`. `til-http-slow`
'un boundary'si bu yüzden `errorTypes: ["TaskCanceledException"]` olarak yazıldı;
`ErrorBoundaryInteractionTests` tam olarak bunu ve sonucu (`BoundaryAction: Rollback`,
`rolled-back`/`Completed`) doğruluyor.

**Sonuç: bugün bir domain yazarı için HTTP task timeout'unu yakalamanın keşfedilebilir hiçbir yolu
yok.** `onTimeout` etkisiz, `errorCodes` bu hata için yapısal olarak asla eşleşemez (statusCode ve
OriginalCode ikisi de null), ve eşleşen tek şey — `errorTypes` altında çıplak bir .NET istisna tipi
adı — şemada `errorCodes`'dan ayırt edilemiyor, yani gayet makul görünen bir
`errorCodes: ["TaskCanceledException"]` sessizce hiçbir şey yapmıyor. `errorTypes: ["*"]` tek genel
alternatif ama SADECE timeout'u değil, istisna kaynaklı her başarısızlığı yakalıyor.

**StateStore ve CacheAside statik anahtar kullanıyor** (`til:statestore:roundtrip`,
`til:cacheaside:roundtrip`) — instance-scoped değil, paylaşılan Dapr state store'un kendisi. `set`
her zaman aynı literal değeri yazdığı için sıra bağımsızdır; CacheAside'ın miss-then-hit'i DEĞİL —
60 saniyelik TTL içinde aynı anahtara dokunan BAŞKA bir case/test de "hit" üretebilir. Bu yüzden
`TaskTypeInvocationTests.CacheAsideRoundTrip_MissesThenHits` bunu tek yerde ölçer,
`ResultModelParityTests` ise `CacheHit` boole değerini hiç iddia etmez (bkz. o dosyanın XML açıklaması).

## Nasıl koşulur (iki mod)

Runtime **lokal derlenmiş** olmalı (`1007-…` dalı henüz release edilmedi).

```bash
# 1) altyapı
cd ../vnext/etc/docker && ./run-docker.sh

# 2) dört host, her biri ayrı terminalde — MOD 1 (varsayılan: beş tip de Local)
dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host --launch-profile http
dotnet run --project execution/BBT.Workflow.Execution.HttpApi.Host --launch-profile http
dotnet run --project workers/BBT.Workflow.Workers.Inbox --launch-profile http
dotnet run --project workers/BBT.Workflow.Workers.Outbox --launch-profile http

# 3) MockLab (bu repo) — task-invocation-lab-collection.json seed'i yalnız koleksiyon YENİYSE içeri alınır
cd ../vnext-example && docker compose up -d       # gerekirse: docker compose down -v && docker compose up -d

# 4) testler — MOD 1 (Local, varsayılan appsettings.json)
cd tests/Core.IntegrationTests
dotnet test --settings test.runsettings --filter "FullyQualifiedName~TaskInvocationLab"
```

**MOD 2 — beş tipi de Remote'a zorlayıp AYNI testleri tekrar koşturun** (yalnız
`ResultModelParityTests`'in anlamlı olması için değil, tüm dosyanın iki modda da değişmeden
geçtiğini görmek için):

```bash
# Orchestration host'u DURDURUN, bu env değişkenleriyle YENİDEN başlatın
# (TaskInvocationOptions tek seferlik bind edilir — restart şart, appsettings.json değişikliği YETMEZ):
Workflow__TaskInvocation__Modes__http=Remote \
Workflow__TaskInvocation__Modes__daprservice=Remote \
Workflow__TaskInvocation__Modes__soap=Remote \
Workflow__TaskInvocation__Modes__statestore=Remote \
Workflow__TaskInvocation__Modes__cacheaside=Remote \
dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host --launch-profile http

# Execution servisi zaten ayakta olmalı (Remote yolun gideceği yer budur)

cd tests/Core.IntegrationTests
dotnet test --settings test.runsettings --filter "FullyQualifiedName~TaskInvocationLab"
```

| Değişken | Anlamı |
| --- | --- |
| `VNEXT_BASE_URL` | Konteyner başlatmayı atlar, verilen orchestrator'a bağlanır. |
| `MOCKLAB_BASE_URL` | MockLab admin API'si; varsayılan `http://localhost:3001`. Ulaşılamazsa MockLab'a bağlı testler (Http/DaprService/Soap/CacheAside case'leri) **skip** olur; iki StateStore case'i MockLab'a hiç bağlı değildir. |

## Beklenen sonuç

| Sınıf | Doğruladığı |
| --- | --- |
| `TaskTypeInvocationTests` | Beş tipin başarı yolu; `landed`, incident yok, projeksiyon alanları (`tilCase`/`tilStatusCode`/`tilHasData`/`tilData`); StateStore set→get round-trip; CacheAside miss→hit round-trip. |
| `ErrorBoundaryInteractionTests` | Task seviyesi Notify/Rollback/Abort'un doğru state'e/incident'e/fault'a götürdüğü; timeout case'inin `onError`+`errorTypes: ["TaskCanceledException"]` üzerinden çalıştığı (ne `onTimeout`, ne `errorCodes`); Notify VE Rollback'in ikisinin de kendi incident'ını landing transition başarıyla tamamlanınca otomatik resolve ettiği; timeout'ta `tilStatusCode`'un null, `tilBodyLength`'in 0 olduğu. |
| `FunctionResponseCacheTests` | Fonksiyon yanıt cache'inin miss→hit davranışı: ikinci çağrının `computedAtUtc` damgası birincininkiyle AYNI (yani task seti hiç çalışmadı) ve cache'ten dönen yanıt aynı `FunctionResponseOutput` şeklini koruyor. İki modda da değişmeden geçer. |
| `ResultModelParityTests` | **İki modda da DEĞİŞMEDEN geçmesi gereken** projeksiyon şekli: `tilTaskType` (case-insensitive — bkz. dosyanın XML açıklaması), `tilStatusCode`, metadata anahtar kümesi. Bu dosyanın değeri TEK bir koşudan değil, yukarıdaki iki komutun İKİSİNDEN de gelir. |

## Ölçülen / bilinen sınırlar

1. **`tilTaskType` case-insensitive doğrulanıyor.** Runtime kaynağını okuyarak (çalıştırmadan)
   bulunan gerçek: `TaskExecutorBase.CreateSuccessResponse`/`CreateErrorResponse` `TaskType.ToString()`
   (PascalCase enum adı, örn. `"Http"`) damgalıyor; `CacheAsideTaskExecutor` ve
   `HttpTaskInvocation`/`DaprServiceInvocation`/`StateStoreInvocation`'ın kullandığı
   `TaskInvocationResult` fabrikaları ise `BBT.Workflow.Execution.TaskTypes`'ın küçük harfli wire
   sabitini taşıyor (örn. `"cacheaside"`). Her iki kod yolu da Local/Remote routing'den BAĞIMSIZ,
   koşulsuz paylaşılıyor — yani casing farkı tek başına bir Local/Remote sapması KANITLAMAZ. Bu
   testler bu yüzden case-insensitive kimliği pinliyor (doğru tip, hiç boş değil, moddan bağımsız);
   kesin casing'in kendisi bu senaryonun iddiası değil.
2. **`retryCount` gibi bir alan burada yok** — bu senaryonun hiçbir boundary'si retry yapmıyor
   (Notify/Rollback/Abort hepsi tek denemelik). `ErrorBoundaryLab.RetryPolicyTests`'in ölçtüğü
   `retryCount`'un her zaman 0 olması bulgusu burada tekrarlanmıyor çünkü ölçülecek bir retry yok.
3. **CacheAside miss/hit sırası paylaşılan statik anahtara bağlı.** `til:cacheaside:roundtrip`'in
   60 saniyelik TTL'i, bu paketteki BAŞKA bir case (veya aynı paketin `ResultModelParityTests`'i)
   yakın zamanda aynı anahtara dokunduysa "ilk çağrı miss" varsayımını bozabilir —
   `TaskTypeInvocationTests` bunu tek yerde iddia eder ve XML açıklamasında bu riski adlandırır.
4. **`til-cache-source`'un URL'i literal `http://localhost:3001`.** Component build raporunun
   bulduğu gibi, `CacheAsideTaskExecutor.InvokeAsync` kaynak task'ın kendi `InputHandler`'ını hiç
   çalıştırmıyor, bu yüzden `API_BASEURL` placeholder'ı bu task için çözülemiyor. `MOCKLAB_BASE_URL`
   diğer case'leri yönlendirse bile bu task'ı yönlendirmez — varsayılan MockLab portundan farklı bir
   ortamda bu test kırılır.
5. **DaprService case'i (`til-dapr-ok`) MockLab'in ayakta olmasının ÖTESİNDE bir önkoşula sahip**:
   lokal Dapr sidecar'ının `mocklab` app-id'sini MockLab'e yönlendiren bir bileşene ihtiyacı var.
   `IsMockLabUpAsync()` yalnız MockLab'in admin API'sini kontrol eder, bu Dapr yönlendirmesini
   DOĞRULAMAZ — sidecar yanlış yapılandırılmışsa test MockLab ayaktayken bile başarısız olabilir ve
   hata mesajı bunu ayırt etmez.
6. **`til-cached-echo` kendi `InputHandler`'ında `API_BASEURL`'i ÇÖZMEK ZORUNDA.** Bir fonksiyon
   kendi mapping'ini verdiğinde task'ın kendi mapping'inin yerine geçer — task tanımındaki
   placeholder'ı çözen de odur. İlk yazımda bu atlandı ve çağrı 500 ile öldü ("request URI must be
   absolute"); mapping'in `InputHandler`'ı artık diğer örnek fonksiyonlarla aynı
   `GetConfigValue("Example:ApiBaseUrl", …)` satırını taşıyor.
7. **Cache TTL'i 60 saniye, bu yüzden `FunctionResponseCacheTests` ilk okumayı MISS varsaymaz.**
   Üç okuma alır ve ikisinin birbiriyle uyuşmasını arar; zamanlamaya göre değil, damganın
   donmasına göre karar verir.

## Ölçüm

Bu senaryonun trafiğiyle alınan Local/Remote span ölçümleri ve trace bütünlüğü denetimi vnext
reposunda: `docs/runtime/evidence/2026-09-19-task-invocation-routing/README.md`. Özet: hop tip
başına `Task.Invoke` p50'sine 1.5–2.4 ms ekliyor, fonksiyon cache'i hit'te 0.68 ms (Local) ve
2.19 ms (Remote), ve iki modda da eksik span ailesi ya da yetim span YOK.
