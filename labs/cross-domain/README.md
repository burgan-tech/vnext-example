# Cross-Domain Lab

Üç vNext domain'i tek Docker ağında ayağa kaldıran kalıcı lokal ortam. `core` ve `partner` **lokalde
derlenen** runtime imajlarıyla koşar, açılışta kendilerini `discovery` domain'ine kaydeder ve
birbirlerine **Dapr Name Resolution + Service Invocation** ile ulaşır (`ServiceDiscovery:Provider=dapr`).
Cross-domain davranış (SubFlow/SubProcess, trigger task'ları, fonksiyon descent'i) buraya karşı ölçülür.

## Topoloji

| Domain | Orchestrator | Init | Dapr app-id | İmaj | Rol |
|---|---|---|---|---|---|
| `core` | http://localhost:4201 | :3005 | `vnext-app-core` | `*:dapr-nr` (lokal) | parent akışlar, cross-domain task'lar |
| `partner` | http://localhost:4211 | :3015 | `vnext-app-partner` | `*:dapr-nr` (lokal) | child / remote akışlar |
| `discovery` | http://localhost:4231 | :3035 | `vnext-app-discovery` | `*:dapr-nr` (lokal) | registry (`@burgan-tech/vnext-discovery-runtime`) |

**Her üç domain de lokalde derlenen imajlarla koşar** (`orchestrator`, `execution`, `inbox`, `outbox`,
`db-migrator`, `init` — hepsi vnext reposundaki Dockerfile'lardan, `lab.sh images`). Release `latest`
imajı Dapr çalışmasından öncesine ait olduğu için hiçbir domain'de kullanılmaz; aksi hâlde test edilen
kodun bir kısmı eski sürüm olur. Dapr tarafı da pinlidir: `daprd`, `placement`, `scheduler` birlikte
`VNEXT_LAB_DAPR_VERSION` (varsayılan **1.18.0**) sürümünde koşar — geliştirici makinesindeki
`daprio/daprd:latest` etiketi aylarca eski kalabilir (2026-09-03'te 7 aylık 1.16.8 bulundu).

Paylaşılan altyapı (postgres, redis, dapr placement/scheduler, vault, otel-collector, OpenObserve)
`vnext-development` ağında bir kez kalkar; `lab.sh up` placement/scheduler sürümü pinden farklıysa
onları yeniden yaratır. Compose şablonu
[burgan-tech/vnext-runtime](https://github.com/burgan-tech/vnext-runtime)'dır; `lab.sh` onu
`.vnext-runtime/` altına klonlar (git-ignore'lu) ve `create-domain.sh` ile domain env'lerini üretir.

Ad çözümleme: sidecar'lar app konteynerinin ağ alanını paylaşır (`network_mode: service:...`) ve
**mDNS** resolver'ı bridge ağında iki compose projesi arasında çalışır; lab bunu `appconfig` ile
açıkça pinler. `nameformat` **çalışmaz**: daprd 1.16.x imajında yok ("couldn't find name resolver
nameformat/v1"), sidecar resolver'sız kalkar ve her invoke 500 döner. Lab app-id'leri
`vnext-app-{domain}` olduğu için (runtime konvansiyonu `vnext-{domain}-app`)
`DaprDomainDiscoveryProvider` registry'deki `appId`'yi kullanır — `RequireRegistryEntry=true` **ve**
`PreferRegistryAppId=true` bu yüzden şarttır (`orchestration.overlay.env`). Runtime varsayılanı
`RequireRegistryEntry=false`'tur (saf konvansiyon, registry'ye hiç gidilmez); lab bu varsayılanı bilerek
açar çünkü app-id'leri konvansiyondan sapıyor.

## Ön koşullar

Docker (OrbStack/Docker Desktop), `make`, `git`, `curl`, `python3`; imaj derlemek için vnext kaynak
kodu (`../vnext`, `VNEXT_SRC_DIR` ile değiştirilebilir). Testler için .NET 10 SDK.

## Komutlar

```bash
labs/cross-domain/lab.sh images      # vnext kaynağından 6 imajı (init dahil) dapr-nr ile derle + pinli Dapr imajlarını çek (~8 dk)
labs/cross-domain/lab.sh up          # infra + discovery (+ paket publish) + core + partner; sonda verify
labs/cross-domain/lab.sh verify      # health, registry kayıtları, sidecar üzerinden core→partner invoke
labs/cross-domain/lab.sh status      # konteyner listesi + verify
labs/cross-domain/lab.sh logs core   # (servis adı opsiyonel: vnext-app | vnext-execution-app | vnext-orchestration-dapr ...)
labs/cross-domain/lab.sh down        # üç domain'i durdur (--all ile altyapı da)
```

`up` idempotenttir: çalışan altyapıyı, var olan domain env'lerini ve yayınlanmış discovery paketini
atlar. Runtime kodunu değiştirdiysen sıra: `images` → `down` → `up`.

Ortam değişkenleri: `VNEXT_RUNTIME_DIR`, `VNEXT_RUNTIME_REPO`, `VNEXT_RUNTIME_REF`, `VNEXT_SRC_DIR`,
`VNEXT_LAB_IMAGE_TAG` (`dapr-nr`), `VNEXT_DISCOVERY_IMAGE_TAG` (varsayılan = `VNEXT_LAB_IMAGE_TAG`),
`VNEXT_LAB_DAPR_VERSION` (`1.18.0`), `VNEXT_LAB_DISCOVERY_PROVIDER` (`dapr` | `http`, varsayılan
`dapr`), `VNEXT_DISCOVERY_PACKAGE[_VERSION]`.

**Rollback tatbikatı:** `VNEXT_LAB_DISCOVERY_PROVIDER=http lab.sh up` core/partner'ı eski yola
(registry `baseUrl` + düz HttpClient) alır; aynı 11 test yeşil kalmalı. Not: `useDapr:true` task'ları
HTTP modunda da Dapr'a gider — `HttpDomainDiscoveryProvider` Dapr talebini registry `appId` varsa
karşılar (tasarım: task'ın açık Dapr talebi provider ile düşürülmez); provider yalnız `Remote*`
servislerini (subflow forward, fonksiyon descent'i) HTTP'ye çevirir. `lab.sh up` (bayraksız) dapr'a döndürür.

## Doğrulama noktaları

```bash
curl -s 'http://localhost:4231/api/v1/discovery/functions/domain-lookup?key=partner'
# {"data":{"domainName":"partner","baseUrl":"http://vnext-app-partner:5000","appId":"vnext-app-partner",...}}
docker exec vnext-app-core sh -c 'wget -qO- -S http://localhost:42110/v1.0/invoke/vnext-app-partner/method/health'
# HTTP/1.1 200 OK  → core daprd, partner app-id'sini çözdü ve partner orchestrator'a ulaştı
```

Trace: OpenObserve (http://localhost:5080; OTLP collector :4317) → core orchestrator'ın
`Discovery.Resolve/partner` span'i `vnext.discovery.provider=dapr`, `vnext.discovery.resolution=
registry|cache`, `vnext.dapr.app_id=vnext-app-partner` etiketlerini taşımalı; ardından caller ve callee
sidecar span'leri gelir.

## Integration testleri bu lab'a bağlamak

`tests/Core.IntegrationTests/test.runsettings`:

```xml
<VNEXT_BASE_URL>http://localhost:4201</VNEXT_BASE_URL>
<VNEXT_PARTNER_BASE_URL>http://localhost:4211</VNEXT_PARTNER_BASE_URL>
```

`VNEXT_BASE_URL` set olduğunda SDK Testcontainers açmaz ve `core` bileşenlerini 4201'e yayınlar.
`partner` bileşenleri (`partner/`, `vnext.partner.config.json`) harici-stack modunda SDK hook'u
(`OnAfterEnvironmentReadyAsync`) çağrılmadığı için senaryonun kendi fixture'ı
(`CrossDomainLabFixture`) tarafından `LocalDomainPublisher` ile yayınlanır.

```bash
cd tests/Core.IntegrationTests
dotnet test --settings test.runsettings --filter "FullyQualifiedName~CrossDomainLab"
```

Senaryo: [tests/Core.IntegrationTests/Tests/CrossDomainLab/README.md](../../tests/Core.IntegrationTests/Tests/CrossDomainLab/README.md)
· tasarım: [VNEXT-BUILD-PLAN.md](VNEXT-BUILD-PLAN.md). `.csx` değişince
`python3 labs/cross-domain/encode-scripts.py` ile `code` alanlarını yenile (runtime `code`'dan derler).

Dapr tarafı açıkça yapılandırılır: `compose.dapr-appconfig.yml` override'ı her sidecar'a
`--config /dapr/appconfig.yaml` (nameResolution `mdns` + OTel tracing, `dapr/appconfig.yaml`) ve
sabit `--dapr-internal-grpc-port 50002` verir. Şablonun kendisi `--config` taşımaz.

## Bilinen tuzaklar

- **DbMigrator `exit 139` / "Unable to resolve service for type IDomainDiscoveryResolver"** —
  migrator gateway'leri kaydeder ama discovery'yi kaydetmez; remote servisler fabrika ile kaydedilmeli
  (`AddRemoteService`, `RemoteServiceRegistrationTests` pinler). Görürsen imaj eski demektir.
- **`vNextApi__BaseUrl` localhost olmamalı** — registry'ye yazılan adres discovery konteynerinden
  erişilebilir olmalı (`http://vnext-app-{domain}:5000`). Development ortamında localhost kabul edilir
  ama diğer domain ona ulaşamaz.
- **`vnext-component-publisher` servisi bilinçli dışarıda** — şablon `core-runtime` paketini
  yayınlıyor; bizim bileşenler SDK / fixture ile gelir.
- **Port haritası** `create-domain.sh`'den: app `4201+offset`, init `3005+offset`, orchestration Dapr
  HTTP `42110+offset*100`. Offsetler: core 0, partner 10, discovery 30.
- **`data` fonksiyonu gövdeyi subflow'a indirmez**; yalnız `?extensions=` iner. Cross-domain descent
  testlerinde `view`/`schema`/`state`/`authorize` ve extension'lar ölçülür.
- Sidecar callee'ye ulaşamazsa HTTP 500 `{"errorCode":"ERR_DIRECT_INVOKE"}` döner;
  `DaprRemoteTransport` bunu `HttpRequestException` → `Error.Transient("remote_network_error")`'a çevirir.
