# role-matrix-lab

## Neyi denetliyor

vNext'in **yetkilendirme yüzeylerinin birbiriyle tutarlı olduğunu** ve her birinin kendi kuralını
doğru uyguladığını denetler: `queryRoles`, `transition.roles`, `availableIn` rol daraltması,
`function.roles`, master şemadaki `x-roles` alan budaması ve bunların hepsini raporlayan
`authorize` function'ı.

Tek cümlelik iddia: **bir client'a gösterilen ile `authorize`'ın söylediği ve okuma
function'larının yaptığı asla birbirinden ayrışmaz.**

Bu bir *davranış* (pipeline) senaryosu değildir — akış kasıtlı olarak sıkıcıdır. Her state, her
transition ve her alan, **farklı bir grant kombinasyonunu görünür kılmak** için seçilmiştir.

**Alan maskeleme (`x-masking`, 2026-09-28, `feature/field-masking-lab`; komite değişikliği 2026-09-30).** Master
şema (`1.0.3`) üç maskeli alan taşır: `maskedForAll` (rolsüz kural — herkes maskeli görür), `maskedExceptAuditor`
(allow-only muafiyet — yalnız auditor açık görür; yanlış yazılmış ya da hiç rolü olmayan caller maskeli görür) ve
`replacedNote` (`replace` → `[gizli]`). Ayrıca `hashedCustomerNo` `x-encryption.type: hash` taşır: değer **yazılırken**
`HASHED:SHA256:<hex>` özetine çevrilir (instance'a özgü tuz, `InstanceSecrets` tablosunda), ham değer hiçbir yerde
kalmaz; herkes (auditor dahil — hash muafiyet almaz) aynı özeti görür, iki instance'ta aynı değer farklı özet üretir.
`SchemaFieldMaskingTests` bunları data function, ETag/304, caller kapsamı ve **instance GET/list** üzerinden doğrular:
Faz 2'den (2026-09-30) itibaren GET, liste, data function, senkron yanıt ve Get* task'leri tek okuma servisinden
(`IInstanceDataReadService`) geçer, hepsi aynı maskeyi ve aynı budamayı uygular. `mirror-self` shared transition'ı bir
GetInstanceData (type 13) task'iyle **kendi** verisini okuyup korumalı alanları korumasız alanlara kopyalar. Trigger task
okuması **task'in kendi header'larıyla** değerlendirilir, transition'ı tetikleyenle değil: `mirror-self` header vermez
(okuma çağıranındır: maker tetiklerse maske, `auditTrail` yok, `vault.email` jeton; auditor tetiklerse açık değerler),
`mirror-self-auditor` input mapping'inde `role: morph-idm.auditor` verir (açık değerler) — kim tetiklerse tetiklesin.
Task'in giden başlık seti mapping başlıkları + mapping'in vermediği her biri için çağıranın credential'ıdır (`sub`,
`act_sub`, `position`, `client_id`, `role`); `role` yalnız çağıranın gönderdiği haliyle, morph-idm'in çözdüğü değil. Mapping ayrıca motorun düz görünümünü
(`context.Instance.Data`) kaydeder. Jetonun kendisi kopyalanmaz — yazma
bekçisi başka bir yola konan jetonu reddeder (`EncryptedValueReservedException`), bu yüzden `mirroredVaultEmail` `<token>`
kaydeder. Faz 1 build'inde (`3b22a803`) bu testlerin 11'i kırmızıdır — ayırt edici taban çizgisi budur.

**İç içe veri (`SchemaFieldExposureNestedTests`).** Yalnız primitif alanlarla mutlu yol yeterli değil: `customer`
nesnesinin altında `x-roles` ALLOW yaprağı (`segment`), `x-roles` DENY alt ağacı (`contact`, maker hariç herkes;
rolsüz çağıran da göremez), bu alt ağacın içinde maske (`email`), hash (`phone`) ve iki seviye aşağıda ALLOW
yaprağı (`address.line1`) var; ayrıca bütün olarak korunan nesne dizisi (`accounts`), korunan sayı
(`riskScore`), `$InstanceStarter` (`ownerNote`, `act_sub` ile) ve korumasız string dizisi (`tags`). Her ALLOW
alanı izinli, izinsiz, izinli+reddedilen ve rolsüz çağıranla ayrı ayrı doğrulanır; gizlenen alt ağacın hiçbir
çocuğunun hiçbir biçimi (açık, maskeli, hash) data function gövdesinde görünmemelidir. Data function ve senkron
transition yanıtı aynı ağacı döndürür; instance GET her rol için data function'la **birebir aynı** ağacı döner.

**Grant combinator'ları (`allOf` / `anyOf`, 2026-10-03, runtime branch `feature/role-grant-combinators`) —
yazıldı, henüz koşulmadı.** Ayrı bir akışta: `role-matrix-lab-combinators` (`1.0.0`) + master şema
`role-matrix-combinator-master` (`1.0.0`). Ana akışa dokunulmadı — 113 test onun transition ve alan
listelerini birebir okuyor. Bir grant `role` XOR `allOf` XOR `anyOf` taşır, çocuklar yalnız `{ "role": … }`
(derinlik 1). Değerlendirme Kleene: rol-bağlı yaprak rolsüz caller için *Unknown*, kimlik yaprakları
(`$InstanceStarter`, `$PreviousUser`, `$InstanceBehalfOfStarter`, …) her zaman Evet/Hayır; `allOf`'ta Hayır,
`anyOf`'ta Evet baskın; DENY Evet **veya** Unknown'da, ALLOW yalnız Evet'te tetiklenir. Kimlik:
`$InstanceStarter`/`$PreviousUser` ↔ `act_sub`, behalf-of çifti ↔ `sub` — testler ikisini de açıkça gönderir.
Vaka ALİ (`act_sub=u-ali`) tarafından `c-acme` adına (`sub`) başlatılır, müşteri `customerId=u-veli`.

| Yüzey | Grant seti | Test (`CombinatorGrantTests`) | Beklenen |
|---|---|---|---|
| `checking.approve` — `authorize?transitionKey=approve` | allow `maker`, deny `allOf[maker, $PreviousUser]` | `FourEyes_TheMakerWhoSubmittedMayNotApprove_AnotherMakerMay` | `submit`'i yapan maker AYŞE `false`; başka maker `true`; maker rolüyle başlatan ALİ `true` (başlatan ≠ önceki kullanıcı); rolsüz `false` (deny `Unknown ∧ No = No` tetiklenmez, allow Unknown kabul etmez); approver `false` |
| `draft.queryRoles` — `authorize?queryRoles=true` | allow `allOf[morph-idm.customer, $InstanceStarter]` | `QueryRoles_AllOfCustomerAndStarter_AdmitsOnlyTheStarterWhoIsACustomer` | ALİ (customer + başlatan) `true`; başlatmamış customer `false`; customer olmayan başlatan `false`; rolsüz başlatan `false` (Unknown ∧ Yes) |
| master şema `x-roles` — `data` function | `iban`: allow `anyOf[$InstanceStarter, $InstanceBehalfOfStarter]` + allow `corporate-ops`, `x-masking` keepLast 4 (muaf: `corporate-ops`); `riskNote`: allow `corporate-ops` + deny `allOf[corporate-ops, $InstanceBehalfOfStarter]` | `XRoles_Combinators_PruneAndMaskPerCaller` | ALİ: iban maskeli, riskNote yok · OPS (`sub=c-acme`): iban ham, riskNote yok (deny Yes∧Yes) · başka subject adına ops: iban ham, riskNote **var** · VELİ (rolsüz, `sub=c-acme`): iban maskeli, riskNote yok · ANON: ikisi de yok |
| aynı, önbellekli yol | — | `XRoles_TheCachedDataPathKeysOnTheSubject` | yalnız `sub`'ı farklı iki ops caller aynı önbellek girdisini paylaşmaz (`CallerScopeHash` artık `sub` içerir, K5) |

Rol adları lab'ın `morph-idm.` ad alanında (`morph-idm.customer`, `morph-idm.corporate-ops`); plandaki
`customer-role` / `corporate.ops` örneklerinin karşılığıdır.

**Kapsam dışı bırakılan (d):** `x-masking.roles` içinde combinator'ın publish'te reddedilmesi (K1) bu lab'da
**yazılmadı** — role-matrix-lab'da publish-negatif bir desen yok (bileşenler SDK ile toplu yayınlanır, hiçbir
test `POST api/v1/definitions/publish`'i elle çağırmıyor). Desen `ImplicitStartLab` (`PublishAsync(JsonObject)`)
ve `FanOut` (`FanOutConfigMatrixTests`) içinde var; eklenmesi gerekirse oradan taşınmalı. Kural runtime'da
unit seviyesinde pinli (`SchemaComponentValidator`).

## Neden var

İki geliştirme aynı anda bu fixture'ı doğurdu (2026-08-19, `feature/caller-role-provider`):

1. **Provider bazlı caller-role çözümü.** Rol kaynağı `ICallerRoleResolver` arkasına alındı;
   `default` (mevcut `ICurrentUser.Roles` + `role` header) yanına `morph-idm` provider'ı eklendi.
   Rol setinin **nereden geldiği** değişirken, grant motorunun davranışının **hiç değişmemesi**
   gerekiyor. Bu suite o değişmezliğin ölçüsüdür: provider'ı `morph-idm`'e alıp aynı testleri
   koşturmak, yalnızca kaynağın değiştiğini kanıtlar.

2. **Custom function'lardan rol denetiminin kaldırılması.** `FunctionAccessPolicy` artık yalnız
   `scope` denetler; `function.roles` sadece `authorize` tarafından okunur. Bu ayrım her iki yönde
   de yanlışlıkla "bug" sanılabilir, bu yüzden iki yarısı da
   [`CustomFunctionAuthorizationTests`](CustomFunctionAuthorizationTests.cs) içinde **yan yana**
   pinlenmiştir.

Ayrıca daha önce gerçekten ayrışmış olan iki yüzey burada kalıcı olarak bağlanıyor: `authorize`
bir zamanlar instance'ın current state'ini yok sayıyordu, ve bir yüzey request context'siz
değerlendirme kurduğunda dynamic grant'ler bir tarafta eşleşip diğerinde eşleşemiyordu.

## Akış şeması

```
                    start-role-matrix
                           │  (SeedCaseMapping → caseRef, decisionNote, auditTrail)
                           ▼
                      ┌─────────┐
                      │ intake  │  queryRoles: YOK → root'a düşer
                      └────┬────┘  root: maker ALLOW, approver ALLOW, auditor ALLOW  (viewer ⇒ 403)
                           │  submit-for-review   [maker ALLOW, approver ALLOW, viewer DENY]
                           ▼
                      ┌─────────┐
                      │ review  │  queryRoles: approver ALLOW, auditor ALLOW, maker DENY
                      └────┬────┘  ← state seti ROOT'U EZER: maker başlatabilir ama okuyamaz
          ┌────────────────┼────────────────┬──────────────────┐
          │ approve        │ reject         │ escalate         │ open-review-note
          │ [approver      │ [auditor DENY] │ [$InstanceStarter│ [grant YOK]
          │  ALLOW]        │  = blacklist   │  ALLOW]          │  → herkese açık
          │  = allowlist   │                │  = predefined    │
          ▼                ▼                ▼                  ▼ ($self)
     ┌──────────┐    ┌──────────┐    ┌────────────┐
     │ approved │    │ rejected │    │ escalated  │ queryRoles: auditor ALLOW (tek)
     └──────────┘    └──────────┘    └─────┬──────┘ → approver bile 403
                                           │ resolve-escalation [auditor ALLOW]
                                           ▼
                                      ┌──────────┐
                                      │ approved │
                                      └──────────┘

shared: record-note ($self)
        roles              = maker ALLOW, approver ALLOW
        availableIn        = ["intake", {state:"review", roles:[approver ALLOW]}]
        ⇒ intake'te maker VE approver · review'de YALNIZ approver          ← AND daraltması

well-known: cancel-role-matrix   [maker, approver]   availableIn: intake, review
            update-role-matrix-data [maker]          target: $self
            exit-role-matrix     [auditor]           availableIn: {review, [auditor]}
```

### Kritik adımlar

| Adım | Neden kritik |
|---|---|
| `intake → review` | State `queryRoles`'un root'u **ezdiğini** (birleşmediğini) kanıtlayan tek geçiş. Aynı caller, aynı instance, cevap 200'den 403'e döner. |
| `record-note` / `review` | `availableIn` rol daraltmasının **AND** olduğunu gösterir. OR ya da per-state grant'leri yok sayan bir implementasyonda transition her iki state'te de görünür kalır. |
| `reject` | Deny-only set = **blacklist**. Allowlist gibi yorumlanırsa transition herkes için kaybolur — sessiz ve fark edilmesi zor bir regresyon. |
| `escalate` | `$InstanceStarter` rol string'ine değil **caller kimliğine** bağlıdır. Provider değişiminden sonra bu test kırmızıya dönerse, bozulan kimlik hattıdır, rol hattı değil. |
| `decisionNote` (x-roles) | Alan seviyesinde **DENY kazanır**: maker+approver caller instance'ı okur ama alanı kaybeder. Instance seviyesindeki "bir ALLOW yeter" kuralının tam zıddı. |

## Nasıl çalıştırılır

Ön koşullar: altyapı ayakta (`cd etc/docker && ./run-docker.sh` — vNext çalışma alanında),
migration gerekiyorsa DbMigrator bir kez, ve 4 app `--launch-profile http` ile.
MockLab **gerekmez** — bu senaryoda HTTP task yok, hepsi script task.

```bash
dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~RoleMatrixLab"
```

Lokal runtime'a bağlamak için (geliştirme sırasında doğru yol — container image eski kodu taşır)
`tests/Core.IntegrationTests/test.runsettings` içindeki satırın set olduğunu doğrula (repoda commit'li;
farklı port için git-ignore'lu `test.runsettings.local`):

```xml
<VNEXT_BASE_URL>http://localhost:4201</VNEXT_BASE_URL>
```

Bileşenler değiştiğinde workflow ve function JSON'ları **üretilir**, elle düzenlenmez:

```bash
python3 core/Workflows/role-matrix-lab/build-role-matrix-lab.py && npm run validate
```

## Beklenen sonuç / başarı kriteri

Alan görünürlüğü 46 test yeşil (6 `x-roles` + 9 maskeleme/hash + 31 iç içe veri); paketin tamamı 99 test. Anlamlı olan dördü:

- `Authorize_AgreesWithTheStateFunctionListing_ForEveryTransitionAndRole` — beklenen cevabı
  **kodlamaz**, yalnızca iki yüzeyin ayrışamayacağını doğrular. Suite'in en değerli assertion'ı.
- `InvocationSucceeds_WhileAuthorizeDenies_ForTheSameCaller` — aynı caller, aynı function:
  çalıştırma 200, `authorize` 403. İkisi de doğru.
- `ACallerHoldingBothAnAllowedAndADeniedRole_LosesTheField` — DENY'ın alan seviyesinde kazandığı.
**Şifreleme (`SchemaFieldEncryptionTests`, `x-encryption.type: encrypt`).** Master şema `1.0.3` bir `vault`
nesnesi taşır: `email` (encrypt, auditor için allow-only muafiyet, `format: email`), `pin` (encrypt, muafiyetsiz) ve
`label` (şifresiz). Start transition'ı üçünü de açık yazar; runtime iki şifreli alanı `InstancesData."Data"`
kolonunda `ENCRYPTED:AES256:i1:…` jetonu olarak saklar; instance verisi motorda da bu ham haliyle durur. Anahtar ve tuz instance başına ilk
korumalı yazmada runtime tarafından üretilir ve flow şemasının `InstanceSecrets` tablosunda tutulur (config/Vault
yok; önbellek yalnız süreç içi, Redis'e yazılmaz). Doğrulananlar: data function ve senkron transition yanıtında
auditor `email`'i açık, diğer herkes (maker, approver, rolsüz, yanlış yazılmış rol) saklanan jetonu görür; `pin`'i
herkes jeton görür; instance GET, liste ve senkron yanıt aynı muafiyeti uygular (auditor açık, approver jeton);
header'sız `mirror-self` task okuması çağıranın görüşünü (maker → jeton), `mirror-self-auditor` düz metin alır;
mapping'in kendi script context'i jetonu görür ve `context.Instance.DecryptAsync("vault.email")` ile açar; değişmeyen değer sonraki yazmalarda aynı jetonu korur (ve `format: email`
validasyonu geçmeye devam eder); okunan jetonun aynen geri gönderilmesi no-op'tur; başka instance'ın jetonu ya da düz
alana konan önek 400 `Instance:100041` ile reddedilir; şifreli yola (ve üst nesnesine) filtre 400 döner. Kolonun ve
anahtar satırlarının içeriği testin dışında psql ile doğrulanır:
`SELECT "Data"->'vault', "Data"->>'hashedCustomerNo' FROM role_matrix_lab."InstancesData" WHERE "IsLatest" ORDER BY "EnteredAt" DESC LIMIT 3;`
ve `SELECT count(*) FROM role_matrix_lab."InstanceSecrets";`.

- `AHeaderlessTaskRead_CopiesWhatARoleLessCallerSees` / `ATaskReadWithAnAuditorCredential_CopiesWhatTheAuditorSees_WhoeverDrivesIt`
  — Maker tetikler; task header'sızken rolsüz caller'ın gördüğünü, auditor header'ıyla auditor'ün gördüğünü kopyalar.

**2026-10-01 koşusu (ham veri modeli, master ile birleşik build)** — vnext `feature/field-masking-x-masking`
(`c4cf7c32`): `InstanceData.Data` her yerde DB'deki ham hal, çözme yalnız istendiğinde (`DecryptAsync`, okuma bekçisi,
yazma hunisi), context yüklemesindeki `Instance:100040` kapısı kaldırıldı. Fixture değişmedi (workflow `1.0.8`).
RoleMatrixLab 92/115, 23 kırmızı önceki koşularla aynı küme; alan görünürlüğü/maskeleme/şifreleme sınıflarında kırmızı
yok. Tam paket 332/393 — kalan kırmızılar bilinen kümeler: queryRoles gateway kararı (RoleMatrixLab 16 + AccountOpening
ve ErrorBoundaryLab 403 birer), CS8197 (5), `act_sub` olmadan `$InstanceStarter` (2), partner domain kapalı
(HumanTaskChain 10, CrossDomainLab 14), DataIntegrityLab Busy (2), TaskInvocationLab Dapr metadata (1). psql: bu
koşunun satırlarında `vault.email` / `vault.pin` hep jeton, hash yolları hep özet, düz metin 0; Redis'te anahtar/tuz yok.

**2026-10-01 koşusu (credential seti)** — task'in giden başlık seti artık çağıranın `sub`, `act_sub`, `position`,
`client_id` ve `role`'ünü taşır (workflow `1.0.8`). Header'sız `mirror-self` testleri çağıranın görüşünü doğrular: maker
tetikleyince maske / jeton / `auditTrail` yok, auditor tetikleyince açık değer ve `auditTrail`. Paket 92/115, 23 kırmızı
aynı küme. Ayırt edici: eski kurallı runtime'da (yalnız `sub`/`act_sub`) yeni iki auditor testi kırmızı (90/115).

**2026-10-01 koşusu (Faz B) — script jeton görünümü + `DecryptAsync`** (aynı lokal build, workflow `1.0.7`).
`mirror-self` script'i `context.Instance.Data`'da `vault.email`'i jeton görür (`scriptSawVaultEmail = <token>`), kendi alanını
`await context.Instance.DecryptAsync("vault.email")` ile açar (`decryptedVaultEmail` = düz değer) ve task'in döndürdüğü
jetonu yol diye verince `null` alır (`decryptedTaskValue = <null>`). Paket 90/113, 23 kırmızı Faz A koşusuyla birebir aynı.
Ayırt edici: aynı fixture Faz A runtime'ına karşı 87/113 — `mirror-self` kullanan 3 test orada kırmızı (`DecryptAsync`
yok). Bu koşuda 544 satırın hepsinde `vault.email` jeton, düz metin 0; Redis'te korumalı değer yok.

**2026-10-01 koşusu (Faz 2, yalın) — merkezi okuma bekçisi 60/60 yeşil** (vnext `feature/field-masking-x-masking`
lokal build, commit'siz çalışma ağacı, `run-docker.sh up core`, `http://localhost:4201`, provider `default`; workflow
`1.0.6`). `SchemaFieldVisibilityTests` 6/6, `SchemaFieldMaskingTests` 11/11, `SchemaFieldExposureNestedTests` 32/32,
`SchemaFieldEncryptionTests` 11/11; paketin tamamı 90/113, kalan 23 kırmızı aşağıdaki küme ve Faz 1 build'inde
(`3b22a803`) kırmızı olan 34'ün alt kümesi — yalnız bu build'de kırmızı test yok; Faz 1'de kırmızı olan 11 GET/list/task
testi burada yeşil. Bu koşuda yazılan 275 satırın hepsinde `vault.email` jeton, düz metin 0, başka alana kopyalanmış
jeton 0; Redis'te tanım önbelleği dışında korumalı değer taşıyan anahtar yok.

**2026-09-30 koşusu — komite değişikliği sonrası alan görünürlüğü + şifreleme 55/55 yeşil** (vnext
`feature/field-masking-x-masking` lokal build, `run-docker.sh up core`, `http://localhost:4201`; master şema ve workflow
`1.0.3`). `SchemaFieldVisibilityTests` 6/6, `SchemaFieldMaskingTests` 10/10, `SchemaFieldExposureNestedTests` 29/29,
`SchemaFieldEncryptionTests` 10/10; paketin tamamı 85/108, kalan 23 kırmızı aşağıdaki tablodaki küme (16 queryRoles
gateway kararı, 5 CS8197, 2 `act_sub`). Kanıt (psql, `role_matrix_lab`): 218 `InstanceSecrets` satırı (32 bayt anahtar +
tuz, hepsi farklı, yetim yok); son 512 satırın 512'sinde `vault.email` `ENCRYPTED:AES256:i1:` jetonu, düz e-posta 0
(mirror kopyaları hariç); `hashedCustomerNo` 512/512 `HASHED:SHA256:` özeti, ham değer 0, 218 farklı özet (instance
başına bir); ham telefon hiçbir satırda yok. Redis'te `data-fn` kaydı ve anahtar materyali yok. Anahtar silme senaryosu
elle koşuldu: bir instance'ın `InstanceSecrets` satırı silindi → aynı pod L1'den açmaya devam etti (beklenen); restart
sonrası auditor jetonu gördü, `record-note` sync ve async 503 `Instance:100040`, instance `A`/`intake`, incident/fault yok.
İlk koşuda bulunup düzeltilen runtime kusuru: hash yazarken uygulanınca motor özeti görür; `mirror-self` mapping'inin
`customer.contact.phone` özetini korumasız `mirroredPhone`'a kopyalaması funnel'ın önek reddine takılıp instance'ı
fault'a düşürüyordu — `HASHED:` artık yalnız hash yollarında saklıdır (`ENCRYPTED:` her yerde).

**2026-09-29 koşusu — alan görünürlüğü + şifreleme 56/56 yeşil** (vnext `feature/field-masking-x-masking` lokal
build, `run-docker.sh up core`). `SchemaFieldEncryptionTests` 10/10; paketin tamamı 86/109, kalan 23 kırmızı
2026-09-28'dekilerle birebir aynı küme (aşağıdaki tablo). Kanıt: `role_matrix_lab."InstancesData"`'da `vault`
taşıyan 360 satırın 360'ında `vault.email` `ENCRYPTED:AES256:local:` jetonu, düz metin 0; düz e-posta yalnız
`mirror-self`'in bilerek korumasız alanlara yaptığı 12 kopyada. Değişmeyen değer sonraki satırlara aynı jetonla
taşınır. Redis'te `data-fn:v3` kaydı yok (jeton taşıyan satır önbelleğe alınmaz). Anahtar eksik senaryosu elle
koşuldu: orchestration `local` anahtarı olmadan başlatılınca auditor jetonu görür, `record-note` sync ve async 503
`Instance:100040` döner, instance `A`/`intake` kalır (fault/incident yok); anahtar geri gelince aynı çağrı 200.
Bu koşuda bulunup düzeltilen runtime kusurları: önekli değer reddi pipeline içinde fırlayıp instance'ı fault'a
düşürüyordu (şimdi admission öncesi 400); şema filtre reddi 500 dönüyordu (şimdi 400).

**2026-09-28 koşusu — alan görünürlüğü 46/46 yeşil** (vnext `feature/field-masking-x-masking` lokal build,
`run-docker.sh up core`, `http://localhost:4201`). `SchemaFieldVisibilityTests` 6/6, `SchemaFieldMaskingTests`
9/9, `SchemaFieldExposureNestedTests` 31/31. Paketin tamamı 76/99; kalan 23 kırmızının hiçbiri alan
görünürlüğüyle ilgili değil:

| Küme | Test | Neden |
|---|---|---|
| `QueryRoleGateTests` | 15 | Runtime `queryRoles`'u okuma yüzeylerinde artık zorlamıyor (kapı gateway'de, `authorize?queryRoles=true`); ayrıca `view` intake'te 404 (fixture'da yalnız review view'ı var), `schema` `transitionKey` olmadan 400 |
| `AuthorizeFunctionTests.Authorize_QueryRoles_MatchesWhatTheReadFunctionsDo` | 1 | Aynı neden: okuma fonksiyonu 200 dönüyor, authorize reddediyor — tasarım gereği |
| `CustomFunctionAuthorizationTests` | 5 | `RoleMatrixSummaryMapping.csx` derlenmiyor: `(69,66) CS8197` (ilk commit'ten beri) |
| `Escalate_IsOfferedToTheInstanceStarter`, `Authorize_DeniesATransitionThatIsNotAvailableInTheCurrentState` | 2 | `$InstanceStarter` aktör kimliğini `act_sub` başlığından eşler; test tabanı yalnız `user_reference` gönderiyor, kimse "başlatan" olmuyor |

Bu koşuda düzeltilen iki test kusuru: `GetDataAttributesAsync` data function'ın `{ "data": {…} }` zarfını
açmıyordu (eski `SchemaFieldVisibilityTests` kırmızısının nedeni); fixture değişikliği için workflow ve
master şema `1.0.1`'e çekildi (publish sürüm-değişmez). Kanıt: Postgres'te saklanan `customer.tckn` /
`customer.contact.phone` açık, `mirroredPhone` ham kopya; Redis `data-fn:v2-s68e26ec2:*` kayıtları çağıran
kapsamı başına doğru gövdeyi taşıyor (auditor kapsamı açık `tckn` + `riskScore`, maker kapsamı `contact` /
`segment` yok). OpenObserve MCP bu oturumda bağlanamadı; süre/span kanıtı alınmadı.

### Bilinen kısıtlar

- **Combinator'lı akış `npm run validate`'ten geçmiyor (2026-10-03).** Kurulu `@burgan-tech/vnext-schema` (0.0.52)
  grant'te `role`'ü zorunlu tutup `allOf`/`anyOf`'u ek alan sayıyor:
  `core/Workflows/role-matrix-lab/role-matrix-lab-combinators.json` → `/attributes/states/0/queryRoles/0` ve
  `/attributes/states/1/transitions/0/roles/1`: *must have required property "role"* + *must NOT have additional
  property "allOf"*. Şema dosyası (`role-matrix-combinator-master.json`) geçiyor — `x-roles` içeriği şemayla
  doğrulanmıyor. SDK bileşenleri doğrudan publish eder; runtime bu şekli kabul eder. vnext-schema combinator'ları
  yayınlayınca bu madde düşer.
- **Combinator testleri henüz koşulmadı** (infra kapalıydı; `dotnet build` yeşil). Koşu: `run-docker.sh up core`
  (runtime `feature/role-grant-combinators` lokal build) → `init` sistem paketi → `wf domain use core && wf sync`
  → `dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~RoleMatrixLab.CombinatorGrantTests"`
  (`VNEXT_BASE_URL=http://localhost:4201`).

- **Hash özeti hesaplanamaz.** Tuz instance'a özgüdür ve runtime dışına çıkmaz; testler yalnız `HASHED:SHA256:` önekini,
  uzunluğu, kararlılığı ve instance'lar arası farkı doğrular.
- **Maskeleme runtime'ı gerektirir.** `SchemaFieldMaskingTests` yalnız `x-masking` uygulayan (vnext
  `feature/field-masking-x-masking`) yerelde derlenmiş runtime'a karşı yeşildir; önceki runtime anahtar
  kelimeyi yok sayar ve değerleri açık döner.
- **Şifreleme yalnız instance data'yı kapsar.** `InstanceTransition.Body`, task kayıtları, outbox ve job
  payload'ları düz metindir (Faz 2); `mirror-self`'in kopyaladığı `scriptSawVaultEmail`
  bilerek korumasız alanlardır — kopyalanan değer şifresini kaybeder.
- **Şifreleme runtime'ı ve migration'ı gerektirir.** `SchemaFieldEncryptionTests` `encrypt` uygulayan yerel build'e ve
  `InstanceSecrets` migration'ı uygulanmış şemaya karşı yeşildir; `SchemaEncryption:EncryptWrites=false` host
  `encrypt`/`hash` şemasının publish'ini reddeder.
- **Task header'sızsa çağıranı adına okur.** Başka bir kimlikle okumak için credential'ı input mapping'de verin; mapping
  değeri kazanır (Faz 2, `SystemRead` kaldırıldı).
- **Liste yalnız son veri satırını yükler**: liste extension'ı `DataList`'te tek satır görür.
- **Aynı sürümle yeniden publish ETag'i oynatmaz** (bilinen açık, `vnext-meta/known-issues.json`
  `masked-field-same-version-republish-stale-etag`): maskeleme kuralını değiştirince şema sürümünü artırın.

- **`transition.roles` çalıştırma anında zorlanmaz** (tasarım kararı). Bu yüzden hiçbir test
  "rolü olmayan caller transition'ı çalıştıramaz" demez — sadece **gösterilmediğini** ve
  `authorize`'ın **reddettiğini** doğrular.
- **Python yük testi yok, bilinçli.** Bu senaryo eşzamanlılık değil doğruluk ölçer; yetkilendirme
  kararları instance state'ine ve caller kimliğine bağlıdır, yüke değil. Yük altında ölçülecek
  bir şey çıkarsa (ör. morph-idm provider'ının request başına tek çağrı garantisi) `api-tests/`
  altına o zaman eklenir.
- **morph-idm provider'ı bu suite ile henüz koşulmadı.** Provider `default` iken yazıldı;
  Aether'a `ICurrentUser.Position` eklendikten sonra `CallerRoleProvider:Provider = "morph-idm"`
  ile tekrar koşulması gerekir — asıl doğrulama odur.
