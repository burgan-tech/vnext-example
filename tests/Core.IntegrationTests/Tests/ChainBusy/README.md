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
| `ChainBusyAcceptTests` | Async accept (SubFlow transition proxy), 202'den sonraki **ilk** poll'de `B` gösterir; istek gerçekten leaf'e ulaşır; **leaf'in reddettiği forward'lanabilir istek hatayı senkron döner ve hiçbir seviye `Busy` kalmaz** (E31'in yeni biçimi, aşağı bak). **2026-10-08: accept-time chain reserve yerine proxy** (vnext `feature/file-offload-x-storage`, client-sdk-core#101). |
| `ChainBusySharedTransitionTests` | `$self` shared transition kendi işini yapar **ve** state yaşam döngüsünü koşar (onEntry/onExit girer, zamanlayıcıyı yeniden kurar) — `target: $self` "instance'ı oynatma" der, "hook'ları atla" demez; parent'ın kendi shared'ı parent'ta karşılanır (forward edilmez); yalnız leaf'te tanımlı olan aşağı forward edilir. |
| `ChainBusyUpdateDataTests` | `updateData` onEntry/onExit çalıştırmaz, zamanlayıcıyı yeniden kurmaz, state'i değiştirmez. Yaşam döngüsü atlamasını alan **tek** transition; `ChainBusySharedTransitionTests` ile birlikte sınırı pinler — birini diğeri olmadan değiştirmek sınırı sessizce siler. |
| `ChainBusyCancelTests` | Leaf'ten cancel → yukarı tamamlanma + korelasyon kapanır. Root'tan cancel → aşağı kaskad. |

## E31 — leaf'in reddettiği forward: önce rezervasyon salımı, şimdi proxy

> **Değişti (2026-10-08, vnext `feature/file-offload-x-storage`, vnext-client-sdk-core#101 Faz 2).**
> Accept-time chain reserve kalktı; yerine **proxy** geldi: aktif `S` subflow'u olan parent,
> forward'lanabilir bir transition'ı parent'ın çözdüğü modda leaf'e iletir; parent kilit almaz,
> Busy çevirmez, job kuyruğa koymaz. Admission (validation, Busy CAS, kendi job'u) **leaf'te**
> yapılır. Leaf'in hatası artık **senkron** istemciye döner (202 + sonradan salım yok). Test bu
> yüzden yeniden yazıldı: `PostCommitForwardFailure_ReleasesTheChainReserve_SoNoLevelStaysBusy` →
> `ForwardableRequestTheLeafRejects_ReturnsTheLeafsErrorSynchronously_SoNothingStaysBusy`.
> "202'den sonraki ilk poll `B` görür" garantisi artık leaf'in kendi Busy CAS'inden (202'den önce
> commit) ve parent `EffectiveStatus`'unun önceden `Busy` damgalanmasından gelir. Aşağıdaki E31
> geçmişi silinmedi; eski mekanizmayı ve neden var olduğunu anlatır.

### Yeni test (proxy)

`auto-leaf-to-waiting` leaf'te otomatik bir transition'dır; user actor onu tetikleyemez. Root'ta
böyle bir transition olmadığı için root isteği leaf'e proxy'ler ve leaf admission'ı reddeder:
**403 Forbidden, `Transition:100010`**; yanıttaki `target` **leaf instance id**'sidir (hatanın
root'un değil leaf'in kendi reddi olduğunun kanıtı). Test şunları pinler:

1. **Hata senkron döner** — 202 değil, 403 + `Transition:100010` + `target == leafId`.
2. **Salınacak bir şey yok** — root'tan gözlenen durum `A`, leaf `A` ve dinlenme state'inde
   (`leaf-waiting`).
3. **Atalar dokunulmamış** — root ve middle açık SubFlow korelasyonu yüzünden yapısal olarak `B`
   kalır; proxy parent'a hiçbir şey yazmaz.
4. **Akış kullanılabilir** — ardından `finish-leaf` çalışır ve zincir `root-done`'a ulaşır.

### Eski mekanizma (E31 geçmişi)

`auto-leaf-to-waiting` leaf'in tanımında vardır ama `leaf-waiting` state'inde **kullanılabilir
değildir**. Eski runtime'da root isteği kabul eder, zinciri leaf'e kadar `Busy` damgalar ve aşağı
iletirdi; leaf de onu `Transition:100020` (Validation) ile reddederdi.
Post-commit hata politikası Validation'ı **istemci hatası** sayıp fault'lamayı reddeder — ve o çıkış
ne settlement ne fault koşturur. Düzeltmeden önce rezervasyonu geri alan hiçbir şey yoktu.

Sonuç kalıcı bir mahsur kalmaydı: `Busy`'nin kurtarma API'si yok (retry `Faulted` ister), incident
de açılmıyor (`hasActiveIncident: false`), dolayısıyla instance veritabanına elle müdahale
edilmeden erişilemez hale geliyordu. Üretimde ölçülen: **30 günde 51 mahsur instance**, beş canlı
runtime sürümünün hepsinde (agent council `2026-09-08-parent-notification-mechanism`, P0).

Eski testin pinlediği üç şey: client yeniden ilerleyebilir (root'tan gözlenen `A`'ya döner; düzeltme
öncesi 90 sn sonra hâlâ `B`), atalar salınmaz, akış gerçekten kullanılabilir.

Ayırt edici koşu (2026-09-12, lokal stack, eski mekanizma): düzeltme `git stash` ile çıkarılıp
host'lar yeniden derlendiğinde test düşer (31 sn, zincir `B`'de kalır); geri konduğunda ChainBusy
takımının tamamı (15 test) geçer.

### Proxy gecikmesi (2026-10-08, istemci tarafı)

`api-tests/chain-busy/chain-busy-proxy-latency.py` (N=50, 3 ısınma atılır): aynı async
`leaf-only-mark` transition'ı sırayla (a) **root'a** (aktif S zincirinin parent'ı → proxy) ve
(b) **doğrudan leaf'e** gönderilir; HTTP yanıt süresi ölçülür. Her istekten önce leaf'in `A`'ya
dönmesi beklenir (bekleme ölçüme girmez).

| Yol | p50 | p95 | max | ortalama |
| --- | --- | --- | --- | --- |
| proxied (root) | 17.4 ms | 36.1 ms | 39.3 ms | 20.0 ms |
| direct (leaf) | 12.1 ms | 35.7 ms | 103.0 ms | 16.7 ms |

p50 farkı ≈ **+5 ms** (proxy'nin eklediği hop + parent pre-stamp). İlk koşuda aynı yönde:
proxied p50 19.0 / p95 40.7, direct p50 13.1 / p95 22.3.

Sınırlar: **istemci tarafı** (OpenObserve/Elasticsearch yoktu, sunucu span'i ölçülmedi), lokal
loopback, tek runtime, tek zincir (A→B→C), N=50 olduğu için p95/max GC/Dapr/scheduler gürültüsüne
açık (direct max 103 ms tek bir sıçrama). Direct istekler arada `409 Transition:100009` ("already
being processed") aldı — leaf görünür `A`'ya döndüğünde önceki isteğin ayni-key koruması henüz
düşmemişti; bu istekler ölçüme alınmadan beklenip yeniden denendi (4/50). Mutlak değerler üretim
için değil, göreli karşılaştırma içindir.

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

Ayakta bir stack'e karşı (4201/4202 + Inbox/Outbox worker'ları) art arda üç koşu: **15/15** (eski chain-reserve runtime'ı). 2026-10-08, proxy runtime'ı (`feature/file-offload-x-storage`): `ChainBusy`+`SubflowOrchestration` **37/37** (E31 testi yeniden yazıldıktan sonra; yazılmadan önce 36/37 — eski test, root'un leaf reddini senkron Forbidden döndürmesi yüzünden kırmızıydı), `FileOffloadLab` 19/19.
Aynı davranışların script karşılığı `api-tests/chain-busy/` altındadır; ikisi aynı senaryoları
ölçer, script keşif ve hızlı tekrar için, bu testler ise CI kontrol noktası için.
