# vNext Build Plan — Cross-Domain Instance-Call Credential (`instance-call-credential-xd`)

> Durum: **ONAYLI** — kullanıcı, 2026-11-14 ("Onaylı, uygula"). Runtime: vnext `claude/subflow-subprocess-credentials-704267`; lab imajları o
> dalın worktree'sinden, ayrı etiketle (`VNEXT_SRC_DIR=<worktree> VNEXT_LAB_IMAGE_TAG=cred-rule lab.sh images`).
> Lab topolojisi ve bağlam: [VNEXT-BUILD-PLAN.md](VNEXT-BUILD-PLAN.md) (core :4201, partner :4211, discovery :4231).
> Aynı domain karşılığı: `tests/Core.IntegrationTests/Tests/InstanceCallCredentialLab/` (bu dalda, 18 test).

## 0. Özet & Kapsam

**Ne:** Runtime'ın başka bir instance'a yaptığı çağrılarda credential kuralını domain sınırında sınamak. Kural:
mapping'in yazdığı credential (boş dahil) kazanır; boş değer o credential'ı hedefe göndermez ve çağıranınkini
eklemez; mapping'in hiç yazmadığı credential çağırandan dolar. Credential'lar: `sub`, `act_sub`, `position`,
`client_id`, `role`. Kanonik anlatım: vnext `docs/domain/role-grant-authorization.md` § *Caller credential on
instance calls*.

**Neden:** Aynı domain lab'ı in-process çıkışı (`CallerCredentialScope`) kanıtlıyor. Domain sınırında iki ayrı
çıkış var ve hiçbiri uçtan uca koşmadı: SubFlow başlatmasında header birleştirme (boş değer ambient kullanıcının
header'ını siler), trigger task'larda Execution host'taki remote invoker (boş işaret istek çıkmadan silinir; host
artık kimlik eklemez).

**Kapsam dışı:** `credit` (üçüncü hop); HTTP/SOAP/Dapr task'ları (kural onları kapsamıyor); `morph-idm` sağlayıcısı
(lab varsayılan sağlayıcıyla koşar).

## 1. Kabul Kriterleri

Çağıran her testte kendi `sub` (GUID) ve `act_sub` değerini, okuma testlerinde ayrıca `role` header'ını gönderir.
"Hedefin gördüğü", hedef akışın kendi verisine yazdığı kayıttır (aynı domain lab'ındaki kaydediciyle aynı şekil).

- **XC-01 SubFlow, mapping header yazmadı** — core parent partner'da `S` child başlatır → child `sub` ve `act_sub`
  olarak çağıranınkini görür; child satırı `createdByBehalfOf` = `sub`, `createdBy` = `act_sub`.
- **XC-02 SubFlow, `sub = ""`** — child `sub` görmez, `act_sub` görür; `createdByBehalfOf` boş.
- **XC-03 SubProcessTask ve StartTask (core → partner)** — header'sız varyant XC-01, `sub = ""` varyantı XC-02 ile
  aynı sonucu verir.
- **XC-04 DirectTriggerTask (core → partner)** — tetiklenen transition header'sız varyantta `sub` / `act_sub`
  görür; `sub = ""` varyantında `sub` görmez.
- **XC-05 GetInstance, GetInstances, GetInstanceData (core → partner)** — hedefte `x-roles` ile korunan alan:
  çağıran izinli `role` gönderir ve task header yazmazsa alan döner; task `role = ""` yazarsa dönmez.
- **XC-06 İç içe sızıntı** — core parent partner SubFlow'unu `sub = ""` ile başlatır; partner'daki child kendi
  mapping'inde header yazmadan core'daki bir hedefe `DirectTrigger` ve `GetInstance` çalıştırır → hedef `sub`
  görmez (ne header'dan ne trace baggage'ından).
- **XC-07 Bilinen fark** — çağıran `userId` header'ı gönderir; cross-domain SubFlow child'ı onu görür (aynı
  domain'de görmez). Kullanıcı kararıyla korunan davranış; test onu sabitler.
- **XC-08 Eşlik** — XC-01…XC-05'teki her beklenti, `InstanceCallCredentialLab`'daki aynı durumun beklentisiyle
  birebir aynıdır.

## 2. Bileşen Envanteri

| Kök | Bileşen | Rol |
|---|---|---|
| `partner/Workflows/instance-call-credential-xd/` | `icc-xd-target` (F) | Kaydedici hedef; master şemasında `x-roles` korumalı alan; `touch` transition'ı tetiklenen hop'u kaydeder |
| `partner/Workflows/instance-call-credential-xd/` | `icc-xd-sub` (S) | Kaydedici SubFlow child'ı |
| `partner/Workflows/instance-call-credential-xd/` | `icc-xd-sub-nest` (S) | XC-06: header yazmadan core'a `DirectTrigger` + `GetInstance` |
| `partner/Schemas/instance-call-credential-xd/` | `icc-xd-target-master` | `x-roles` korumalı alan |
| `partner/Tasks/instance-call-credential-xd/` | kaydedici script task'ı; core'a dönen iki trigger task'ı | — |
| `core/Workflows/instance-call-credential-xd/` | `icc-xd-parent` (F) | Çağrı başına bir transition; SubFlow `process.domain: "partner"` |
| `core/Tasks/instance-call-credential-xd/` | type 14 / 11 / 12 / 19 / 15 / 13, `{"domain":"partner","useDapr":true}` | Uzak çağrılar |
| `core` (mevcut) | `icc-target` | XC-06'nın core'daki hedefi (aynı domain lab'ından) |

Mapping script'leri aynı domain lab'ındakilerle aynı mantıktadır; `.csx` → `code` için
`labs/cross-domain/encode-scripts.py` koşulur. Partner yayını collection fixture'da yapılır.

## 3. Testler

`tests/Core.IntegrationTests/Tests/CrossDomainLab/InstanceCallCredentialCrossDomainTests.cs` (mevcut
`CrossDomainLabFixture` / `CrossDomainLabTestBase` üstünde; partner URL'i yoksa skip). README ve
`TEST-SCENARIOS.md` satırı aynı commit'te.

## 4. Koşum ve Kanıt

```bash
VNEXT_SRC_DIR=<vnext worktree> VNEXT_LAB_IMAGE_TAG=cred-rule labs/cross-domain/lab.sh images
VNEXT_LAB_IMAGE_TAG=cred-rule labs/cross-domain/lab.sh up
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~InstanceCallCredentialCrossDomain" -v minimal
```

Kanıt: partner veritabanında child satırlarının `CreatedBy` / `CreatedByBehalfOf` kolonları; OpenObserve'de
uzak çağrı span'leri. Komşu koşu: `CrossDomainLab` suite'inin tamamı (mevcut testler bozulmamalı).

## 5. Riskler

- Lab ile `run-docker.sh` altyapısı aynı portları kullanır: önce bu worktree'nin `core`'u ve altyapısı indirilir.
- `useDapr` alanı vnext-schema'da 11/12/13/14 için tanımlı değil (`npm run validate` kırmızısı bilinen açık).
- Runtime kurala uymazsa test yumuşatılmaz; bulgu kanıtıyla raporlanır.

## Sonuç (2026-11-14)

Runtime: lab imajları vnext `claude/subflow-subprocess-credentials-704267` @ `be83eb7f`, sonra `bc127eda` (etiket `cred-rule`), provider
`dapr`, caller-role `default`. Testler: `Tests/CrossDomainLab/InstanceCallCredentialCrossDomainTests.cs`.

| Koşu | Sonuç |
|---|---|
| `InstanceCallCredentialCrossDomain` | **17/17 yeşil** — XC-01…XC-07 + XC-06 kontrolü; XC-08 eşliği aynı domain lab'ının kaydedici ve beklentileriyle |
| tüm `CrossDomainLab` | 43 geçti / 4 kırmızı / 2 skip — kırmızılar `FileOffloadCrossDomainTests` (partner sidecar'ında `vnext-blob-local` binding'i yok, lab şablonu açığı); skip'ler `dapr` provider'da warm-up |
| `InstanceCallCredentialLab` (lab core'u) | 18/18 |
| `HumanTaskChain` | 23 geçti / 4 skip (morph-idm) |

Plandan sapmalar:
- XC-06'nın core hedefi aynı domain lab'ının `icc-target`'ı değil, yeni `icc-xd-core-target` (+ şema
  `icc-xd-core-target-master`, `starterNote` x-roles allow `$InstanceBehalfOfStarter`): okumanın `sub` taşıyıp
  taşımadığını görünür kılan bir alan gerekti ve aynı domain lab'ı değişmesin istendi.
- XC-06'ya bir kontrol testi eklendi (mapping'siz aynı yol `sub`'ı core'a taşır).
- Bileşenler 1.0.3 (yayınlanmış 1.0.0–1.0.2 düzeltmeleri; publish sürüm-değişmez).

Koşum sırasında bulunan ve düzeltilen runtime kusuru: cross-domain Start mapping'in `key`'ini düşürüyordu
(`TaskBindingMapper.MapStartTask` uzak binding'e `Key` koymuyordu; SubProcess koyuyordu — bu kuralla ilgisiz, master'da da
vardı). vnext `bc127eda` ile düzeltildi; XC-03 artık Start hedefini de anahtarla okur ve anahtarın partner'a ulaştığını
assert eder. `bc127eda` imajlarıyla yeniden koşu: `InstanceCallCredentialCrossDomain` 17/17, tüm `CrossDomainLab` 43/4/2
(aynı FileOffload kırmızıları ve warm-up skip'leri), `InstanceCallCredentialLab` 18/18.

