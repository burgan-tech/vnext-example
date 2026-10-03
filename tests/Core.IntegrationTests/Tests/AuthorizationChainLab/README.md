# authorization-chain-lab

## Ne kontrol ediyor

`authorize` fonksiyonunun bir **aktif korelasyon zinciri** boyunca verdiği cevabın, okuma
yüzeylerinin fiilen uyguladığı kapıyla aynı olduğunu — ve parent'ın subflow override'larının
**hop başına**, çocuğa damgalanmış haritadan çözüldüğünü.

> **2026-10-03 — K9: karar yalnız leaf'te (role-grant combinators, vnext `feature/role-grant-combinators`).**
> `authorize?queryRoles=true` artık zincirin **konjonksiyonu değil**: aktif SubFlow'u olan bir instance
> için cevap **en derin aktif leaf'in** verdiktidir — `damgalı parent override ?? leaf state queryRoles ??
> leaf workflow queryRoles`. Üst seviyeler AND'lenmez: kökün `queryRoles`'u instance SubFlow'dayken kimseyi
> kısıtlamaz, `queryRoles`'u boş leaf **izin verir**. Parent leaf'i kısıtlamak istiyorsa bunu
> `subFlow.overrides.states.<state>.queryRoles` ile leaf'e damgalar. Parent'ta cevaplanan transition'lar ve
> `?ack=true` **değişmedi**. Aşağıdaki "Neden var" bölümündeki (1) maddesi bu yüzden **tarihçedir**: o gün
> doğru olan kusur tespitiydi (`authorize` kökün grant'larını hiç değerlendirmiyordu), konjonksiyon ise o
> günün okuma yolunu (kökte kapı + leaf'te kapı) taklit ediyordu; okuma yolu 2026-09-23'te `queryRoles`
> uygulamayı bıraktığından konjonksiyonun taklit ettiği bir şey kalmadı. Testlerin eski→yeni beklentisi
> "Koşu kaydı"nın 2026-10-03 girdisinde.

## Neden var

2026-09-22 konseyi (`DECISION-2026-09-22-remove-execution-authorization`) `authorize`'da üç canlı
kusur buldu. Üçü de aynı sonuca çıkıyordu: **ara katmanın danıştığı cevap, runtime'ın uyguladığı
kapıdan farklıydı.**

1. **Konjonksiyon yoktu.** `authorize?queryRoles=true` aktif subflow'a kısa devre yapıp poll edilen
   instance'ın kendi `queryRoles`'unu **hiç değerlendirmiyordu**. State function ise kökte gate'leyip
   sonra iniyor ve leaf'te tekrar gate'leniyor — iki konjonktif kapı. Yani `authorize`, tarif etmesi
   gereken kapıdan **zayıftı**: ona güvenen bir gateway, runtime'ın reddettiği okumayı kabul ederdi.
2. **Override eşleşince iniş duruyordu.** Parent'ın tanımındaki override derinlik 1'de `return`
   ediyordu, dolayısıyla torunun kendi kapısı hiç çalışmıyordu.
3. **Override yanlış yerden okunuyordu.** Parent'ın tanımından okumak, **doğrudan adreslenen** bir
   leaf'te (parent kapsamda değilken) hiçbir şey bulamıyor ve çocuğun kendi grant'larına düşüyordu —
   aynı instance için `authorize` ile state function **zıt** verdikt veriyordu.

Ayrıca bu suite, aynı değişiklikle gelen iki şeyi sabitliyor: `authorize`'ın dördüncü hedefi
**`ack`** (long-poll acknowledge ön-kontrolü, ara katmanın başka türlü soramadığı tek
durum-değiştiren yüzey) ve runtime'ın kendi `queryRoles` / ack **kapılarının kaldırılmış olması** —
o karar artık Internal Gateway'in, `authorize`'a sorarak verdiği karardır.

## Neden ayrı bir akış

`subflow-orchestration` zaten A→B→C bir zincir ve A'nın B için bir `overrides.states` girdisi var.
Ama o suite **yeşil** ve testleri rol header'ı göndermeden okuyor; ara/yaprak seviyelere `queryRoles`
eklemek onu kırardı. Bu lab aynı zincir şekline sahip bağımsız bir kopya.

## Zincir ve grant tasarımı

```
ROOT (F)  waiting --subFlow--> MID (S)  mid-waiting --subFlow--> LEAF (S)  leaf-waiting
```

| Seviye | Kendi `queryRoles` | Üstündeki override |
|---|---|---|
| ROOT | `chain.reader`, `chain.admin` | — |
| MID | `chain.reader`, `chain.admin`, `chain.mid-only` | ROOT → `chain.admin` |
| LEAF | `chain.reader`, `chain.admin`, `chain.leaf-only` | MID → `chain.leaf-admin` |

Her rol **tam olarak bir seviyede** düşecek şekilde seçildi; böylece yanlış bir verdikt kendi
sebebini söylüyor:

| Rol | ROOT | ROOT→MID override | MID→LEAF override |
|---|---|---|---|
| `chain.reader` | ✅ | ❌ | — |
| `chain.admin` | ✅ | ✅ | ❌ |
| `chain.leaf-admin` | ❌ | — | ✅ |
| `chain.mid-only` | ❌ | — | — (yalnız `root-plain` üzerinden görünür) |

**K9 sonrası okuma:** zincirde kararı yalnız son sütun (MID→LEAF override = `chain.leaf-admin`,
`chain.admin`) verir. `chain.reader` kökte **yine reddedilir** (leaf onu kabul etmiyor), `chain.leaf-admin`
ise kökte ve mid'de **artık kabul edilir** (eskiden kökün allowlist'i reddediyordu).

`authorization-chain-lab-root-open` → `authorization-chain-lab-leaf-open` (2026-10-03) iki seviyeli bir çift:
kökün kendi `queryRoles`'u yalnız `chain.admin`, leaf'in state'inde ve workflow'unda hiç `queryRoles` yok,
override da yok. "Kök dar + leaf boş" şeklidir: K9 ile kökte `chain.reader` ve rolsüz caller da **izinli**.

`authorization-chain-lab-root-plain` aynı MID'i **override bildirmeden** başlatır: "override yoksa
nesnenin kendi tanımı uygulanır" yarısının kontrol grubu. Onsuz REPLACE iddiası tek yönlü kalırdı.

**Kritik adım:** zincir otomatik kurulur (her seviyenin initial state'inde `triggerType: 1` bir hop).
Test yalnızca root'u başlatır ve korelasyon haritası iki seviye derinleşene kadar bekler.

## Nasıl koşulur

Ön koşullar: infra + runtime ayakta (`etc/docker/run-docker.sh up core`), `VNEXT_BASE_URL`
`test.runsettings` içinde doğru. MockLab **gerekmez** — bu lab HTTP task çağırmıyor.

```bash
cd vnext-example
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~AuthorizationChainLab" -v minimal
```

### Kapılar kaldırıldı — ölçülen şey artık "reddetmiyor"

Runtime, `queryRoles`'u okuma yüzeylerinde (`state`, `data`, `view`, `schema`, `master`, `tasks`,
`actions`, `incidents`, `incidents/active`) ve `interaction.longPoll` kapısını `POST .../longpoll/ack`
üzerinde **artık uygulamıyor**. `EnforcementPostureTests` bunu yüzey yüzey doğruluyor: hiçbiri 403
dönmüyor ve hiçbiri kapıya sormuyor.

**`queryRoles` kaldırılmadı, yeri değişti.** Hâlâ değerlendiriliyor — ama `GET .../functions/authorize?queryRoles=true`
tarafından (2026-10-03'ten beri, K9, aktif korelasyon zincirinin en derin leaf'inde). Silinen şey,
runtime'ın aynı kararın **ikinci** kopyası. Bu suite'in geri kalan 40 testi zaten o cevabın doğruluğunu
ölçüyor; bu bölüm yalnızca ikinci kopyanın gitmiş olduğunu ölçüyor.

Bu yüzden `AuthorizeKeepsRefusing` buradaki en önemli satır: bir kaldırmanın bir yüzey fazla
gitmiş olduğunu diff'te görmek zordur, ve `authorize` reddetmeyi bıraksaydı gateway her şeye "evet"
diyen bir kâhine danışıyor olurdu.

## Geçme kriteri

45 testin tamamı yeşil (2026-10-03'ten itibaren; önceden 44). En ayırt edici dördü:

- `ChainLeafDecisionTests.ARoleTheRootRefusesIsAllowedWhileTheLeafAdmitsIt` — kökün grant'ları hâlâ
  AND'leniyorsa **yeşil olamaz** (K9'un "kök deny + leaf allow" vakası).
- `ChainLeafDecisionTests.ALeafWithNoQueryRolesAllowsWhateverTheRootDeclares` — kökün allowlist'i hâlâ
  karar veriyorsa ya da boş küme izin vermiyorsa kırmızı (K9'un "kök dar + leaf boş" vakası).
- `ChainOverrideTests.AnAncestorsOverrideDoesNotReachTheGrandchild` — override aşağı taşınırsa kırmızı.
- `ChainOverrideTests.ADirectlyAddressedLeafAnswersTheSameAsItsStateFunction` — damgalı harita
  yerine parent-taraflı okuyucu kullanılırsa kırmızı.

## Koşu kaydı

**2026-10-03 — K9 (leaf-only `queryRoles`): yazıldı, henüz koşulmadı** (runtime branch
`feature/role-grant-combinators`; infra kapalıydı, `dotnet build` yeşil). `ChainConjunctionTests`
→ `ChainLeafDecisionTests` (dosya da yeniden adlandırıldı). Eski → yeni beklenti:

| Eski test | Yeni test | Eski beklenti | Yeni beklenti |
|---|---|---|---|
| `ReaderPassesTheRootAndFailsTheChain` | `TheRootsOwnAllowDoesNotAdmitWhatTheLeafRefuses` | kökte `false` (konjonksiyon) | kökte `false` (leaf reddediyor — sebep değişti) |
| `ARoleThatPassesEveryLevelIsAllowedEverywhere` | `TheLeafsAdmittedRoleIsAllowedAtEveryLevel` | root/mid/leaf `true` | aynı |
| `ALeafOnlyRoleIsRefusedAtTheRoot` | `ARoleTheRootRefusesIsAllowedWhileTheLeafAdmitsIt` | `chain.leaf-admin` kökte `false` | kökte **`true`**, mid'de **`true`**, leaf'te `true` |
| — | `ALeafWithNoQueryRolesAllowsWhateverTheRootDeclares` (yeni, `root-open`/`leaf-open`) | (konjonksiyonla `chain.reader`/rolsüz kökte `false` olurdu) | admin, reader, rolsüz kökte **`true`**; leaf doğrudan `true` |
| `AuthorizeAgreesWithTheStateFunctionWhereBothResolveTheSameGrants` | `TheStateFunctionServesWhateverAuthorizeAnswers` (fix round 1: ad, kapılar kalktığından beri ölçülmeyen "agreement"ı iddia ediyordu) | state 403 değil | aynı |
| `ARoleLessCallerIsRefused` | aynı ad | — | değişmedi |
| `MorphIdmProviderTests.TheConjunctionStillHoldsOnProviderSuppliedRoles` | `TheLeafDecidesOnProviderSuppliedRoles` | `false` | `false` (sebep: leaf'in damgalı override'ı) |

`ChainOverrideTests`, `AckAndParentRetainedTests`, `DataDescentAsymmetryTests`, `EnforcementPostureTests`
değişmedi (yalnız yorumlar): hepsi ya doğrudan adreslenen leaf'te ya da parent'ta cevaplanan hedeflerde
(`transitionKey` parent-retained, `ack`) çalışıyor, K9 onları etkilemiyor. Fixture: mevcut akışlar birebir
aynı üretiliyor (sürüm `1.0.1` kaldı); yalnız iki **yeni** anahtar eklendi (`root-open`, `leaf-open`).

**2026-09-23 — 44/44.** Runtime lokal build (`claude/remove-authorize-checks-cc40e5`, master tabanı
`f7a053c8`), `VNEXT_BASE_URL=http://localhost:4201`.

Suite önce bir **geçiş anahtarı** (`Workflow:Authorization:EnforceInProcess`) karşısında yazıldı ve
iki duruşta da koşuldu; sonra kullanıcı kararıyla kapılar **tamamen kaldırıldı** ve suite bu son hale
göre yeniden yazılıp tekrar koşuldu. Anahtarı ölçen testler, kaldırmayı ölçenlere dönüştü — artık
ortam değişkeni yok, tek duruş var.

İlk koşu yeşil değildi ve bu laboratuvarın var olma sebebi o: **üç runtime kusuru buldu, üçü de
`authorize`'ı ara katmanın güvenemeyeceği hale getiriyordu.**

1. **`queryRoles` kapısı `EffectiveState` okuyordu.** Kendi subflow'u olan bir ara seviyede bu bir
   TORUNUN state anahtarıdır; parent'ın workflow tanımı onu çözemez, çocuğa damgalanmış override
   (parent'ın beyan ettiği state ile anahtarlanmıştır) eşleşemez, ve kapı sessizce workflow kökünün
   grant'larına düşer. Ölçüldü: mid'de `CurrentState=mid-waiting` / `EffectiveState=leaf-waiting`,
   kök `chain.mid-only`'yi `chain.admin`'e daraltmışken o rol mid'i **200** okudu. Yani yazılmış bir
   kısıt, çocuk kendi subflow'unu açtığı anda — hatasız, logsuz — uygulanmayı bırakıyordu.
2. **State transition'ları `availableIn` üzerinden state'e bakılıyordu.** O liste state
   transition'larında boştur ve "boş = her state" kuralı onlar için yanlıştır: `review`'de tanımlı
   `approve`, instance `intake`'te otururken de allowed görünüyordu. Execution bunu `Transition:100021`
   ile reddediyor — yani oracle **permissive** yönde yanlıştı, ki ara katman onun cevabıyla kapı
   açtığında önemli olan yön budur.
3. **`interaction` bloğu state'in BEYANINA göre yayınlanıyordu**, gerçekten bir ack beklenip
   beklenmediğine göre değil. Endpoint bekleyen yokken idempotent `Ok()` döndüğü için hiçbir şey
   gürültülü kırılmıyordu; istemci o state'in her poll'ünde bir ack atıp başarı okuyordu.
   `ResponseShapeVersion` v10→v11.

**2026-09-25 — `?role=` parametresi de header gibi davranır: 9/9** (aynı branch, `morph-idm`).
`authorize`'ın `role` query parametresi artık istekte `role` header'ı yoksa **header olarak**
resolver'a veriliyor (`RoleParameterMode.AsRoleHeader`): rol kümesi `[X]` olur, morph-idm'e gidilmez;
gerçek header parametreye üstün gelir; `ack` dahil her hedefte. `TheRoleQueryParameterDoesNotSurvive…`
ters çevrilip `TheRoleQueryParameterBehavesLikeTheRoleHeader` oldu, `ARealRoleHeaderWinsOverTheRoleQueryParameter`
eklendi. Kanıt: MockLab sayacı — `?role=maker` ile header'sız istek **0** `get-roles` çağrısı, parametresiz
istek 1 çağrı. `default` provider'da davranış değişmedi; yetki suite'leri yine master'daki 21 bilinen
kırmızıyla birebir.

**2026-09-25 — `role` header'ı önceliklidir: 8/8** (runtime lokal build, branch
`feature/morph-idm-header-role-precedence`, `CallerRoleProvider__Provider=morph-idm`). Komite kararı:
istekte boş olmayan bir `role` header'ı varsa o roller çağıranın kümesidir ve morph-idm'e **hiç
sorulmaz**; header yoksa eskisi gibi morph-idm'e sorulur (hata/boş cevap → boş küme). Birleştirme
yok — header servisin cevabının yerine geçer. `?role=` query parametresi bu kararın kapsamında değil,
morph-idm altında hâlâ yok sayılıyor. Testlerde: `AnAssertedHeaderRoleDoesNotSurviveAnEmptyProviderAnswer`
ters çevrilip `ARoleHeaderDecides_AndMorphIdmIsNotAsked` oldu, `ARoleHeaderDecides_EvenWhenMorphIdmWouldFail`
eklendi, kesinti testi ve okuma yüzeyi testi header'sız isteğe çekildi (header'lı istek artık provider'ı
ölçmez). Kanıt: OpenObserve'de `Auth.ResolveRoles` `outcome=header` span'leri (`idm-broken` için 3
`header` + 3 `failed/http_status`); MockLab `_admin/logs`'ta `idm-broken` için yalnızca 6 `get-roles`
çağrısı (3 header'sız istek × 1 retry) ve hiçbir çağrıda `role` header'ı yok. Aynı runtime `default`
provider'la yetki suite'leri: 93 geçti / 30 kırmızı (21 benzersiz test) — master'daki bilinen
kırmızılarla birebir aynı (`RoleMatrixLab` 19, `AccountOpening` 1, `ErrorBoundaryLab` 1).

**2026-09-25 — provider hatası artık boş küme: 7/7** (runtime lokal build, branch
`feature/role-resolution-fail-open-deny-closed`, `CallerRoleProvider__Provider=morph-idm`). morph-idm'in
hiçbir hatası isteği kırmıyor: 4xx/5xx, timeout, bağlantı hatası, parse edilemeyen gövde, `204` /
`roles: []` hepsi **boş rol kümesi**; `act_sub` ve `client_id` ikisi de yoksa runtime morph-idm'e hiç
sormuyor. Bunu güvenli yapan şart aynı değişiklikte: **rolsüz çağıran, role-bound (statik rol / `$role.`)
bir deny'ı geçemez** — aksi halde kesinti her blacklist'i açardı. `AnUnreachableProviderRefusesRather…`
testi `AnUnreachableProviderIsEvaluatedAsNoRoles_AndTheAllowlistRefusesIt` oldu: `authorize?queryRoles=true`
yine 403 ama gövde `{"allowed":false}` (bir **karar**, `Authorization:110004` hata gövdesi değil), aynı
çağıran için state function **200**. `As()` artık `sub` ile birlikte `act_sub` da gönderiyor — göndermeseydi
yeni ön koşul yüzünden MockLab seed'ine hiç ulaşılmazdı. Kanıt: host log'unda `idm-broken` için
`fail … [20442] FailureKind=http_status, StatusCode=500`, `idm-empty` için `warn … [20441]
EmptyReason=no_content`; OpenObserve'de `Auth.ResolveRoles` span'leri `outcome=failed` + `failure_kind=http_status`
(status ERROR), `outcome=empty` + `empty_reason=no_content` (UNSET), memo-hit span'leri aynı tag'lerle.

**2026-09-23 — `morph-idm` provider'ı: 7/7.** Orchestration host'u
`CallerRoleProvider__Provider=morph-idm` + MockLab (`morph-idm-roles-collection.json`) ile üçüncü kez
başlatıldı; sonra varsayılan provider'a geri alınıp 44'lük suite tekrar koşuldu (44 geçti, 5 atlandı
— provider testleri kendilerini `VNEXT_CALLER_ROLE_PROVIDER` ile kapatıyor, çünkü `default` altında
sessizce yeşil olmak hiç koşmamaktan kötüdür). Kapılar kaldırıldıktan sonra tekrarlandı: 5/5.

**Bu koşu bir açık daha buldu — okuyarak değil, prob atarak.** Header kanalı kapatılıp yeşile
alındıktan sonra aynı etkinin **`role` query parametresinden** geçtiği görüldü: `authorize`, provider
boş küme döndüğünde parametreye düşüyordu ve bunu **hangi** provider'ın döndürdüğüne bakmıyordu. Yani
morph-idm'in `204`'ü ("bu çağıranın operasyonu yok"), çağıranın query string'e kendi rolünü yazmasıyla
eziliyordu. Aynı instance, aynı çağıran:

```
?queryRoles=true                  ->  {"allowed":false}  403
?queryRoles=true&role=chain.admin ->  {"allowed":true}   200     ← düzeltmeden önce
```

`authorize` tek yetki noktası olduğundan bu, istemcinin query string'ini geçiren bir gateway'in
istemcinin kendi beyanına göre kapı açması demekti. Çözüm resolver üzerinde bir yetenek
(`ICallerRoleResolver.AllowsRoleParameterFallback`): kaynağı zaten çağıranın kendi beyanı olan
provider'da (`default`) parametre geçerli, otorite olan provider'da (`morph-idm`) tamamen yok sayılıyor —
`authorize` içinde provider adı kontrolü değil, çünkü yeni bir provider cevabı miras almak yerine
kendi cevabını beyan etmeli. `TheRoleQueryParameterDoesNotSurviveAnEmptyProviderAnswer` ve
`TheRoleQueryParameterDoesNotReachTheAckPreflightEither` bunu sabitliyor.

Kaldırma, bu setteki bir testin **önermesini** yok etti: "morph-idm boş küme dönen çağrıyı state
fonksiyonu 403'ler" artık doğru değil, okuma her hâlükârda servis ediliyor. Yerine gözlenebilir kalan
şey konuldu — rol **çözümlemesi**: aynı instance'ta, header'ında `chain.admin` yazan ama morph-idm'in
`204` dediği çağrıya `record-note` **teklif edilmiyor**, hiç role header'ı olmayıp morph-idm'in
`chain.admin` dediği çağrıya **ediliyor**. Okuma yolu ile `authorize` aynı çağıranı tarif etmezse
gateway'in verdikti yanlış isteğe iliştirilmiş olurdu; ölçülen budur.

Bu koşunun ilk denemesi de kırmızıydı, ama kusur **testin kendisindeydi**, runtime'da değil: kimlik
header'ı `user_reference` sanılmıştı; resolver `AetherClaimTypes.UserName` gönderir, o da **`sub`**'tır.
MockLab'in istek günlüğü bunu doğrudan gösterdi — giden istekte hiçbir kimlik header'ı yoktu, mock da
anahtarsız varsayılan cevabı döndü. Kayda değer yan gözlem: kimlik header'ı olmayan bir çağrıda
runtime yine de morph-idm'e **anonim** olarak soruyor; bu bir kusur değil (fallback zinciri öyle
tasarlanmış) ama provider tarafının o durumu ne döndürdüğü dağıtım başına kararlaştırılmalı.

## Bilinen sınırlar

- **`interaction.longPoll.rule` kolu kapsanmıyor, çünkü AUTHORLANAMIYOR.** Rule kolu runtime'a
  issue #936 ile (0.0.92) girdi ama `@burgan-tech/vnext-schema` özelliği hiç almadı: kurulu 0.0.52
  **ve** sibling repodaki 1.0.0, `longPoll` için `required: [terminate, roles]` +
  `additionalProperties: false` diyor. Rule kollu bir state `npm run validate`'den geçmiyor, yani
  hiçbir domain ekibi yazamıyor. `vnext-meta/features.json` bunun tersini iddia ediyor ("the
  vnext-schema longPoll contract enforces exactly one of roles|rule at authoring time") — **bu iddia
  yanlış**. Ack'in rule-kolu pariteси şema özelliği gönderilene kadar unit testlerde kalıyor.
- **K9 suite'i henüz koşulmadı** (2026-10-03). Koşmak için: `cd ../vnext/etc/docker && ./run-docker.sh up core`
  (runtime `feature/role-grant-combinators` lokal build), `init` üzerinden sistem paketi
  (`curl -X POST localhost:3005/api/package/runtime/publish -H 'content-type: application/json' -d '{"appDomain":"core"}'`,
  status URL'sini tamamlanana kadar izle), sonra bu repoda `wf domain use core && wf domain active && wf sync`
  ve yukarıdaki `dotnet test … --filter "FullyQualifiedName~AuthorizationChainLab"` (`VNEXT_BASE_URL=http://localhost:4201`).
- **`morph-idm` provider'ı ile yalnız `MorphIdmProviderTests` koşuyor**, 44'ün tamamı değil — ve bu
  bilinçli. Provider başlangıçta bir kez seçilir, istek başına değişmez; diğer 44 test rolleri
  `x-roles` ile ayrıştırıyor, o header ise bu provider altında kararı vermiyor, dolayısıyla aynı
  suite'i ikinci provider altında koşmak yalnızca her çağrıyı aynı varsayılan role kümesine
  indirgerdi. Leaf kararı (K9; 2026-10-03 öncesi konjonksiyon) ve override'lar zaten **provider'dan bağımsızdır**: bir rol
  kümesini tüketirler, onun nereden geldiğine karar vermezler. Provider'a özgü olan **hangi kümenin
  geldiğidir** ve ölçülen tam olarak odur.
- Bu bir **doğruluk** senaryosu; gecikme iddiası yok, Python yük testi bilinçli olarak yazılmadı.
