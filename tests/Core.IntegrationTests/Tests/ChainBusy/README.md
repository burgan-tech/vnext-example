# chain-busy davranış testleri

Bu test sınıfları vNext platformunun **davranışsal kontrol noktasıdır**. Platform tarafında bir
değişiklik yapıldığında (pipeline adımları, admission, kilitleme, subflow relay, `$self` profili),
etkisinin buradan görülmesi beklenir.

Kapsanan davranışlar `core/Workflows/chain-busy/` altındaki üç akış üzerinden ölçülür:
`chain-busy-root` (A) → `chain-busy-middle` (B) → `chain-busy-leaf` (C).

## Neden bu akış

Zincir tamamen auto transition ile kurulur; A başlatıldığında C `leaf-waiting`'de `Active` bekler,
A ve B ise açık SubFlow korelasyonu boyunca **yapısal olarak** `Busy` olur. Bu, davranışı
gözlemlenebilir kılan iki özellik sağlar:

- Ata seviyelerin `Busy`'si hiçbir bilgi taşımaz — state function en derindeki aktif subflow'u
  raporladığı için **client'ın gördüğü tek sinyal C'dir**.
- Her onEntry / onExit / onExecute bir sayaç task'ı çalıştırır ve sayaç instance verisine yazılır.
  `leaf-waiting`'de 30 dakikalık, asla ateşlenmeyen bir scheduled transition kurulur; tek görevi
  ARMED bir zamanlayıcı bırakmaktır — `executeAtUtc` değişmediyse yeniden kurulmamış demektir.

Her şey **public API'den** doğrulanır: `GET /instances/{id}` sayaçları `attributes` altında,
state function ise zinciri (`activeCorrelations`) ve armed scheduled kayıtlarını döner. Veritabanı
erişimi gerekmez.

## Sınıflar

| Sınıf | Doğruladığı |
| --- | --- |
| `ChainBusyStartTests` | Start, başlangıç state'inin onEntry'sini çalıştırır (üst seviye + subflow); zincir kurulduğunda A/B `Busy`, C `Active`. |
| `ChainBusyAcceptTests` | Async accept, 202 dönmeden **önce** zinciri leaf'e kadar `Busy`'e çeker; relay gerçekten leaf'e ulaşır. |
| `ChainBusySharedTransitionTests` | `$self` shared transition kendi işini yapar **ve** state yaşam döngüsünü koşar (onEntry/onExit girer, zamanlayıcıyı yeniden kurar) — `target: $self` "instance'ı oynatma" der, "hook'ları atla" demez; parent'ın kendi shared'ı parent'ta karşılanır (forward edilmez); yalnız leaf'te tanımlı olan aşağı forward edilir. |
| `ChainBusyUpdateDataTests` | `updateData` onEntry/onExit çalıştırmaz, zamanlayıcıyı yeniden kurmaz, state'i değiştirmez. Yaşam döngüsü atlamasını alan **tek** transition; `ChainBusySharedTransitionTests` ile birlikte sınırı pinler — birini diğeri olmadan değiştirmek sınırı sessizce siler. |
| `ChainBusyCancelTests` | Leaf'ten cancel → yukarı tamamlanma + korelasyon kapanır. Root'tan cancel → aşağı kaskad. |

## Çalıştırma

### Konteynerli ortam (varsayılan)

```bash
dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~ChainBusy"
```

SDK, Docker üzerinde postgres/redis/vault/dapr/orchestrator/execution/mocklab ayağa kaldırır,
db-migrator'ı koşturur ve `core/**` altındaki bileşenleri yayınlar. İlk açılış imaj indirmeleri
yüzünden dakikalar sürebilir.

### Ayakta olan bir stack'e karşı (hızlı döngü)

```bash
VNEXT_BASE_URL=http://localhost:4201 \
VNEXT_IT_SKIP_PUBLISH=1 \
dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~ChainBusy"
```

| Değişken | Anlamı |
| --- | --- |
| `VNEXT_BASE_URL` | Konteyner başlatmayı atlar, verilen orchestrator'a bağlanır. |
| `VNEXT_IT_SKIP_PUBLISH=1` | Domain publish'i atlar. Publisher sürümleri `{v}-pkg.{paket}+{domain}` olarak yeniden yazdığı için, zaten yüklü bir stack'te bunu atlamak gerekir. |

## Aşağı yönlü cancel kaskadı

İki cancel yönü farklı yollardan gider. Leaf'ten cancel, korelasyonu kapatıp parent'ı
**in-process** devam ettirir. Root'tan cancel ise her açık korelasyon için
`ChildSubflowCancelRequestedEvent` üretir; bu **distributed event**'tir ve alt seviyelere
Outbox worker yayınlayıp Inbox worker tükettiğinde ulaşır.

SDK artık Inbox/Outbox'ı desteklediği için `CancelOnTheRoot_CascadesDownTheWholeChain`
ayrı bir ortam değişkeni ya da gate gerektirmeden koşar. Kaskad nihai tutarlı olduğundan
testin bekleme payı diğerlerinden uzundur (120 sn).

## Doğrulama durumu

Ayakta bir stack'e karşı (4201/4202 + Inbox/Outbox worker'ları) art arda üç koşu: **15/15**.
Aynı davranışların script karşılığı `api-tests/chain-busy/` altındadır; ikisi aynı senaryoları
ölçer, script keşif ve hızlı tekrar için, bu testler ise CI kontrol noktası için.
