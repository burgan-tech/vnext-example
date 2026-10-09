# CrossDomainLab — cross-domain SubFlow, trigger task'ları, fonksiyon descent'i (core → partner) ve discovery warm-up

## Neyi denetliyor

`core` domain'indeki `xd-parent` akışı, `partner` domain'indeki bileşenleri **Dapr Name Resolution +
Service Invocation** üzerinden tüketir (`ServiceDiscovery:Provider=dapr`). Suite şunu pinler: parent
üzerinden yapılan her okuma/yazma partner içeriğiyle cevaplanır (descent) ve altı cross-domain task
tipi (11 Start, 12 DirectTrigger, 13 GetInstanceData, 14 SubProcess, 15 GetInstances, 19 GetInstance)
`useDapr:true` ile partner'a ulaşır.

Suite'in ikinci yarısı (`DiscoveryWarmUpTests`) transport'u değil **adres kaynağını** denetler:
runtime'ın discovery endpoint cache'ini registry'nin `domain-list` fonksiyonundan doldurması. Bu kısım
`partner`'a değil, yalnız `core` + registry'ye ihtiyaç duyar ve `ServiceDiscovery:Provider=http`
ister — cache yalnız HTTP provider'da register edilir.

## Neden var

Cross-domain adres çözümlemesi Discovery registry HTTP'sinden Dapr'a taşındı
(vnext `feature/dapr-name-resolution`, 2026-09). vnext-example'da o güne kadar **hiç** cross-domain
örnek yoktu (`config.domain` her yerde `core`, `useDapr` hiç geçmiyordu); yeni transport'un tek uçtan
uca regresyon fixture'ı bu suite'tir. Tasarım: `labs/cross-domain/VNEXT-BUILD-PLAN.md`.

## Akış

```
core/xd-parent (F)
xd-initial ─auto→ xd-hub ─enter-subflow→ xd-subflow(4: S → partner/xd-child) ─auto→ xd-after-subflow
  ─spawn-subprocess(14)→ xd-after-spawn ─start-remote(11)→ xd-after-start ─trigger-remote(12)→ xd-after-trigger
  ─read-remote(19+13)→ xd-after-read ─list-remote(15)→ xd-completed

partner/xd-child (S):  child-initial ─auto→ child-review [view, child-approve: schema + roles xd-approver] → child-completed
partner/xd-remote (F): remote-initial ─auto→ remote-waiting ─remote-advance→ remote-done
partner/xd-worker (P): worker-initial ─auto→ worker-done
```

Kritik adımlar: `xd-subflow` parent'ı subflow ömrü boyunca Busy tutar → testler **gözlenen** (leaf) state'i
bekler, status'u değil. Her task ayrı transition'da: kırmızı bir test tek bir task tipini gösterir.

## Sınıflar

| Sınıf | AC | Ölçtüğü |
|---|---|---|
| `SubflowDescentTests` | 01–06 | child start (partner'da), `state`/`view`/`schema`/`authorize` descent'i, `data?extensions=` descent'i (gövde parent'ta kalır — runtime kararı), parent üzerinden `child-approve` forward + parent resume |
| `TriggerTaskTests` | 07–11 | 14 fire-and-forget worker, 11 sync start (+`remoteInstanceId`/`remoteKey`), 12 `remote-advance`, 19+13 okuma (`remoteState`, `remoteData.testId`), 15 `attributes.testId` filtresiyle liste |
| `DiscoveryWarmUpTests` | 12–14 | registry'nin `domain-list` sözleşmesi (düz `items[]`, dört alan, sayfalama zarfı **yok**), `POST utilities/discovery/refresh` → `Refreshed` (runtime function'ı okuyup cache'i yazdı), yeni bir kayıttan sonra listenin büyümesi ve warm-up'ın hâlâ başarılı olması |
| `FileOffloadCrossDomainTests` | XS-01–05 | `x-storage` domain sınırında: partner yaprağa proxy'lenen sync/async yükleme, leaf-owned handle (partner binding'i + owner), partner `functions/file` 200 / core parent 404, core'dan `GetFileAsync("partner", …)` — bkz. § x-storage |

`CrossDomainLabFixture` partner bileşenlerini (`partner/`, `vnext.partner.config.json`) bir kez yayınlar —
harici-stack modunda SDK'nın `OnAfterEnvironmentReadyAsync` hook'u çağrılmadığı için fixture'da.
Partner yoksa `SubflowDescentTests` + `TriggerTaskTests` **skip** (`Xunit.SkippableFact`). "Yok" =
değişken boş **veya** `GET {url}/health` 2 sn'de cevap vermiyor **veya** cevaptaki `domain` `partner`
değil (`Infrastructure/OptionalDomainEndpoint.cs`, 2026-10-05). Önceden yalnız değişkene bakılıyordu;
URL `test.runsettings`'te commit'li olduğu için core-only koşuda 14 test 60 sn timeout'la kırmızıya
düşüyordu. Konsolda `[optional-domain] … dependent tests skip` satırı sebebi söyler.
`DiscoveryWarmUpTests` ayrı bir fixture (`DiscoveryRegistryFixture`) ve ayrı bir değişken kullanır —
`VNEXT_DISCOVERY_BASE_URL` boşsa ya da registry `/health`'e `discovery` (veya `VNEXT_DISCOVERY_DOMAIN`) olarak cevap vermiyorsa skip; cache kapalıysa (refresh `disabled` döner) yine skip, çünkü
`Provider=dapr` altında cache hiç register edilmez ve bu bir kusur değil konfigürasyondur.

## Çalıştırma

Ön koşul: lab ayakta — `labs/cross-domain/lab.sh up` (core :4201, partner :4211, discovery :4231;
detay `labs/cross-domain/README.md`). Runtime kodunu değiştirdiysen `lab.sh images` → `down` → `up`.

```bash
cd tests/Core.IntegrationTests
dotnet test --settings test.runsettings --filter "FullyQualifiedName~CrossDomainLab"
```

`test.runsettings`: `VNEXT_BASE_URL=http://localhost:4201`, `VNEXT_PARTNER_BASE_URL=http://localhost:4211`,
`VNEXT_DISCOVERY_BASE_URL=http://localhost:4231`. Farklı port/offset kullanıyorsan **committed dosyayı
düzenleme**, yanına git-ignore'lu `test.runsettings.local` koy.

`DiscoveryWarmUpTests` için ek koşullar:

- registry `@burgan-tech/vnext-discovery-runtime` **>= 0.0.7** taşımalı (`domain-list` ilk o sürümde);
  lab bunu init container'ından yayınlar (`lab.sh` içinde `VNEXT_DISCOVERY_PACKAGE_VERSION`).
- core `ServiceDiscovery__Enabled=true`, `ServiceDiscovery__Provider=http`,
  `ServiceDiscovery__Cache__Enabled=true` ve `ServiceDiscovery__BaseUrl=<registry>/api/v1` ile
  koşmalı. Lab'ın varsayılanı `Provider=dapr`'dır: `VNEXT_LAB_DISCOVERY_PROVIDER=http` ile kaldır.
- Üç domain'lik lab şart değil; `core` + registry yeten en küçük kurulumdur.
Script gövdeleri (`.csx`) değiştiğinde `python3 labs/cross-domain/encode-scripts.py` ile `code`
alanlarını yenile (runtime `location`'dan değil `code`'dan derler).

**Publish sürüm-değişmezdir.** Aynı sürüm yeniden yayınlanınca runtime 409 `Instance:100002` döner;
publisher çıktısındaki `FAIL Conflict` satırları değişmemiş bileşenler için **normaldir** ve testler
önceki yayına karşı koşar. Bir bileşeni değiştirdiysen `version`'ı patch bump'la ve onu tam sürümle
referanslayan yerleri güncelle (ör. `xd-child-ext 1.0.1` → `xd-child.extensions[]` → `xd-child 1.0.1`
→ `xd-parent.subFlow.process.version` → `xd-parent 1.0.1`); aksi hâlde düzeltmen canlıya hiç çıkmaz.

## 2026-10-05 koşusu

master @ `1104d8ae`, lab `dapr-nr` imajları (aynı gün derlendi). `dapr` provider: 12 geçti / 2 skip
(`DiscoveryWarmUpTests` — cache yalnız `http`'de), art arda üç koşu. `http` provider: 13/14.

- **Düzeltilen test kusuru:** her adım state'e varınca hemen sonrakini gönderiyordu; parent subflow
  tamamlanmasını settle ederken (Busy) gelen `spawn-subprocess` 409 "instance is Busy" alıyordu (3'te 1).
  `CrossDomainLabTestBase` artık state'ten sonra Active'i de bekliyor.
- **Bulunan gerçek kusur (discovery paketi):** `NewRegistration_IsVisibleToTheNextWarmUp` — kayıt
  StartTask'ı asenkron, `discovery:domains:active` eviction'ı commit'ten önce; yarışı kaybeden kayıt
  24 saat listede görünmez. Zaman çizelgesi ve öneri: `TEST-SCENARIOS.md` § Bilinen Kapsam Açıkları.
- **MockLab lab'da yok:** konteynerdeki core `localhost:3001`'e erişemez; lab'da yalnız cross-domain
  suite'leri koşulur, MockLab'e bağlı suite'ler yerel host runtime'ında (`run-docker.sh up core`).

## x-storage cross-domain (`file-offload-xd`, vnext-client-sdk-core#101)

Plan ve kabul kriterleri: `labs/cross-domain/VNEXT-BUILD-PLAN-file-offload.md` (XS-01..XS-06). Bileşenler
`core/Workflows/file-offload-xd/build-file-offload-xd.py` ile `./src/*.csx`'ten üretilir (core VE partner
dosyalarını birlikte yazar; sonra `python3 labs/cross-domain/encode-scripts.py core/Workflows/file-offload-xd`).

```
core/fo-xd-parent (F, master fo-xd-parent-master — aynı passport x-storage yolu, parent-tarafı swap'ı yakalamak için)
  p-hub ─enter-child→ p-child (4: S → partner/fo-xd-child) ─auto→ p-done
partner/fo-xd-child (S, master fo-xd-child-master — passport x-storage: vnext-blob-local)
  child-waiting ─child-upload→ child-waiting ─child-finish→ child-done
core/fo-xd-reader (F, şemasız)
  r-waiting ─read {sourceDomain, sourceFlow, sourceInstance, sourceFileId}→ r-read [onEntry: GetFileAsync → checksum]
```

| Test | AC | Ölçtüğü |
|---|---|---|
| `EnterChild_StartsTheChildInPartner` | XS-01 | core parent'ın `state`'i `child-waiting`, korelasyon `subFlowDomain=partner` |
| `SyncUpload_ThroughCoreParent_PartnerLeafOwnsTheHandle` | XS-02 | parent'a sync `child-upload` → 200 + parent id; partner verisinde handle (`component`, D-GUID, `size`, `eTag` = SHA-256, `owner = partner/fo-xd-child/<child>`); core parent verisinde/geçmişinde `content`, `passport` ve dosya id'si yok |
| `AsyncUpload_ThroughCoreParent_Is202_AndThePartnerLeafOwnsTheHandle` | XS-03 | aynısı async → 202 + parent id, dinlenme sonrası aynı koşullar |
| `FileFunction_PartnerServesTheBytes_CoreParentDoesNotDescend` | XS-04 | partner `functions/file` 200 (SHA-256 = eTag, `ETag` başlığı, mime); core parent'ın `functions/file`'ı aynı id'ye 404 `Instance:100049` |
| `GetFileAsync_FromCore_ReadsThePartnerFile` | XS-05 | core script'i `GetFileAsync("partner", "fo-xd-child", child, file)` → checksum = eTag, ad/mime/eTag handle'la aynı |

Notlar:

- Okuyucunun gövde anahtarı bilerek `file` **değil** (`sourceFileId`): runtime `content`/`file` üyesi taşıyan her
  nesneyi dosya referansı sayar ve şemasız bir akışta onu reddedebilir.
- Child `child-upload`'da kendine döner (plan taslağında `child-done`'a gidiyordu): yaprak aktif kaldığı için core
  parent'ın `functions/file` 404'ü gerçek bir "iniş yok" kontrolü ve `GetFileAsync` aktif bir instance'ı okur.
- Parent subflow ömrü boyunca Busy (`B`) — tasarım gereği, yukarıdaki notla aynı.
- **XS-06 elle:** sidecar imajı distroless (`ls` yok); `docker cp vnext-orchestration-dapr-partner:/tmp/vnext-blobs <dir>`
  ve aynısı `-core` için. Beklenen: nesneler (dosya adı = handle'ın `file` GUID'i, `shasum -a 256` = `eTag`) yalnız
  partner'da.

### 2026-10-09 koşusu

Lab imajları vnext `feature/file-offload-x-storage`'dan, `ServiceDiscovery:Provider=dapr`, Dapr 1.18.0, her
domain'in orchestration sidecar'ında `vnext-blob-local`. `FileOffloadCrossDomain` **5/5** ilk koşuda yeşil; tüm
`CrossDomainLab` **17 geçti / 2 skip / 0 kırmızı** (skip'ler `dapr` provider'da beklenen warm-up testleri).
XS-06: partner deposunda 4 nesne (boyutlar 2048/3000/4096/8192, SHA-256'lar eTag'lere eşit), core deposu boş.
Postgres: core `fo_xd_parent` satırlarında `child-upload` kaydı yok (yalnız `start`/`enter-child`, gövdeler ≤ 21
bayt), `InstancesData` `content`/`passport` taşımıyor; partner `fo_xd_child.InstanceTransitions.child-upload`
gövdesi yalnız handle (≤ 348 bayt). Core'un uzak okuması log'da
`/v1.0/invoke/vnext-app-partner/method/api/v1/partner/workflows/fo-xd-child/instances/<id>/internal/file` → 200.

## Başarı kriteri / bilinen kısıtlar

- 11 test yeşil (her biri kendi `testId`'siyle yeni parent açar).
- `data` fonksiyonu gövdeyi subflow'a **indirmez**, yalnız `?extensions=` iner — AC-04 bunu pinler;
  runtime değiştirilirse test bilerek kırılır.
- vnext-schema 0.0.52 `useDapr`'ı yalnız task 15/19'da tanır → `core/Tasks/cross-domain-lab/` içindeki
  11/12/13/14 dosyaları `npm run validate`'te "must match then schema" verir; runtime alanı okur, alan
  bilinçli korunuyor. `partner/` `npm run validate` kapsamında değil (kabul edilmiş).
- `Discovery.Resolve/partner` span etiketleri (`vnext.discovery.provider=dapr`,
  `vnext.dapr.app_id=vnext-app-partner`) manuel doğrulanır (OpenObserve :5080); test assert etmez.
- Hata enjeksiyonu (callee kapalı → `ERR_DIRECT_INVOKE` → `remote_network_error`) ikinci faz.
- `DiscoveryWarmUpTests` cache'in **içeriğini** okuyamaz: runtime'da cache'i geri okuyan bir endpoint
  yok. `Refreshed` sonucu "okuma başarılı **ve** liste boş değil **ve** her kayıt yazıldı" demektir
  (refresher boş listeyi `Failed` sayar); kayıtların kendisi Redis'ten
  `vnext||discovery:domain:v1:<domain>` ile elle doğrulanır.
- AC-14 registry'ye sentetik bir `warmup-probe-*` domain'i yazar ve **silmez** — kayıt akışının
  geri alma yolu yok. Lokal lab DB'sinde zararsızdır; paylaşılan bir registry'ye karşı koşturma.
