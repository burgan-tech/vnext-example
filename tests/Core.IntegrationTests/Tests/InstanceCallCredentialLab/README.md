# InstanceCallCredentialLab — integration test

Runtime başka bir instance'ı çağırdığında — SubFlow start, `SubProcess` / `Start` / `DirectTrigger` /
`GetInstance` / `GetInstances` / `GetInstanceData` task'ı — kimlik header'larını (`sub`, `act_sub`,
`position`, `client_id`, `role`) **tek kuralla** sunar, header başına:

| Mapping ne yaptı | Hedef ne görür |
|---|---|
| Anahtarı boş olmayan bir değerle set etti | Mapping'in değerini |
| Anahtarı **boş** (`""`) set etti | Kimlik hedefe ULAŞMAZ; çağıranın değeri EKLENMEZ |
| Anahtarı hiç set etmedi | Çağıranın değerini (parent'ın hop'unu süren isteğin header'ı) |

Aynı domain'de çağrı ayrıca çözülen header setinin kullanıcısı OLARAK koşar: başlatılan child'ın satırı
`createdBy` = `act_sub`, `createdByBehalfOf` = `sub` ile damgalanır. SubFlow input mapping'inin `Headers`'ı
okunamayan bir şekilse (string, tuple, liste…) SubFlow start'ı child oluşmadan başarısız olur.
Sözleşme: vnext `docs/domain/role-grant-authorization.md` § "Caller credential on instance calls"
(`HttpTaskInvocation.ResolveInstanceCallCredential`). Domain `core`, yalnız script task'lar (MockLab yok),
yalnız aynı-domain yarısı; HTTP / SOAP / Dapr task'ları kapsam dışı.

| Test | Neyi denetliyor |
|---|---|
| `SubFlow_NoMappingHeaders_ChildSeesCallerCredential_AndRunsAsCaller` | Mapping `Headers` vermez → S child `sub` = çağıranın GUID'i, `act_sub` = çağıranınki görür; child satırı `createdByBehalfOf` = çağıran `sub`, `createdBy` = çağıran `act_sub` |
| `SubFlow_DictionaryBlankSub_ChildSeesNoSub_KeepsActSub` | `Headers = Dictionary { sub = "" }` → child `sub` görmez, `act_sub` = çağıranınki; `createdByBehalfOf` boş, `createdBy` = çağıran `act_sub` |
| `SubFlow_AnonymousObjectBlankSub_ChildSeesNoSub_KeepsActSub` | `Headers = new { sub = "" }` → child tarafı bir öncekiyle aynı. Son assert parent'ın tamamlanması — **KIRMIZI**, bkz. "Runtime bulguları" |
| `SubFlow_MappingSetsAnotherSub_ChildSeesMappingValue` | `Headers = { sub = <başka GUID> }` → child o GUID'i görür, `createdByBehalfOf` = o GUID; `act_sub` çağırandan |
| `SubFlow_StringHeaders_FailsTheStart_NoChildExists` | `Headers = "sub="` → start child oluşmadan düşer: parent `icc-p-subflow`/`F`, aktif incident (`Instance:100023`, mesaj "Headers of type 'String' … cannot be read as headers"), `{parentId}-sub` anahtarında ve korelasyondaki id'de child **404** — SubflowStartFailureLab'ın yerleşik beklentisi (post-commit start hatası parent'ı fault'lar, incident açar) |
| `SubProcess_NoMappingHeaders_…` / `SubProcess_BlankSub_…` | SubProcessTask (14) P child: başlık yok → çağıranın `sub`/`act_sub`'ı + satır damgası; task header `sub = ""` → `sub` yok, `act_sub` var, `createdByBehalfOf` boş |
| `Start_NoMappingHeaders_…` / `Start_BlankSub_…` | StartTask (11) ile yeni `F` instance: aynı iki satır |
| `DirectTrigger_NoMappingHeaders_…` / `DirectTrigger_BlankSub_…` | DirectTriggerTask (12) testin başlattığı, `icc-t-wait`'te bekleyen hedefte `touch`'ı tetikler; `touch`'ın onExecutionTasks kaydı (`touchSeen`) çağıranın `sub`/`act_sub`'ını görür / `sub` görmez, `act_sub` görür |
| `Read_NoMappingHeaders_CallerRoleReachesTheRead_ProtectedFieldReturned` (×3: `do-get`, `do-list`, `do-data`) | Hedefin master şemasında `secretNote` `x-roles` allow `icc.reader`. Çağıran `role: icc.reader` gönderir, task header vermez → GetInstance (19) / GetInstances (15) / GetInstanceData (13) sonucu `secretNote`'u taşır |
| `Read_BlankRoleHeader_CallerRoleWithheld_ProtectedFieldNotReturned` (×3) | Aynı çağıran, task header `role = ""` → okuma rolsüz koşar, sonuçta `secretNote` YOK (okuma yine başarılı, 200) |
| `Nested_BlankSubOnSubFlow_NotRefilledOneLevelDown` | Parent `icc-sub-nest`'i `sub = ""` ile başlatır; o child kendi header'ı olmadan StartTask ile `icc-target` başlatır → torun `sub` görmez (baggage'dan ya da ilk çağırandan geri dolmaz), `act_sub` görür, `createdByBehalfOf` boş |

Her okuma testi önce bir **kontrol** yapar: hedefin doğrudan GET'i `icc.reader` ile `secretNote`'u görür,
rolsüz görmez — yani alan gerçekten rol-korumalı ve provider `role` header'ını okuyor.

## Akış şekli

`core/Workflows/instance-call-credential-lab/`, `build-instance-call-credential-lab.py` ile üretilir
(sürüm **1.0.1**; publish sürüm-değişmezdir, değişiklikte `VERSION`'ı patch artır — üretici task'ları da
`core/Tasks/instance-call-credential-lab/` altına yazar). Şema:
`core/Schemas/instance-call-credential-lab/icc-target-master.json` (elle; sürümü `VERSION` ile aynı tutulur).

```
icc-parent (F): start → icc-p-ready [Initial, Active] — test TEK bir manual transition'ı kendi
                sub/act_sub(/role) header'larıyla sürer; varyantı start verisi (variant) seçer
  do-subflow       → icc-p-subflow       (S: icc-sub,      IccSubFlowMapping: none|dict-blank|other|string, key {parentId}-sub)
  do-subflow-anon  → icc-p-subflow-anon  (S: icc-sub,      IccSubFlowAnonMapping: new { sub = "" }, key {parentId}-anon)
  do-nest          → icc-p-nest          (S: icc-sub-nest, sub = "", key {parentId}-nest)
  do-subprocess    → icc-p-subprocess    onEntry SubProcessTask → icc-target, key {parentId}-proc, sync
  do-start         → icc-p-start         onEntry StartTask      → icc-target, key {parentId}-start, sync
  do-trigger       → icc-p-trigger       onEntry DirectTrigger  → targetId, transition touch, sync
  do-get|list|data → icc-p-get|list|data onEntry GetInstance / GetInstances(testId) / GetInstanceData
  her çağrı state'i ─auto→ icc-p-done [Finish]
  Task varyantı (IccCallMapping): none = header yok · blank = sub "" · role-blank = role ""

icc-sub (S):      start → icc-s-entry [onEntry kaydedici] ─auto→ icc-s-done
icc-sub-nest (S): start → icc-n-entry [onEntry kaydedici, sonra StartTask → icc-target, key {childId}-start] ─auto→ icc-n-done
icc-target (F, şema icc-target-master): start → icc-t-entry [onEntry kaydedici]
                  ─auto(mode=wait)→ icc-t-wait ──touch [onExecutionTasks kaydedici → touchSeen]──▶ icc-t-touched
                  ─auto→ icc-t-done
```

**Kaydedici** (`IccRecordMapping`): kaydın yapıldığı hop'un `context.Headers`'ını sözlük olarak okur (dynamic
üye okuması yok — olmayan header'ı dynamic'ten okumak fırlatır) ve `seen` (ya da `icc-t-wait`'te
`touchSeen`) altına beş kimlik header'ının her biri için değeri ve `has_<ad>` bayrağını, `keys` altına tüm
header adlarını, `selfId` altına kendi id'sini yazar. Test hedefi anahtarla (`{callerId}-sub|anon|nest|proc|start`)
GET eder — runtime GET'i id-sonra-key ile çözer.

**Neden manual transition:** kural "çağıran = parent hop'unu süren istek" der; otomatik hop'un çağıranı
yoktur (kimlik eklemez). Bu yüzden çağrı, testin kendi header'larıyla gönderdiği manual transition'ın
hedef state'inde (onEntry / SubFlow) yapılır. Parent'ın kendisi standart `Headers()` ile başlatılır.

## Nasıl koşulur

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~InstanceCallCredentialLab" -v minimal
# beklenen (bugünkü runtime): 17 geçti, 1 kırmızı (SubFlow_AnonymousObjectBlankSub_*, runtime bulgusu)
```

Bağımlılık: yalnız lokal derlenmiş runtime (`core`), varsayılan caller-role provider (`role` header'ı).
`morph-idm` ile okuma testleri anlamını değiştirir (rol IDM'den gelir) — o modda koşulmaz.

## Dikkat noktaları

- **`sub` GUID olmalı.** Runtime onu varchar(36) kimlik kolonuna yazar; daha uzun değer isteği 500'e düşürür.
  `act_sub` da kısa tutulur (`icc-<8 hex>`).
- **Anonim tip SubFlow mapping script'ini bozar.** Bir SubFlow mapping dosyasında herhangi bir anonim tip
  (`new { … }`) varsa o mapping'in OUTPUT handler'ı çalışmaz (aşağıda). İlk koşuda dört SubFlow varyantı
  tek dosyadaydı ve dördü de parent'ı `F`'e düşürdü; anonim varyant bu yüzden kendi dosyasında
  (`IccSubFlowAnonMapping.csx`) ve kendi state'inde. Trigger-task mapping'leri (`IMapping`) etkilenmez:
  onlar `CompileToInstanceAsync<IMapping>` ile derlenir.
- **Okuma sonucu nerede:** `IccCallMapping` `secretNote`'u task cevabının (`context.Body.data`) her yerinde
  arar (üç okuma tipinin cevap şekli farklıdır; arama şekle bağlı değildir).
- **Hedefi test başlatır** (DirectTrigger ve okumalar): o başlatma testin standart `Headers()`'ıyla yapılır,
  `seen`'inde `sub`/`act_sub` yoktur — ölçülen değer `touchSeen` / okuma sonucudur.

## Geçme kriteri + kanıt

Her test parent id'sini, çağıranın `sub` / `act_sub` / `role`'ünü ve hedefin `createdBy` /
`createdByBehalfOf` / `attributes`'ını test çıktısına yazar (TRX `StdOut`). Kanıt: hedefin kendi kaydı
(`seen` / `touchSeen`) + satır metadata'sı + parent'ın `call` kaydı. Fault'ta `GET …/incidents/active` ve
orchestration host logu (instance id ile grep). OpenObserve / Postgres / Elasticsearch MCP'leri koşu
sırasında bağlanamadı: sonuç **"testler geçti, trace'e karşı doğrulanmadı"**; değerler API'den ve host
loglarından okundu.

## Runtime bulguları

**SubFlow output mapping script'teki anonim tipi örnekliyor** (`SubFlow_AnonymousObjectBlankSub_*` kırmızı,
bilerek bırakıldı). `SubflowOutputMappingService.PrepareAsync` mapping'i `CompileToInstanceAsync<object>` ile
derler; `CSharpEvaluator` derlenen assembly'de `typeof(T).IsAssignableFrom(t)` olan İLK tipi seçer —
derleyicinin ürettiği `<>f__AnonymousType0`1` mapping sınıfından önce gelir. Sonuç: input mapping çalışır
(child doğru header'larla başlar ve biter), child bittiğinde output mapping
`ArgumentException: Type <>f__AnonymousType0`1[<sub>j__TPar] is a generic type definition` ile düşer, parent
`icc-p-subflow-anon`/`F`, incident `Instance:100030` (`errorLayer: SubFlow`). Kuralın kendisi (child'ın gördüğü)
doğru; ama belgelenen `new { sub = "" }` şekli, aynı dosyadaki output handler'ı kırdığı için bugün kullanılamaz.
Master'da da aynı satır var — kimlik kuralı değişikliğinden önce de vardı; anonim `Headers` şekli onu
görünür kılıyor.

## Son koşu (2026-11-14, vnext `claude/subflow-subprocess-credentials-704267` @ 56f78a52, lokal core)

17/18 yeşil, 1 kırmızı (yukarıdaki runtime bulgusu). Gözlenen değerler (her satır kendi koşusundan):

| Çağrı / varyant | Hedefin gördüğü | Satır damgası |
|---|---|---|
| SubFlow, header yok | `sub` = çağıran GUID, `act_sub` = çağıran | `createdBy` = çağıran `act_sub`, `createdByBehalfOf` = çağıran `sub` |
| SubFlow, dict `sub = ""` | `has_sub = false`, `act_sub` = çağıran | `createdBy` = `act_sub`, `createdByBehalfOf` boş |
| SubFlow, `new { sub = "" }` | `has_sub = false`, `act_sub` = çağıran | `createdBy` = `act_sub`, `createdByBehalfOf` boş — parent `F` (`Instance:100030`) |
| SubFlow, `sub` = başka GUID | `sub` = o GUID, `act_sub` = çağıran | `createdByBehalfOf` = o GUID |
| SubFlow, `"sub="` | child yok (404) | parent `icc-p-subflow`/`F`, `Instance:100023` (`errorLayer: PostCommit`) |
| SubProcess, yok / `sub = ""` | çağıranın `sub`+`act_sub` / yalnız `act_sub` | çağıran / `createdByBehalfOf` boş |
| Start, yok / `sub = ""` | aynı | aynı |
| DirectTrigger, yok / `sub = ""` | `touchSeen`: `sub`+`act_sub` / yalnız `act_sub` | — |
| GetInstance / GetInstances / GetInstanceData, `role: icc.reader`, header yok | sonuçta `secretNote` var (200) | — |
| aynısı, task `role = ""` | sonuçta `secretNote` yok (200) | — |
| İç içe (`sub = ""` → child → StartTask) | child ve torun `has_sub = false`, `act_sub` = çağıran | torun `createdBy` = `act_sub`, `createdByBehalfOf` boş |

`position` / `client_id` / `role` (SubFlow ve yazma çağrılarında) çağıran göndermediği için hiçbir hedefte
yoktu.

Komşu suite'ler aynı runtime'da (tek tek filtreyle): SubflowCarriedHeadersLab 5+1 skip, SubflowOrchestration 22,
SubflowOverrideLab 12, SubflowStartFailureLab 2, SyncSubProcessStateLab 3, HistoryNoneSubflowLab 3,
ParallelVariableKeyLab 7, RowSecurityLab 156+16 skip, RoleMatrixLab 117, SchemaRolesLab 16, CallerRolesLab 6+3 skip,
HumanTaskChain 12+15 skip, DataIntegrityLab 5 — hepsi yeşil (skip'ler morph-idm / partner domain / mod anahtarı,
beklenen). AuthorizationChainLab 44 + 9 skip + **2 kırmızı**: `AuthorizeAckAgreesWithTheAcknowledgeEndpoint`
(`null`, `chain.leaf-admin`) — `longpoll/ack` uç noktası rolleri artık hiç denetlemiyor (vnext #1027, master'da), ön-kontrol
reddediyor; bu kuralla ilgisi yok, üç koşuda da aynı.

## Kapsanmayanlar

- Cross-domain yarısı (aynı kural, `CurrentUserForwardHeadersHelper` / `DropBlankCredentialHeaders` çıkışı) — ayrı iş.
- `position` / `client_id` için ayrı varyant yok: aynı resolver beş anahtarı aynı döngüde işler; `sub` / `act_sub` /
  `role` üç farklı tüketiciyi (kimlik damgası, `has_*`, `x-roles`) kapsar.
- SubFlow mapping'inde ExpandoObject, `JsonElement` ve script-tanımlı sınıf şekilleri; tuple / liste ile başarısızlık
  (string ile kanıtlandı).
- HTTP / SOAP / Dapr task'ları (eski kural, `AppendCallerCredential`).
