# authorization-chain-lab

## Ne kontrol ediyor

`authorize` fonksiyonunun bir **aktif korelasyon zinciri** boyunca verdiği cevabın, okuma
yüzeylerinin fiilen uyguladığı kapıyla aynı olduğunu — ve parent'ın subflow override'larının
**hop başına**, çocuğa damgalanmış haritadan çözüldüğünü.

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

**`queryRoles` kaldırılmadı, yeri değişti.** Hâlâ tam olarak, aktif korelasyon zinciri boyunca hop
başına değerlendiriliyor — ama `GET .../functions/authorize?queryRoles=true` tarafından. Silinen şey,
runtime'ın aynı kararın **ikinci** kopyası. Bu suite'in geri kalan 40 testi zaten o cevabın doğruluğunu
ölçüyor; bu bölüm yalnızca ikinci kopyanın gitmiş olduğunu ölçüyor.

Bu yüzden `AuthorizeKeepsRefusing` buradaki en önemli satır: bir kaldırmanın bir yüzey fazla
gitmiş olduğunu diff'te görmek zordur, ve `authorize` reddetmeyi bıraksaydı gateway her şeye "evet"
diyen bir kâhine danışıyor olurdu.

## Geçme kriteri

44 testin tamamı yeşil. En ayırt edici üçü:

- `ChainConjunctionTests.ReaderPassesTheRootAndFailsTheChain` — konjonksiyon yoksa **yeşil olamaz**.
- `ChainOverrideTests.AnAncestorsOverrideDoesNotReachTheGrandchild` — override aşağı taşınırsa kırmızı.
- `ChainOverrideTests.ADirectlyAddressedLeafAnswersTheSameAsItsStateFunction` — damgalı harita
  yerine parent-taraflı okuyucu kullanılırsa kırmızı.

## Koşu kaydı

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
- **`morph-idm` provider'ı ile yalnız `MorphIdmProviderTests` koşuyor**, 44'ün tamamı değil — ve bu
  bilinçli. Provider başlangıçta bir kez seçilir, istek başına değişmez; diğer 44 test rolleri
  `x-roles` ile ayrıştırıyor, o header ise bu provider altında kararı vermiyor, dolayısıyla aynı
  suite'i ikinci provider altında koşmak yalnızca her çağrıyı aynı varsayılan role kümesine
  indirgerdi. Konjonksiyon ve override'lar zaten **provider'dan bağımsızdır**: bir rol
  kümesini tüketirler, onun nereden geldiğine karar vermezler. Provider'a özgü olan **hangi kümenin
  geldiğidir** ve ölçülen tam olarak odur.
- Bu bir **doğruluk** senaryosu; gecikme iddiası yok, Python yük testi bilinçli olarak yazılmadı.
