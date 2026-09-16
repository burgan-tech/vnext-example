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
| `ChainBusyAcceptTests` | Async accept, 202 dönmeden **önce** zinciri leaf'e kadar `Busy`'e çeker; relay gerçekten leaf'e ulaşır; **forward başarısız olduğunda rezervasyon geri alınır** (E31, aşağı bak). |
| `ChainBusySharedTransitionTests` | `$self` shared transition kendi işini yapar **ve** state yaşam döngüsünü koşar (onEntry/onExit girer, zamanlayıcıyı yeniden kurar) — `target: $self` "instance'ı oynatma" der, "hook'ları atla" demez; parent'ın kendi shared'ı parent'ta karşılanır (forward edilmez); yalnız leaf'te tanımlı olan aşağı forward edilir. |
| `ChainBusyUpdateDataTests` | `updateData` onEntry/onExit çalıştırmaz, zamanlayıcıyı yeniden kurmaz, state'i değiştirmez. Yaşam döngüsü atlamasını alan **tek** transition; `ChainBusySharedTransitionTests` ile birlikte sınırı pinler — birini diğeri olmadan değiştirmek sınırı sessizce siler. |
| `ChainBusyCancelTests` | Leaf'ten cancel → yukarı tamamlanma + korelasyon kapanır. Root'tan cancel → aşağı kaskad. |

## E31 — forward başarısız olduğunda zincir rezervasyonu

`PostCommitForwardFailure_ReleasesTheChainReserve_SoNoLevelStaysBusy` accept-time rezervasyonun
diğer yarısını pinler: uğruna alındığı forward hiç başarılı olmazsa ne olacağı.

`auto-leaf-to-waiting` leaf'in tanımında vardır ama `leaf-waiting` state'inde **kullanılabilir
değildir**. Root'ta böyle bir transition olmadığı için root isteği kabul eder, zinciri leaf'e kadar
`Busy` damgalar ve aşağı iletir; leaf de onu `Transition:100020` (Validation) ile reddeder.
Post-commit hata politikası Validation'ı **istemci hatası** sayıp fault'lamayı reddeder — ve o çıkış
ne settlement ne fault koşturur. Düzeltmeden önce rezervasyonu geri alan hiçbir şey yoktu.

Sonuç kalıcı bir mahsur kalmaydı: `Busy`'nin kurtarma API'si yok (retry `Faulted` ister), incident
de açılmıyor (`hasActiveIncident: false`), dolayısıyla instance veritabanına elle müdahale
edilmeden erişilemez hale geliyordu. Üretimde ölçülen: **30 günde 51 mahsur instance**, beş canlı
runtime sürümünün hepsinde (agent council `2026-09-08-parent-notification-mechanism`, P0).

Testin pinlediği üç şey:

1. **Client yeniden ilerleyebilir** — root'tan gözlenen durum `A`'ya döner (düzeltmeden önce
   süresiz `B` okunuyordu; ölçüldü: 90 saniye sonra hâlâ `B`).
2. **Atalar salınmaz** — root ve middle `B` kalır. Her biri açık SubFlow korelasyonu tutar ve
   çocuğunun ömrü boyunca meşru olarak `Busy`'dir; telafi yalnızca rezervasyonun çevirdiğini geri
   alır, hâlâ subflow ortasında olan bir parent'ı settle etmez.
3. **Akış gerçekten kullanılabilir** — ardından `finish-leaf` çalışır ve zincir `root-done`'a ulaşır.
   Mahsur kalmanın üretimdeki bedeli buydu: akış bir daha asla tamamlanamıyordu.

Ayırt edici koşu (2026-09-12, lokal stack): düzeltme `git stash` ile çıkarılıp host'lar yeniden
derlendiğinde test düşer (31 sn, zincir `B`'de kalır); geri konduğunda ChainBusy takımının tamamı
(15 test) geçer.

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
