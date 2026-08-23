# payload-modes — request payload shapes vs schema validation

## Neyi denetliyor

Bir transition ya da `startTransition` üzerinde `schema` tanımlıysa, **istemcinin payload'ı hangi
biçimde gönderdiği validasyonun sonucunu değiştirmemelidir.** Üç biçim de aynı iş payload'ına
çözülmeli, aynı şemaya karşı doğrulanmalı ve instance verisine aynı şekilde yansımalıdır.

```jsonc
// 1) Standart zarf + instance metadata
{ "key": "<guid>", "attributes": { "session": "-", "customer": { "ownerUserId": "2321321" } } }

// 2) Standart zarf, metadata yok — zarfın her alanı opsiyoneldir
{ "attributes": { "session": "-", "customer": { "ownerUserId": "2321321" } } }

// 3) Serbest payload — gövdenin kendisi payload'dır
{ "session": "-", "customer": { "ownerUserId": "2321321" } }
```

Zarf alanları (`key`, `tags`, `stage`) **payload değildir**: iş verisi olarak şemaya sokulmamalı ve
instance verisine yazılmamalıdır.

## Neden var

Runtime, payload modunu tek bir **case-sensitive** `attributes` property'sinin varlığına bakarak
belirliyordu (`PayloadModeDetector` + `FormUrlEncodedJsonElementInputFormatter`). Oysa vNext zarfı
bir *alan kümesidir* ve alanların hepsi opsiyoneldir. Sonuç:

- `attributes` içermeyen geçerli bir zarf (`{"key":"..."}`, `{"key":"...","tags":[...]}`) serbest
  payload sanılıyor, **tüm zarf** `attributes` altına sarılıyordu. Şema artık iş payload'ını değil
  `key`/`tags`/`stage` alanlarını doğruluyor ve `additionalProperties: false` olan bir şemada
  istek *"All values fail against the false schema"* ile 400 alıyordu.
- `Attributes` (PascalCase) aynı şekilde serbest payload sanılıyordu — oysa hemen ardından gelen
  JSON model binding case-insensitive'dir, yani alan bağlanabiliyor ama oraya hiç ulaşılamıyordu.
- Şeması olmayan bir transition'da aynı hata sessizdi: zarf, iş verisiymiş gibi instance datasına
  yazılıyordu (`attributes.key`).

İlgili düzeltme vNext tarafında `PayloadEnvelope` ortak zarf sözlüğünü getirir; iki dedektör de
aynı kuralı kullanır. Bkz. `vnext/docs/contracts/form-url-encoded-payloads.md` → *Payload-mode
override*. Tarih: 2026-08-22.

## Akış şeması

`payload-modes` (core / sys-flows) — kasıtlı olarak **hiç task içermez**, böylece testler yalnızca
payload/şema davranışını ölçer; MockLab, execution host veya worker bağımlılığı yoktur.

```
start-payload-modes  ──► collect-payload ──(submit-payload)──► payload-received ──(complete)──► done
   schema: payload-modes-input          schema: payload-modes-input        (şema yok)
```

`payload-modes-input` şemasında `session` ve `customer.ownerUserId` zorunludur ve
**`additionalProperties: false`** açıktır — zarf sızıntısını sessiz bir geçiş yerine sert bir 400'e
çeviren şey budur.

## Nasıl çalıştırılır

Önkoşul: altyapı ayakta (`cd etc/docker && ./run-docker.sh`) ve **lokalde derlenmiş** orchestration
host çalışıyor (bu senaryo execution host'a ve worker'lara ihtiyaç duymaz):

```bash
dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host --launch-profile http
```

`test.runsettings` içindeki `VNEXT_BASE_URL` lokal orchestrator'ı göstermelidir
(`http://localhost:4201`) — aksi halde SDK kendi container yığınını ayağa kaldırır ve **yayınlanmış
image'daki eski kodu** test edersiniz.

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings --filter "FullyQualifiedName~PayloadModes"
```

## Beklenen sonuç

24 test yeşil. Düzeltme öncesi runtime'a karşı çalıştırıldığında **tam olarak şu 4 test kırmızıya
döner** — senaryonun regresyon iğnesi budur:

| Test | Düzeltme öncesi hata |
|---|---|
| `PayloadModesStartTests.EnvelopeWithoutAttributes_IsTreatedAsAnEnvelope_NotAsThePayload` | zarf payload olarak doğrulandı |
| `PayloadModesStartTests.PascalCasedAttributes_IsStillAStandardPayload` | `Attributes` serbest payload sanıldı → 400 |
| `PayloadModesTransitionTests.EnvelopeWithoutAttributes_IsTreatedAsAnEnvelope_NotAsThePayload` | zarf payload olarak doğrulandı |
| `PayloadModesTransitionTests.TransitionWithoutSchema_AcceptsEveryShape_AndNeverStoresTheEnvelope` | `key` iş verisi olarak instance'a yazıldı |

Kullanıcının bildirdiği üç kanonik biçim (yukarıdaki 1/2/3) düzeltme **öncesinde de** geçiyordu;
testler bunu da sabitler, çünkü asıl sözleşme "üçü de aynı sonucu üretir" iddiasıdır.

## Hata gövdesi — alan düzeyinde ayrıntı

Şema reddi **her zaman** `{"error":{…,"validationErrors":[…]}}` biçiminde döner ve hangi alanın
neden düştüğünü söyler:

```jsonc
{ "members": ["root"],     "message": "Required properties [\"customer\"] are not present" }
{ "members": ["customer"], "message": "Required properties [\"ownerUserId\"] are not present" }
{ "members": ["rogue"],    "message": "All values fail against the false schema" }
{ "members": ["session"],  "message": "Value is \"integer\" but should be \"string\"" }
```

Bu senaryo yazılırken bu **çalışmıyordu**: kök düzeyindeki bir `required` hatası, hiyerarşik
değerlendirme ağacı düzleştirilirken düşüyordu (`JsonSchemaValidationMapper.FlattenErrors` bir
düğümün *kendi* hatalarını, çocukları varsa, atıyordu). Sonuç: istemci `"errors":{}` taşıyan bir
RFC7807 ProblemDetails gövdesi alıyor ve **hangi alanın hatalı olduğunu öğrenemiyordu**. Aynı hata
iki farklı gövde biçiminin de sebebiydi — hata listesi boş kalınca yanıt ProblemDetails'e
düşüyordu. Düzeltmeyle birlikte tek biçim kaldı.

`AssertSchemaValidationFailure(shape, body, "customer")` bu sözleşmeyi sabitler: 400 + `App:900002`
+ **boş olmayan** `validationErrors` + beklenen alan adının geçmesi.
