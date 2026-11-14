# VersionPinLab — integration test

İki sınıf, **on bir test**; `version-pin-lab-child`, `version-pin-lab-pkg-child`, `version-pin-lab-subflow-parent`, `version-pin-lab-subprocess-parent`, `version-pin-lab-start-parent` (domain `core`).

| Class | Test | What it checks |
|---|---|---|
| `VersionPinLabTests` | `Start_ReturnsResolvedFlowVersion` | `instances/start?version=1.0` (1.0.0 ve 1.0.3 yayında) cevabı çözümlenen sürümü taşır: `flowVersion == "1.0.3"` |
| `VersionPinLabTests` | `SubFlowCorrelation_StoresChildResolvedVersion` | `subFlow.process.version: "1.0"` olan SubFlow state'inde parent'ın correlation'ı `subFlowVersion == "1.0.3"` tutar; child instance'ı 1.0.3'te koşar; child bitince parent fault'suz `sf-completed`'a gider |
| `VersionPinLabTests` | `SubProcessCorrelation_StoresChildResolvedVersion` | Aynısı, child `SubProcessTask` (TaskType 14, trigger version `"1.0"`) ile başlatıldığında |
| `VersionPinLabTests` | `StartTask_StartsTheVersionItsConfigNames` | Config'i `"version": "1.0.0"` olan Start task'ı (TaskType 11; mapping `SetVersion` çağırmaz) child'ı 1.0.3 yayındayken de **1.0.0**'da başlatır |
| `PackageVersionResolutionTests` | `FloatingRequest_ResolvesTheHighestPackage` (4 vaka) | `1.0.0` ve `1.0` → `1.0.0-pkg.1.0.123+core` (1.0.9 / 1.0.99 / 1.0.123 arasından, sayısal); `1` ve `latest` → `1.1.0-pkg.1.0.1+core` |
| `PackageVersionResolutionTests` | `FullVersion_ResolvesThatPackage_WithOrWithoutBuildMetadata` (2 vaka) | `1.0.0-pkg.1.0.99` ve `1.0.0-pkg.1.0.99+core` → saklı `1.0.0-pkg.1.0.99+core` |
| `PackageVersionResolutionTests` | `FullVersionNotPublished_IsRefused` | `1.0.0-pkg.1.0.124` yayında değil → start reddedilir |

## Why this exists

Parent'ın okuma yolları (state function, descent, instance read) child'ı bulmak için correlation'daki
`SubFlowVersion`'ı **yeniden çözümler**. Yazara ait kısmi referans (`"1.0"`) saklanırsa, sonradan
yayınlanan bir `1.0.x` bu okumaları, çalışan child instance'ın doğduğu sürümden **farklı** bir sürüme
yönlendirir. Bu yüzden correlation, child'ın gerçekten koştuğu çözümlenmiş sürümü (`1.0.3`) tutmalı;
start cevabı da (`flowVersion`) aynı çözümlenmiş sürümü döndürmeli ki istemci neyin başladığını bilsin.
Pinlenen: start cevabındaki `flowVersion`, SubFlow state'inin ve SubProcess task'ının correlation
`subFlowVersion`'ı, child instance'ının kendi `flowVersion`'ı.

### Package versions (`PackageVersionResolutionTests`)

Paketlenmiş bir domain bileşenleri `ARTIFACT-pkg.PACKAGE+domain` sürümüyle yayınlar; tüketen akışlar
çoğunlukla yalnız `version=1.0.0` yazar. Bu sınıf aynı artifact'ın birden çok paket sürümünü yayınlar —
en yükseği (`1.0.123`) **son yayınlanan değil**, böylece "son yayın kazanır" varsayan bir önbellek ısıtması
yanlış cevap verirdi — ve her istek biçiminin doğru satıra çözüldüğünü pinler. vnext'in floating sürüm
düzeltmesinin regresyon bekçisidir: bu cevaplar eskiden tüm sürüm gövdelerini tutan bir listeden
hesaplanıyordu; uzun yayın geçmişinde bellek bitiyor, en yeni satırlar düşüyor ve eski bir sürüm
seçiliyordu. Artık sürüm adlarından karar verilip tek gövde yükleniyor; cevaplar değişmemeli.
Sürümler ayrı bir anahtar (`version-pin-lab-pkg-child`) altında yayınlanır ki `VersionPinLabTests`'in
çıplak `1.0.0`/`1.0.3` satırları karışmasın.

**Koşum (2026-11-13, vnext `claude/runtime-version-loading-32c214` çalışma ağacı, lokal core,
`VNEXT_BASE_URL=http://localhost:4201`):** 10/10 yeşil, iki kez. Postgres: başlatılan instance'lar
`1.0.0-pkg.1.0.123+core` ×2, `1.0.0-pkg.1.0.99+core` ×2, `1.1.0-pkg.1.0.1+core` ×2; Redis anahtarları
`s3` şekil segmentinde. Bu testler küçük gövdelerle koşar, yani eski runtime'da da yeşildir — ayırt
edici kanıt bellek tekrarıdır (vnext PR gövdesi: aynı flow'un 190 × 1,35 MB sürümü, 1 GB yığın
sınırında eski runtime 8/20 başlatmayı sessizce `1.0.0-pkg.1.0.98`'de başlattı, yeni runtime 50/50 `…1.0.190`).

### Start task config version (`StartTask_StartsTheVersionItsConfigNames`)

Start task'ının (TaskType 11) sürümü mapping'den değil yalnız config'inden geldiğinde de hedefe iletilmeli:
`"version": "1.0.0"` yazan bir Start task'ı child'ı 1.0.0'da başlatır, yayındaki daha yeni 1.0.3'te değil.
vnext'te config okuyucusu `version` alanı yerine `flow` alanını okuyordu (vnext `fa762e9b` ile düzeltildi,
burgan-tech/vnext#1310); o kodda istek sürüm olarak akış adını taşır,
start `404 Cache:300001` (`core/version-pin-lab-child@version-pin-lab-child`) alır ve test kırmızıdır.

**Koşum (2026-11-14, vnext `claude/subflow-subprocess-credentials-704267` @ `fa762e9b`, lokal core,
`VNEXT_BASE_URL=http://localhost:4201`):** VersionPinLab 11/11 yeşil. Postgres: child
`<parentId>-start` `FlowVersion = 1.0.0-pkg.1.0.0+core`; tanımın satırları `1.0.0-pkg.1.0.0+core` ve `1.0.3` (latest).

## The fixture

`core/Workflows/version-pin-lab/`, `build-version-pin-lab.py` ile `./src/*.csx`'ten üretilir
(`.csx`'i düzenle, script'i tekrar çalıştır; base64'e elle dokunma):

- `version-pin-lab-child.json` **1.0.0** — `child-initial` → manual `finish` → `child-finished`
  (Finish). Test bitirene kadar `Active` kalır. SDK bunu `core/` altından otomatik yayınlar.
- **1.0.3 varyantı `core/` altında YOK.** Test onu 1.0.0 dosyasından kurar (sürüm + etiket farkı) ve
  `api/v1/definitions/publish` ile bir kez yayınlar; SDK yayıncısı kendiliğinden göndermesin diye
  `core/` dışında tutulur (bkz. ImplicitStartLab'in probe yaklaşımı). Yayın sürüm-immutable olduğundan
  suite tekrar koşunca 409 / "already" cevabı sorun sayılmaz.
- `version-pin-lab-subflow-parent.json` — `sf-initial` → auto → `sf-subflow` (`stateType: 4`,
  `subFlow.type: "S"`, `process.version: "1.0"`) → auto → `sf-completed`.
- `version-pin-lab-subprocess-parent.json` — `sp-initial`'in onEntry'si
  `core/Tasks/version-pin-lab/version-pin-lab-start-subprocess.json` (TaskType 14) task'ını çalıştırır;
  mapping `SetVersion("1.0")` der. `sp-waiting`'de manual `complete` bekler (child bittikten sonra
  parent'ın fault'suz devam ettiğini göstermek için).
- `version-pin-lab-start-parent.json` — `st-initial`'in onEntry'si
  `core/Tasks/version-pin-lab/version-pin-lab-start-task.json` (TaskType 11, config
  `{"flow": "version-pin-lab-child", "version": "1.0.0", "sync": true}`) task'ını çalıştırır; mapping
  (`StartChildStartTaskMapping.csx`) yalnız `SetKey(<parentId>-start)` ve boş gövde verir, sürüme
  dokunmaz. `st-waiting`'de manual `complete` bekler.

## What each test asserts, in order

### `Start_ReturnsResolvedFlowVersion`
1. 1.0.3'ü yayınla (bir kez). 2. Child'ı `?version=1.0&sync=true` ile başlat. 3. Cevapta
`flowVersion == "1.0.3"`.

### `SubFlowCorrelation_StoresChildResolvedVersion`
1. Parent'ı başlat; state function'ın `activeCorrelations`'ında `subFlowName == version-pin-lab-child`
girdisini bekle. 2. `subFlowVersion == "1.0.3"`. 3. Child instance'ı okunur: `flowVersion == "1.0.3"`.
4. Child'da `finish`; parent `sf-completed`'a gider ve fault'lu değildir.

### `SubProcessCorrelation_StoresChildResolvedVersion`
Aynı üç adım, parent `sp-waiting`'e ulaştıktan sonra; child bitince parent fault'suz kalır, `complete`
ile `sp-completed`'a geçer.

### `StartTask_StartsTheVersionItsConfigNames`
1. 1.0.3'ü yayınla (bir kez). 2. Parent'ı başlat; `st-waiting`'e ulaşır ve fault'lu değildir.
3. Child `<parentId>-start` anahtarıyla okunur: `flowVersion`'ın artifact kısmı `1.0.0` (SDK 1.0.0 dosyasını
`1.0.0-pkg.<paket>+core` olarak yayınlar; latest `1.0.3`'tür). 4. Parent `complete` ile `st-completed`'a geçer.

## Pitfalls

- `subFlowVersion`'ın yeri: parent'ın state function cevabında `activeCorrelations[]`. Aynı alan
  instance okumasında farklı bir yolda olabilir; yol değişirse testin okuma yeri güncellenir.
- 1.0.3 yayını yapılmadan parent başlarsa çözümleme 1.0.0'a düşer; her test `EnsureChild103PublishedAsync`
  ile başlar.
- Çalıştırma: `dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~VersionPinLab"`.
