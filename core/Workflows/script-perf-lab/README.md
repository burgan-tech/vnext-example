# script-perf-lab

## Neyi denetliyor

Script compiler ve `ScriptContext` sıcak yollarının (compile-hit sabiti, `scripts.helpers`
çok üyeli seti, instance-data append zinciri, `FanOutTask` inline branch klonu) yük altındaki
maliyetini denetler; Katman 0 metriklerinin (compile hit/miss sayaçları, script execution
duration) gerçek bir akışta doğru üretildiğini doğrular.

## Neden var

Katman 0 ölçüm altyapısının makro baseline'ı — Katman 1-3 compiler/serialization
optimizasyonlarının gerçek-yük önce/sonra referansı olarak eklendi (2026-08-23, vnext
`feature/script-perf-katman0`; spec: vnext
`docs/superpowers/specs/2026-08-23-script-perf-katman0-design.md`).

## Akış şeması

```
perf-initial
   └─ auto ─▶ perf-stage-1 ... perf-stage-10
                 (her girişte onEntry ScriptTask: instance data'ya chunkKb boyutunda
                  deterministik chunk merge eder — doküman lineer büyür, append maliyeti
                  kareselleşir, B9 profili)
                 └─ auto ─▶ perf-fanout
                               (FanOutTask type 21, inline mode, $.fanoutItems üzerinden
                                N item × HTTP child → mevcut fan-out-documents MockLab
                                mock'u, allSettled join)
                               └─ auto ─▶ perf-done
```

Kritik adımlar: **perf-stage-10** (en büyük doküman üzerinde son append — B9 O(n²) profilinin
tepe noktası) ve **perf-fanout** (en büyük `Body`'nin item başına klonlanması — B6 branch
klonu maliyeti).

İki helper (`perf-chunk-helper`, `perf-stamp-helper`) yalnızca bu workflow'un kendi
`scripts.helpers` alanında bildirilir — A7 çok üyeli helper set yolunu tetikler. `cancel`
hedefi `perf-cancelled`, `start` hedefi `perf-initial`'dır.

## Nasıl çalıştırılır

Ön koşullar: vNext altyapısı + 4 app (`--launch-profile http`) + MockLab ayakta; bileşenler
yayınlanmış (`api-tests/script-perf-lab/publish.py`).

Üretici (bileşenleri yeniden üretmek / nonce bumplamak için):

```bash
python3 core/Workflows/script-perf-lab/build-script-perf-lab.py --nonce 1
```

Integration test (doğruluk — perf iddiası yok):

```bash
VNEXT_BASE_URL=http://localhost:4201 \
  dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~ScriptPerfLabTests"
```

Yük / baseline (soğuk + sıcak faz, metrics snapshot) — bkz.
`api-tests/script-perf-lab/README.md`:

```bash
python3 api-tests/script-perf-lab/perf-load.py --publish --parallel 20 --iterations 3 \
  --payload-kb 4 --fanout-count 25
```

## Beklenen sonuç / başarı kriteri

- Integration test yeşil: `perf-done`'a ulaşılır, `stage1..stage10` hepsi merge edilmiş
  (`stamp` + beklenen boyutta `chunk`), `perfItemResults` + `perfItemResultsSummary` dolu.
- `join.policy: allSettled` + `perf-fanout → perf-done` koşulsuz auto transition nedeniyle
  **kısmi item hatası da `perf-done`'a ulaşır** — state bunu ayırt etmez. Test başarı/
  başarısızlığı bu yüzden state'ten değil, `perfItemResultsSummary`'den (`succeeded`/`failed`)
  assert eder.
- Yük koşusunda **0 Faulted**; TIMEOUT oranı ≤ %5 (eşikler `api-tests/script-perf-lab/README.md`'de).
- Baseline tabloları (soğuk/sıcak latency, dotnet-counters özeti, metrics delta) bu README'nin
  "Sonuçlar" bölümüne Task 4 tamamlandığında işlenir — bu bölüm henüz eklenmedi.

## Bilinen kısıt

`npm run validate`, `script-perf-fanout-task.json`'ı (`attributes.type = "21"`) reddeder —
`@burgan-tech/vnext-schema` paketindeki task tipi enum'u `"20"`de bitiyor. Bu, aynı runtime
tipini kullanan `fan-out-documents` ve `fan-out-config-matrix` senaryolarında da görülen,
bilinen ve dokümante edilmiş bir şema paketi açığıdır (bkz. `TEST-SCENARIOS.md`) — engelleyici
değildir, çünkü `definitions/publish` runtime'ın kendi doğrulamasını kullanır, bu npm şema
kontrolünü değil.

## Sonuçlar — Makro Baseline (2026-08-23, optimizasyon ÖNCESİ)

**Ortam:** lokal 4-app koşumu (`--launch-profile http`), vnext branch `feature/script-perf-katman0`
(runtime kodu `ce92aade` itibarıyla; koşum anındaki HEAD `d7cbad98`, sonrası docs-only).
vnext-example `feature/script-perf-lab` @ `013189a`. Sandbox: orchestration açık, execution kapalı.
DI smoke: 4/4 host ilk denemede healthy (21 executor ctor değişikliği sorunsuz).
Integration test: 1/1 yeşil.

### Soğuk faz (taze nonce=3, ilk dokunuş)

| Metrik | Değer |
|---|---|
| coldLatency (start→C) | **1.62 s** |
| Sıcak 1×1 kontrol | 1.54 s |

### Sıcak faz (3 tur × 20 paralel = 60 instance, closed-loop)

| payload-kb | p50 | p95 | p99 | Sonuç | compile hit/miss delta |
|---|---|---|---|---|---|
| 4 | 5.78 s | 5.93 s | 6.33 s | 60/60 C | +1980 / **+0** |
| 16 | 6.23 s | 8.02 s | 8.03 s | 60/60 C | +1980 / **+0** |

- Hit sayısı instance başına tam **33** (10 stage × input+output + 12 condition + fanout) — deterministik.
- `miss=+0`: compile cache ideal; scriptType kırılımı: condition +720, task-input +660, task-output +660 (koşu başına).
- payload 4×↑ → p95 +%35: büyüyen dokümanın append maliyeti (B9) gerçek yükte görünür.

### dotnet-counters (yük penceresi ~70 s, iki sıcak koşu birlikte; Server GC)

| Host | Alloc toplam | Alloc ort/tepe | GC pause | gen0/1/2 | LOH max | WS max | CPU(user) |
|---|---|---|---|---|---|---|---|
| Orchestration | **8.10 GB** (~67 MB/instance) | 116 / **291 MB/s** | 0.61 s (%0.9) | 9/10/17 | **207.8 MB** | 679 MB | 34.6 s |
| Execution | 2.78 GB | 40 / 131 MB/s | 0.24 s (%0.3) | 1/4/**39** | 150.4 MB | 287 MB | 9.0 s |

**Okuma:** allocation hacmi (instance başına ~67 MB!) ve LOH büyümesi, analizin B6 (FanOut branch
klonu) + B9 (append zinciri) + B1-B3 (expando yeniden inşası) kalemleriyle uyumlu — Katman 1-3'ün
hedefi bu tabloyu küçültmek. Ham veriler: `api-tests/script-perf-lab/results/`
(`counters-*-kb4.csv` iki koşuyu da kapsar, `metrics-{before|after}-*.txt`).
Mikro baseline karşılığı: vnext `test/BBT.Workflow.Benchmarks/baselines/2026-08-23-master.md`.

**Koşum notları:** İlk koşumda CS0012 kök nedenli bir fixture hatası bulundu ve üreticide
düzeltildi (`013189a` — ExpandoObject cast'i dynamic üzerinden; statik cast sandbox altında
System.ObjectModel referansı ister). TIMEOUT/başarısızlık yok; MockLab 25 item × 20 paralelde
doygunluk göstermedi.

## Katman 1 sonrası (2026-08-23) — compiler hit-yolu optimizasyonları

**Runtime:** vnext `feature/script-perf-katman0` @ Katman 1 tamamlanmış durumda (per-task compile memo,
ContentHash+profil anahtarı, generation-token bekçili helper-set memo'su, factory delegate).

### Sıcak faz karşılaştırması (3×20, aynı parametreler)

| Metrik | Baseline | Katman 1 | Δ |
|---|---|---|---|
| 4KB p50 / p95 | 5.78 / 5.93 s | **4.85 / 5.84 s** | **-16% / -1.5%** |
| 16KB p50 / p95 / p99 | 6.23 / 8.02 / 8.03 s | **5.74 / 6.27 / 6.30 s** | -8% / **-22% / -22%** |
| compile hit/instance | 33 | **23** | -30% (öngörü birebir) |
| compile miss (sıcak) | +0 | **+0** | anahtar doğruluğu yük altında korunuyor |
| Orch alloc (yük penceresi) | 8.10 GB | 7.87 GB | -2.8% |
| Orch CPU(user) | 34.6 s | 30.6 s | -12% |
| Orch GC pause / gen0 | 0.61 s / 9 | 0.46 s / 5 | -25% / -44% |
| Soğuk (adil: ısınmış process + taze nonce) | 1.62 s | 1.57 s | ~aynı (Roslyn-baskın, beklenen) |

**Okuma:** Katman 1 latency/CPU'daki sabit compiler bedelini kaldırdı; allocation dağı (instance
başına ~65 MB) beklendiği gibi yerinde — o, serialization katmanının (B6 fan-out klonu + B9 append,
Katman 2) hedefi. Mikro karşılık: identity yolu düz 1.67 µs / 8.67 KB (kaynak boyutundan bağımsız);
bkz. vnext `test/BBT.Workflow.Benchmarks/baselines/2026-08-23-master.md` § Katman 1 sonrası.

### Helper-hotfix canlı doğrulaması (floating çözümleme + token bekçisi)

Çalışan process'te, workflow'a dokunmadan:
1. Authored `perf-stamp-helper@1.0.0` (TAM versiyon) + `1.0.1` publish → eski içerik servis edildi —
   **runtime semantiği**: tam versiyon exact-match (`CacheSet.GetByVersionAsync` → `IsFullVersion`).
2. Authored **`"1.0"`** (kısmi) yapılan workflow v1.0.99 + `1.0.1` publish → **`:v101` görüldü**.
3. Aynı process'te `1.0.2` publish (re-initialize YOK) → sonraki instance **`:v102` gördü** —
   helper-set memo'su generation-token bump'ıyla kendiliğinden düştü.

Sonuç: memo hiçbir senaryoda memo'suz davranıştan sapmıyor; "hotfix prod'da görünmedi" sınıfı risk
bu memo için kapalı. NOT: bu doğrulama runtime'da `perf-stamp-helper` 1.0.1/1.0.2 ve workflow
v1.0.99'u publish edilmiş bırakır (repo dosyaları değişmedi); sonraki koşullar nonce'larını
artırarak ilerler.

## Katman 2 sonrası (2026-08-23) — serialization/ScriptContext optimizasyonları

**Runtime:** vnext `feature/script-perf-katman0` @ Katman 2 tamamlanmış (okuma memo'ları, tek-geçişli
canonicalizer append + kill-switch, COW branch, audit referansı).

### Sonuçlar (3×20, aynı parametreler; kanonik koşum)

| Metrik | Baseline | Katman 1 | Katman 2 |
|---|---|---|---|
| Soğuk (adil) | 1.62 s | 1.57 s | **1.08 s (-33%)** |
| 4KB p50 / p95 | 5.78 / 5.93 | 4.85 / 5.84 | 5.68 / 6.31 |
| 16KB p50 / p95 | 6.23 / 8.02 | 5.74 / 6.27 | 5.92 / 6.59 |
| Orch alloc (yük penceresi) | 8104 MB | 7870 MB | **7259 MB (-10.4%)** |
| Orch LOH max | 207.8 MB | 194.8 MB | **177.7 MB (-15%)** |
| Orch GC pause | 0.61 s | 0.46 s | **0.40 s (-35%)** |
| compile hit/instance · miss | 33 · +0 | 23 · +0 | **23 · +0 (korunuyor)** |
| Integration (geniş set) | — | — | **37/37** (script-perf-lab + chain-busy + fan-out) |
| Kill-switch canlı testi | — | — | ✅ legacy konumda 1/1 (env üzerinden) |

**Dürüst okuma:** (1) Latency farkları K1↔K2 arasında bu makinenin kanıtlanmış gürültü bandında —
aynı 16KB konfigürasyonu ardışık iki koşuda 11.08s ve 6.38s okudu (ilk okuma collector-başlangıcı/GC
artçısıyla kirliydi ve ATILDI); p95'ler baseline'a karşı hâlâ net iyi. (2) Mikro seviyedeki devasa
kazançlar (branch klonu 1.29ms/1.7MB → 102ns/992B; canonicalizer -66% süre) makro allokasyonun
~%8-10'una tekabül etti: kalan ~7.2GB'ın sahipleri bu katmanın hedeflediği yollar DEĞİL
(EF/persist, HTTP/Dapr, pipeline altyapısı). Bir sonraki tur için işaret: allocation profiling
(dotnet-gcdump/trace) ile yeni dağ sahiplerini bulmak. (3) EXEC tarafı beklendiği gibi düz
(K2 yolları orada koşmuyor). Ham veriler: `results/counters-*-k2.csv` (kanonik pencere
21:08:45-21:09:35 lokal), metrics-*.txt.
