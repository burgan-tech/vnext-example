# rule-schema-lab — kural tabanlı transition / function şemaları (vnext#1225)

## Neyi denetliyor

**Tek iddia:** Bir transition `schemas` (kural tabanlı şema listesi) taşıyorsa runtime girdileri
bildirim sırasıyla değerlendirir, **ilk eşleşen kuralın şemasıyla** gövdeyi doğrular, kuralsız son
girdi fallback'tir; liste tanımlı olup hiçbir girdi eşleşmezse istek `400` `Transition:100025` ile
reddedilir ve instance yerinde kalır. Şema hiç tanımlı değilse istek kabul edilir. Kural tabanlı
`inputSchema`'sı eşleşmeyen bir function isteği `400` `Function:800006` ile reddedilir.

## Neden var

Transition doğrulaması çekirdek süreçtir (start, accept ve her manuel hop'ta koşar) ve şema
fonksiyonunun cache anahtarı/ETag'i artık istek header'larını özetler; ikisi de uçtan uca pinlenir.

## Akış

```
start-rule-schema-lab (şema yok)
        │
        ▼
   review (Initial)
        ├─ submit           schemas: [x-segment=corporate → corporate, x-segment=retail → retail]  → done
        ├─ submit-fallback  schemas: [x-segment=corporate → corporate, (kuralsız) → retail]        → done
        └─ note             şema yok                                                               → $self
```

- `rule-schema-lab-corporate`: `taxNumber` zorunlu (string, en az 10 karakter).
- `rule-schema-lab-retail`: `nationalId` zorunlu (11 rakam).
- `rule-schema-lab-echo` (function, scope `D`): `inputSchema` = tek kurallı girdi (corporate), fallback yok.

Kurallar `x-segment` header'ını okur. Bileşenler `.csx` kaynaklarından üretilir:

```bash
python3 core/Workflows/rule-schema-lab/build-rule-schema-lab.py
```

## Beklenen sonuç

| Test | İddia |
| --- | --- |
| `Submit_CorporateSegment_ValidatesAgainstTheCorporateEntry` | `corporate` altında retail gövdesi `400` (şema hatası, `100025` değil); corporate gövdesi kabul, `done`/`C` |
| `Submit_RetailSegment_ValidatesAgainstTheRetailEntry` | Aynısının tersi: seçilen girdi header'a göre değişir |
| `Submit_NoEntryMatches_Rejected_InstanceUntouched` | `sme` ve header'sız istek `400` `Transition:100025`; instance `review`/`A`, fault yok |
| `SubmitFallback_NoRuleMatches_FallbackEntryValidates` | Eşleşmeyen çağıran kuralsız retail girdisine düşer |
| `Note_NoSchemaDeclared_AcceptsAnyPayload` | Şemasız transition her gövdeyi kabul eder |
| `SchemaFunction_ServesTheRuleSelectedEntry` | `functions/schema?transitionKey=submit` segment'e göre corporate/retail döner, ETag'ler farklı; aynı segment + ETag `304`; başka segment + o ETag `200`; eşleşme yoksa `404`; fallback'li transition retail döner |
| `Function_RuleBasedInputSchemaNoMatch_Rejected` | `retail` header'ı + gövde `400` `Function:800006`; corporate altında geçersiz gövde `400` (şema hatası); geçerli gövde `200` |

Seçilen şema sonucuyla kanıtlanır: aynı gövde bir segment altında geçer, öbüründe düşer. `schemas`'ı
yok sayan ya da yanlış girdiyi seçen runtime bu testleri geçemez.

## Çağıran rolleriyle seçim (`context.Caller`)

`RuleSchemaLabCallerRolesTests` (6 test) kuralların çağıranın **runtime'ın çözdüğü** rollerini okumasını
pinler (vnext `script-caller-roles`, konsey `2026-11-12-script-caller-roles`):

- `review` state'inin view'i role göre seçilir (`IsApproverRole.csx` → `rule-schema-lab-approver-view`,
  fallback `rule-schema-lab-default-view`); `submit-by-role` approver'a corporate, diğerlerine retail
  fallback şemasını verir.
- **Header testleri (iki provider'da da koşar):** `role: rule-schema.approver` corporate şemayı ve approver
  view'ini seçer; başka rol fallback'e düşer; şema fonksiyonunun ETag'i rolden role değişir, bir rolün ETag'i
  öbür rolde `304` almaz.
- **morph-idm testleri** (`VNEXT_CALLER_ROLE_PROVIDER=morph-idm`, runtime
  `CallerRoleProvider__Provider=morph-idm` + MockLab `morph-idm-roles-collection`): `rbs-idm-approver`
  (rol header'ı **yok**) corporate + approver view; `rbs-idm-plain` fallback; `idm-broken` (IDM 500) ile
  transition, şema ve view fonksiyonları **503 `Authorization:110006`** — fallback değil; instance yerinde
  kalır; yalnız header okuyan `submit` kuralı kesintiden etkilenmez.
- `rule-schema-lab-start` (v1.0.0): start şeması role göre seçilir — start yolu maliyet ölçümü (C-3) için.

morph-idm koşusu (runtime'ı yeniden başlatır; `GetRolesPath` seed'in `api/1` yoluyla eşleşmeli):

```bash
CallerRoleProvider__Provider=morph-idm \
CallerRoleProvider__MorphIdm__BaseUrl=http://localhost:3001 \
CallerRoleProvider__MorphIdm__GetRolesPath=/api/1/morph-idm/functions/get-roles \
  ./run-docker.sh up <domain> --no-build
VNEXT_CALLER_ROLE_PROVIDER=morph-idm dotnet test tests/Core.IntegrationTests \
  --settings tests/Core.IntegrationTests/test.runsettings --filter "FullyQualifiedName~RuleSchemaLab"
```

## Nasıl çalıştırılır

Lokalde derlenen runtime'a karşı (TEST-SCENARIOS.md § Koşum Öncesi Kontrol). MockLab gerekmez; tek
task `sched-script-task` (script). Suite `TestDomain.Name` kullanır, yan domain'de de koşar:

```bash
dotnet test tests/Core.IntegrationTests \
  --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~RuleSchemaLab" -v minimal
```

`schemas` (transition) yayımlanmış vnext-schema'da vnext-schema#169 birleşip yayınlanana kadar yoktur;
`npm run validate` o zamana kadar bu akışı reddeder.

## Doğrulama durumu

✅ **7/7 yeşil** (2026-11-12, vnext `1ebbd9c8`, yan domain `rbs` :4231). Postgres: 6 instance, 3 `done`/`C`
(`submit` ×2, `submit-fallback` ×1), 3 `review`/`A`; reddedilen istekler transition satırı bırakmadı,
fault yok. OpenObserve: 8 `Schema.Validate` span'i (6 transition + 2 function, ort. 1.9 ms, maks.
12.6 ms), 3 "no schema entry matching" logu (2 reddedilen submit + 1 şema fonksiyonu 404), 1 function
no-match logu. `npm run validate`: dört bileşen lokal bağlı vnext-schema#169 ile geçti.

**Çağıran rolleri — 2026-11-12, vnext `3caddd40` (`feature/script-caller-roles`), yan domain `rbs`:**
default provider 10/10 (+3 morph-idm testi skip), morph-idm 13/13. MockLab: onaylı kullanıcı istek başına
tam 1 IDM çağrısı (handler'ın cache-key çözümü ile script okuması aynı memo), header-only kural kesintide 0
çağrı, `idm-broken` 3 çözüm × 2 deneme (retry 1) = 6 çağrı, hepsi 500. Start yolu ölçümü (C-3, 40 ardışık /
100 eşzamanlı start): IDM gecikmesi 0 / 50 / 500 ms'de ardışık p95 49 / 119 / 584 ms (rol kuralsız kontrol
akış 32 / 28 / 64 ms); 100 eşzamanlıda tümü 202, havuz tükenmedi (en çok 72 bağlantı); 2 sn'lik IDM
beklemesi sırasında `idle in transaction` 0 — IDM çağrısı açık transaction tutmuyor. 6 sn IDM ile istek
5 sn timeout'ta 503 döner; 22 ardışık timeout'tan sonra da circuit breaker açılmadı (her istek ~5 sn) —
mevcut morph-idm resolver ayarı, bu değişiklikten bağımsız.
