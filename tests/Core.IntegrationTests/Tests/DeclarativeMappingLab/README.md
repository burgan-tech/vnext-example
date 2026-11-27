# declarative-mapping-lab — bildirimsel task mapping'leri (vnext#1229)

## Neyi denetliyor

**Tek iddia:** Bir task binding'i C# `IMapping` (`.csx`) yerine bildirimsel `input` / `output`
bloklarıyla (`select`, `into`, `set` — değerler Dynamic Expresso ifadesi) isteği şekillendirebilir ve
sonucu instance verisine yazabilir; publish bu blokları derleyip hatayı JSON konumuyla reddeder.

Runtime sözleşmesi: vnext `docs/runtime/declarative-task-mappings.md`. Şema: vnext-schema
`inputMappingBlock` / `outputMappingBlock` (`mapping` ile karşılıklı dışlayıcı).

## Akış

```
start-declarative-mapping-lab → ready (Initial)
   ├─ run-transition-mapping  onExecutionTasks: dml-http-echo  input + output  → transition-mapped (Finish)
   ├─ run-entry-mapping       (task'sız)                                       → entry-mapped (Finish)
   │                          entry-mapped.onEntries: dml-http-echo  output select $.body, into YOK
   └─ run-bad-cast            onExecutionTasks: dml-http-echo  output "gold".AsInt32()  → transition-mapped
```

`dml-http-echo` (Http, tip 6) MockLab `POST /api/dml/echo`'yu çağırır. URL'i `API_BASEURL/api/dml/echo`'dur; her binding'in input bloğu `$.url`'i `task.url.ToString().Replace("API_BASEURL", config.get("Example:ApiBaseUrl", "http://localhost:3001"))` ile çözer. Route istek gövdesindeki
`customerId`, `customer.name`, `source` alanlarını ve `x-channel` header'ını `echo` altında geri
döndürür, ayrıca sabit `score: 87`, `level: "gold"` verir. Böylece **yanıtın kendisi input bloğunun ne
yazdığını**, instance verisi de output bloğunun ne yazdığını kanıtlar.

## Beklenen sonuç

| Test | İddia |
| --- | --- |
| `TransitionTask_InputAndOutputBlocks_WriteTheExpectedData` | Input: `$.url` = `API_BASEURL` config'ten + `?customer=` + `urlEncode(data.customer.id)` (output `task.url` ile `$.dml.calledUrl`'e yazar; test `…/api/dml/echo?customer=c-42` ile bittiğini ve yer tutucunun kalmadığını doğrular), `select $.data.customer into $.body.customer`, `set` ile `$.body.customerId` ve `$.headers.x-channel`; task'ın statik gövdesi (`source: static`) korunur. Output: `echo` alt ağacı `into $.dml.echo`, ardından `set`: `result.body.score.AsInt32() + 10` = 97, `result.statusCode` = 200, `instance.currentState` = `ready` (ChangeState'ten önce), `instance.key`, `now`, `user.available` = true (kullanıcı hop'u), `input.amount` = 5 (transition gövdesi). Eksik ara üye (`data.nope.deeper`) hiçbir şey yazmaz, hata da vermez. Instance `transition-mapped`'te `C` |
| `EntryTask_SelectWithoutInto_MergesAtTheDataRoot` | `onEntries` binding'i: `select $.body` + `into` yok → seçilen nesne verinin köküne merge olur (`score`, `level`, `echo` kökte); `instance.currentState` girişte `entry-mapped`; başlangıç verisi korunur |
| `OutputExpressionError_FaultsTheInstance_AndWritesNothing` | `"gold".AsInt32()` eksik değer değil hatadır: mapping başarısız olur, boundary olmadığı için instance `F` (vnext#1257); delta'nın hiçbir parçası yazılmaz |
| `Publish_RejectsInvalidBindings` (×2) | `mapping` + `output` birlikte → 400, mesaj `cannot carry both a 'mapping' script`; derlenmeyen ifade → 400, konum `output.set['$.dml.x']`. İkisinde de konum `states[ready].transitions[run-transition-mapping].onExecutionTasks[0]` |

## Dikkat noktaları

- **Task URL'i `API_BASEURL` yer tutucusu taşır**, `.csx`'li lab'larla aynı; bildirimsel binding onu input
  bloğunda `config.get("Example:ApiBaseUrl", …)` ile çözer (orchestration `appsettings.json`'da
  `Example:ApiBaseUrl`). Yer tutucuyu çözmeyen bir binding geçersiz URL'le hata verir — bu yüzden üç
  binding'in üçü de `$.url`'i yazar.
- **MockLab şablonu null-güvenli olmalı.** Şablon, eksik bir nesnenin üyesini okursa
  (`request.body.customer.name`, `customer` yokken) MockLab `X-Mocklab-Template-Error` döndürüp
  gövdeyi ham şablon metni olarak yollar; HTTP task'ı bunu **string** olarak alır. O zaman
  `select $.body` (into'suz) "must select a JSON object" hatasıyla düşer, `result.body.level` ise
  eksik üye sayılıp **sessizce atlanır**. Seed bu yüzden `customerName`'i `{{ if … }}` ile korur.
- Input hedefleri task `config`'inin alanlarıdır (`$.url`, `$.body.x`, `$.headers.x`); task tipinin
  okumadığı bir alan yok sayılır. Http task'ında ayrı bir query alanı yok — query URL'e eklenir.

## Task mapping'leri dışında Dynamic Expresso (`ValueSlotExpressionTests`)

**İddia:** `location: "dynamicExpresso"` (`encoding: "NAT"`) taşıyan bir `ScriptCode`, `.csx` olmadan satır içi
ifade olarak şu yuvalarda çalışır: transition `timer`, `resourceLock.keyExpression`, automatic transition `rule`,
AiAgent `prompt` ve `decision.mapping`. CacheAside task'ı kaynağını C# `sourceMapping` yerine bildirimsel
`sourceInput` / `sourceOutput` bloklarıyla şekillendirir. Sözleşme: vnext `docs/runtime/declarative-task-mappings.md`
§ "Dynamic Expresso outside task mappings" ve § "Declarative blocks of a nested task".

```
dml-value-slots (1.0.3)
  idle (Initial)                    errorBoundary: Execution:200010 → seat-busy (shared) → seat-waiting
    ├─ book-seat     resourceLock Acquire, key "dml-vs:seat:" + context.Body["seat"]  → seat-held
    │                seat-held ─ release-seat  resourceLock Release (aynı ifade)       → seat-released
    ├─ cache-read    ($self) dml-cacheaside-echo, output select $.body into $.cache.latest
    ├─ route         → routing: to-large  rule context.Instance.Data["amount"].AsDouble() > 1000  → routed-large
    │                           to-small  rule … <= 1000                                          → routed-small
    ├─ book-seat-std resourceLock Acquire, key "dml-vs:seat:" + data.seat (standart değişken) → seat-held
    │                seat-held ─ release-seat-std  Release (aynı ifade)                  → seat-released
    ├─ cache-read-std ($self) dml-cacheaside-data-key, output select $.body into $.cacheStd.latest
    ├─ book-seat-input resourceLock Acquire, key "dml-vs:seat:" + input.seat (hop gövdesi)  → seat-held
    │                seat-held ─ release-seat-input  Release (aynı ifade)                → seat-released
    ├─ route-input   (task'sız) → routing-input: to-input-fast  input.lane == "fast" → input-fast
    │                                            to-input-slow  input.lane == "slow" → input-slow
    │                input-fast (devam hop'u): to-continuation-null    input == null → input-continuation-null
    │                                          to-continuation-leaked  input != null → input-continuation-leaked
    ├─ book-seat-header resourceLock Acquire, key "dml-vs:seat:" + headers["x-seat"] (istek header'ı) → seat-held
    │                seat-held ─ release-seat-header  Release (aynı ifade)               → seat-released
    ├─ route-channel (task'sız) → routing-channel: to-channel-web    headers["x-channel"] == "web"  → channel-web
    │                                              to-channel-other  !headers.has("X-Channel") || headers["X-Channel"] != "web" → channel-other
    ├─ route-std     → routing-std: to-large-std  data.amount.AsDouble() > 1000 && instance.currentState == "routing-std"
    │                               to-small-std  data.amount.AsDouble() <= 1000 && user.available
    └─ wait-{timespan|iso|datetime|null} → timer-{…} ─ fire-{…} (triggerType 2, timer ifadesi) → timer-fired
         timespan  TimeSpan.FromSeconds(data.waitSeconds.AsDouble())   iso  data.waitIso ("PT3S")
         datetime  now.AddSeconds(3.0)                                  null null (anında)

dml-agent-slots (1.0.0)  — ai-triage-agent, provider local, tools.exclude lab_script
  classifying (AiAgent)  prompt   "<message>" + data.message.ToString() + "</message>"
                         decision script: pick == "by-output" ? (outcome.output.category == "question" ? is-question : is-fraud)
                                          : candidates.has(pick) ? pick : null (→ fallback classify-failed)
```

`dml-cacheaside-echo` (tip 18): anahtar `"dml-vs:ca:" + context.Instance.Key`, kaynak `dml-http-echo`;
`sourceInput` `$.url`'i `config`'ten çözer, `$.body.customerId` ve `x-channel: cache` yazar; `sourceOutput`
`select $.body.echo into $.echo` + `cachedAt` (`now`), `scorePlus` (`result.body.score + 1`), `calledUrl` (`task.url`).
Cache'e yazılan bu nesnedir, ham yanıt değil. `dml-cacheaside-data-key` aynı şekildedir; tek fark anahtar: `"dml-vs:ca-data:" + data.customer.id`.

**Tek değişken standardı (vnext, 2026-11-25):** condition rule'ları ve string anahtarlar (CacheAside `key`, function
cache anahtarları, `resourceLock.keyExpression`) artık değer yuvalarıyla aynı değişkenleri görür — `data`, `input`,
`instance` (camelCase), `now`, `user` — ve legacy `context` değişmeden durur. `*-std` geçişleri yeni değişkenleri,
eski geçişler (`book-seat`, `cache-read`, `route`) geriye uyumluluğu kanıtlar.

**Tek hop girdisi (vnext, 2026-11-25):** `input` = bu hop'u tetikleyen ham istek gövdesi, hop boyunca sabit — hangi
adım okursa okusun (lock 25, task'lar, agent, auto 80, timer 90). Önceden lock anahtarında hep, auto rule'da ise
hop'ta task adımı yoksa `null`'dı. Gövde taşımayan hop'lar (auto devam, ateşlenen timer, SubFlow resume) ve okuma
yolları `null` görür. `*-input` geçişleri bunu kanıtlar.

**`headers` değişkeni (vnext, 2026-11-25):** her yuva gelen isteğin header'larını salt-okunur görür —
`headers["x-channel"]` (değer ya da yoksa `null`, ad büyük/küçük harf duyarsız) ve `headers.has("x-channel")`.
`context.Headers`'ın arkasındaki sözlüğün kendisi üzerine ince bir görünümdür (kopya yok, yalnız kullanılırsa
bağlanır), bu yüzden `Workflow:HeaderExposure:Deny` listesinin çıkardığı ad ikisinden de eksiktir. Lokal host'larda
deny listesi tanımlı olmadığından bu davranış yalnız runtime unit testleriyle kanıtlanır (`ExpressionHeadersTests`),
burada değil. Tireli ad indexer ister (`headers.x-channel` çıkarma işlemidir). `*-header` / `route-channel` geçişleri
bunu kanıtlar; header testin kendi caller header'larının yanına eklenir (`RunWithHeadersAsync`).

| Test | İddia |
| --- | --- |
| `TransitionTimer_ExpressionResult_ArmsAndFiresTheTimer` (×4) | State function'daki `executeAtUtc` hop'tan ~3 sn sonra (TimeSpan, ISO string, DateTime); `null` = anında, runtime `TimerSchedule.Immediate` ile now + 2 sn kurar. Dördü de `timer-fired`'a `C` ile ulaşır, süresinden önce değil |
| `ResourceLockKeyExpression_SameKeyConflicts_DifferentKeyDoesNot_ReleaseFreesIt` | Aynı `seat`: ikinci instance `Execution:200010` → boundary → `seat-waiting`; farklı seat aynı anda `seat-held`; `release-seat` (aynı ifadeyle Release) sonrası yeni instance aynı seat'i alır |
| `CacheAside_DeclarativeSourceBlocks_ShapeTheCallAndTheCachedValue_SecondReadHits` | Echo: `customerId`, `channel = cache`, statik `source`; cache'lenen nesne `scorePlus = 88`, `calledUrl` yer tutucusuz, `score`/`level` YOK. İkinci okuma aynı `cachedAt`'i döndürür ve MockLab bu customer için tam **1** çağrı görür |
| `AutoTransitionRule_DynamicExpresso_RoutesOnInstanceData` (×2) | `amount` 5000 → `routed-large`, 20 → `routed-small` (transition gövdesi veriye merge olur, Auto adımı ifadeyi okur) |
| `ResourceLockKeyExpression_StandardDataVariable_SameKeyConflicts_ReleaseFreesIt` | Aynı senaryo, anahtar `data.seat` (başlangıç verisi; hop'lar gövde taşımaz): aynı seat → `seat-waiting`, farklı seat → `seat-held`, `release-seat-std` sonrası yeni instance alır |
| `CacheAside_KeyFromDataVariable_IsSharedAcrossInstancesOfTheSameCustomer` | Aynı `customer.id`'li **iki farklı instance** tek cache girdisini paylaşır: ikincinin okuması hit (aynı `cachedAt`), MockLab bu customer için tam **1** çağrı — `context.Instance.Key` anahtarıyla imkânsız |
| `AutoTransitionRule_StandardVariables_RoutesOnData` (×2) | `data.amount … && instance.currentState == "routing-std"` → `routed-large`; `… <= 1000 && user.available` → `routed-small` (kullanıcı hop'unun ardındaki auto hop'ta caller var) |
| `AutoTransitionRule_ReadsTheHopInput_WithoutTasks_AndNullOnTheContinuation` (×2) | Task'sız `route-input` hop'unda `routing-input` rule'ları `input.lane`'i okur: `fast` → `input-fast`, `slow` → `input-slow`. `fast`'ın ardındaki auto devam hop'unda `input == null` → `input-continuation-null` (`input != null` dalı `input-continuation-leaked` hiç seçilmez) — gövdenin `lane`'i veriye merge olduğu hâlde |
| `ResourceLockKeyExpression_InputVariable_IsTheTransitionBody` | Anahtar `"dml-vs:seat:" + input.seat` (lock adımı task'lardan önce koşar): aynı seat → `seat-waiting`; legacy `book-seat` (`context.Body["seat"]`) aynı seat'te de çakışır — **aynı anahtar**; farklı seat → `seat-held`; `release-seat-input` sonrası yeni instance alır |
| `AutoTransitionRule_HeadersVariable_RoutesOnTheRequestHeader` (×3) | Manuel `route-channel` isteği `x-channel` header'ı taşır; aynı hop'taki `routing-channel` rule'ları `headers` değişkeniyle okur: `web` → `channel-web`; `mobile` ve header'sız istek → `channel-other` (`has` ve indexer büyük/küçük harf duyarsız) |
| `ResourceLockKeyExpression_HeadersVariable_KeysOnTheRequestHeader` | Anahtar `"dml-vs:seat:" + headers["x-seat"]` (gövde yok): aynı header değeri → `seat-waiting`; gövdeli `book-seat-input` aynı seat'te de çakışır — **aynı anahtar**; farklı değer → `seat-held`; `release-seat-header` sonrası yeni instance alır |
| `AgentDecisionExpression_ChoosesTheTransition` (×3) | `is-fraud` → `escalated` (`Script`); `by-output` → `outcome.output.category` üzerinden `is-question` → `answered`; aday dışı → `null` → `failed` (`Fallback`) |
| `AgentPromptExpression_BuildsThePromptFromData` | `local` sağlayıcı token'ı karakter/4 sayar: 4000 karakterlik mesaj kısa olandan ≥ 900 token fazla — ifade `data.message`'ı prompt'a taşıdı |

Dikkat noktaları:

- `TimeSpan.FromSeconds(2)` derlenmez (int literal `long`/`double` overload'ları arasında belirsiz): `2.0` ya da
  `data.x.AsDouble()` yaz.
- Bütün Dynamic Expresso yuvaları (rule, anahtar, timer, prompt, decision) aynı değişkenleri görür: `data`, `input`,
  `instance`, `now`, `user`, `headers`, `context` (legacy, PascalCase, `context.Body` = transition gövdesi). İstisna: agent tool
  `binding.code` (`args`, `data` ExpandoObject, `now`). JSON değeri karşılaştırmak için dönüştür:
  `data.amount.AsDouble()`, `data.x.ToString() == "…"`.
- Agent koşusunun `Request` satırı `promptHash` / `promptLength` taşır ama okuma API'sinde yok; prompt'un birebir
  kanıtı Postgres'tedir (`dml_agent_slots."InstanceAgentRuns"."Request"`, `transcript: none` iken `prompt` null).
  Test bu yüzden token farkına bakar.
- Agent ve `sys-agents` flow'u SDK tarafından yayınlanmaz; test sınıfı `AiAgentLabTests` gibi kendisi yayınlar.
- CacheAside sonucu `metadata.CacheHit`'i bildirimsel `result`'ta görünmez (`result` = `body`/`headers`/`statusCode`);
  hit, kaynak zamanında damgalanan `cachedAt` ve MockLab çağrı sayısıyla kanıtlanır.

## Nasıl çalıştırılır

Lokalde derlenen runtime'a karşı (container image eski kodu taşır), MockLab ayakta:

```bash
docker compose up -d     # repo kökünde, infra'dan SONRA; seed değiştiyse önce `docker compose down -v`
dotnet test tests/Core.IntegrationTests \
  --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~DeclarativeMappingLab" -v minimal
```

MockLab yoksa MockLab'e bağlı 5 test (3 + iki CacheAside) **skip** eder; publish, timer, lock, auto ve agent testleri yine koşar. Bileşenleri SDK
`LocalDomainPublisher` publish eder. Şema: yayımlanmış vnext-schema bu blokları henüz tanımıyor;
`npm run validate` için lokal vnext-schema dalını bağla.

## Doğrulama durumu

✅ **5/5 yeşil** (2026-11-24, vnext `claude/issue-1227-analysis-50e96f` + config-alanı input modeli, lokal
binary). MockLab `_admin/logs`: `POST /api/dml/echo?customer=c-42`, `Host: localhost:3001`, flow
`1.0.1`, `x-channel: web`, gövdede statik `source` korunmuş. Komşu `AutoPriority` ile birlikte 7/7.

✅ **`ValueSlotExpressionTests` 12/12 yeşil, lab toplam 17/17** (2026-11-25, aynı vnext worktree'si + değer yuvaları,
lokal binary; komşu `AutoPriority` 2/2). Kanıt: orchestration log'u `Resource lock acquired: Key=dml-vs:seat:s-…` →
ikinci instance `Resource lock conflict` (`Execution:200010`) → farklı seat `…-b` acquired → `released` → yeni sahip
acquired; script'e düşme uyarısı (`10270`/`10271`) yok. MockLab: CacheAside kaynağı `POST /api/dml/echo` gövde
`{"source":"static","customerId":"c-…"}`, `x-channel: cache`, iki okumada tek çağrı. Timer `executeAtUtc` hop'tan
+3,02–3,03 sn (TimeSpan/ISO/DateTime), `null` +2,0 sn. Agent `Request.promptHash` = SHA-256(`<message>hello nope</message>`),
`promptLength` 29 — ifadenin ürettiği metin birebir.

✅ **Standart değişkenler: `ValueSlotExpressionTests` 16/16, lab 21/21, `AutoPriority` 2/2 — toplam 23/23** (2026-11-25,
vnext `claude/issue-1227-analysis-50e96f` commit'lenmemiş "tek değişken standardı", lokal Debug binary; `dml-value-slots`
1.0.1, yeni task `dml-cacheaside-data-key` 1.0.0). Eski `context` testleri aynı koşuda yeşil (geriye uyumluluk).
Kanıt: orchestration log'u `data.seat` anahtarıyla `Resource lock acquired: Key=dml-vs:seat:s-1ddf…` → ikinci instance
`Resource lock conflict` → `…-b` acquired → `released` → yeni sahip acquired; 10159 / 10270 / 10271 / 10272 (ifade
hatası, script'e düşme) yok. MockLab: `data.customer.id` anahtarlı CacheAside iki ayrı instance için tek
`POST /api/dml/echo` çağrısı.

✅ **Tek hop girdisi: lab + `AutoPriority` 26/26** (2026-11-25, vnext `claude/issue-1227-analysis-50e96f` commit'lenmemiş
"uniform hop input", lokal Debug binary; `dml-value-slots` 1.0.2; 3 yeni test). Kanıt: orchestration log'u
`input.seat` anahtarıyla `Resource lock acquired: Key=dml-vs:seat:s-4125…` → **iki** `Resource lock conflict` (input
anahtarlı ve legacy `context.Body` anahtarlı ikinci/üçüncü instance, aynı anahtar) → `…-b` acquired → `released` → yeni
sahip acquired; 10270 / 10271 yok. `route-input` `fast` → `input-continuation-null` (`C`), `slow` → `input-slow` (`C`).

✅ **`headers` değişkeni: lab + `AutoPriority` 30/30** (2026-11-25, vnext `claude/issue-1227-analysis-50e96f` commit'lenmemiş
`headers` değişkeni, lokal Debug binary; `dml-value-slots` 1.0.3; 4 yeni test). Kanıt: orchestration log'u
`headers["x-seat"]` anahtarıyla `Resource lock acquired: Key=dml-vs:seat:s-28ce…` → `Resource lock conflict` (header
anahtarlı ve gövdeli `book-seat-input` instance'ı, aynı anahtar) → `…-b` acquired → `released` → yeni sahip acquired;
ifade hatası / script'e düşme log'u yok. `route-channel`: `web` → `channel-web`, `mobile` ve header'sız → `channel-other`
(hepsi `C`). Deny listesi lokal host'larda tanımlı değil: `Workflow:HeaderExposure:Deny`'ın `headers`'tan da düştüğü
yalnız runtime unit testleriyle kanıtlı.
