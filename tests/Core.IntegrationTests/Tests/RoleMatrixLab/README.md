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
`SchemaFieldMaskingTests` bunları data function, ETag/304 ve caller kapsamı üzerinden doğrular; **instance GET/list
veriyi saklandığı gibi döner** (maskeleme ve `x-roles` yok — komite kararı, Faz 2). `mirror-self` shared transition'ı
bir GetInstanceData (type 13) task'iyle **kendi** verisini okuyup korumalı alanları korumasız alanlara kopyalar:
trigger task okuması sistem kimliğiyle yapılır (runtime'ın `SystemRead` bayrağı), kopyalar **saklanan** değeri taşımalıdır.
Master runtime'da (bayraktan önce) `mirroredAuditTrail` `<absent>` döner — kırmızı taban çizgisi budur.

**İç içe veri (`SchemaFieldExposureNestedTests`).** Yalnız primitif alanlarla mutlu yol yeterli değil: `customer`
nesnesinin altında `x-roles` ALLOW yaprağı (`segment`), `x-roles` DENY alt ağacı (`contact`, maker hariç herkes;
rolsüz çağıran da göremez), bu alt ağacın içinde maske (`email`), hash (`phone`) ve iki seviye aşağıda ALLOW
yaprağı (`address.line1`) var; ayrıca bütün olarak korunan nesne dizisi (`accounts`), korunan sayı
(`riskScore`), `$InstanceStarter` (`ownerNote`, `act_sub` ile) ve korumasız string dizisi (`tags`). Her ALLOW
alanı izinli, izinsiz, izinli+reddedilen ve rolsüz çağıranla ayrı ayrı doğrulanır; gizlenen alt ağacın hiçbir
çocuğunun hiçbir biçimi (açık, maskeli, hash) data function gövdesinde görünmemelidir. Data function ve senkron
transition yanıtı aynı ağacı döndürür; instance GET her çağırana saklanan ağacı (budamasız, maskesiz; `phone` özet) döner.

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
kolonunda `ENCRYPTED:AES256:i1:…` jetonu olarak saklar, motor düz metin görür. Anahtar ve tuz instance başına ilk
korumalı yazmada runtime tarafından üretilir ve flow şemasının `InstanceSecrets` tablosunda tutulur (config/Vault
yok; önbellek yalnız süreç içi, Redis'e yazılmaz). Doğrulananlar: data function ve senkron transition yanıtında
auditor `email`'i açık, diğer herkes (maker, approver, rolsüz, yanlış yazılmış rol) saklanan jetonu görür; `pin`'i
herkes jeton görür; instance GET ve liste herkese jetonu döner; `mirror-self`'in sistem okuması ve mapping'in kendi
script context'i düz metni görür; değişmeyen değer sonraki yazmalarda aynı jetonu korur (ve `format: email`
validasyonu geçmeye devam eder); okunan jetonun aynen geri gönderilmesi no-op'tur; başka instance'ın jetonu ya da düz
alana konan önek 400 `Instance:100041` ile reddedilir; şifreli yola (ve üst nesnesine) filtre 400 döner. Kolonun ve
anahtar satırlarının içeriği testin dışında psql ile doğrulanır:
`SELECT "Data"->'vault', "Data"->>'hashedCustomerNo' FROM role_matrix_lab."InstancesData" WHERE "IsLatest" ORDER BY "EnteredAt" DESC LIMIT 3;`
ve `SELECT count(*) FROM role_matrix_lab."InstanceSecrets";`.

- `ATriggerTaskRead_CopiesTheStoredValues_WhateverTheCallerMaySee` — maskeli/budanmış alanı
  göremeyen Maker'ın tetiklediği GetInstanceData task'i bile saklanan değeri kopyalar.

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

- **Hash özeti hesaplanamaz.** Tuz instance'a özgüdür ve runtime dışına çıkmaz; testler yalnız `HASHED:SHA256:` önekini,
  uzunluğu, kararlılığı ve instance'lar arası farkı doğrular.
- **Maskeleme runtime'ı gerektirir.** `SchemaFieldMaskingTests` yalnız `x-masking` uygulayan (vnext
  `feature/field-masking-x-masking`) yerelde derlenmiş runtime'a karşı yeşildir; önceki runtime anahtar
  kelimeyi yok sayar ve değerleri açık döner.
- **Şifreleme yalnız instance data'yı kapsar.** `InstanceTransition.Body`, task kayıtları, outbox ve job
  payload'ları düz metindir (Faz 2); `mirror-self`'in kopyaladığı `mirroredVaultEmail` / `scriptSawVaultEmail`
  bilerek korumasız alanlardır — kopyalanan değer şifresini kaybeder.
- **Şifreleme runtime'ı ve migration'ı gerektirir.** `SchemaFieldEncryptionTests` `encrypt` uygulayan yerel build'e ve
  `InstanceSecrets` migration'ı uygulanmış şemaya karşı yeşildir; `SchemaEncryption:EncryptWrites=false` host
  `encrypt`/`hash` şemasının publish'ini reddeder.
- **Instance GET/list alan korumasını uygulamaz** (komite kararı, Faz 2): `x-roles` ile gizlenen ve maskelenen alanlar
  orada açık görünür — bu testler bunu bilerek pinler.
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
