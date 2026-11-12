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
