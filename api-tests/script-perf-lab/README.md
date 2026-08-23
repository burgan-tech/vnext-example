# script-perf-lab — makro baseline yük testi

`api-tests/script-perf-lab/perf-load.py` script-ağırlıklı `script-perf-lab` akışının (bkz.
`core/Workflows/script-perf-lab/README.md`) soğuk/sıcak fazlarını koşar ve Katman 0 script
metriklerinin (`script_compilations_total`, `script_execution_duration_seconds`) yük altındaki
delta'sını raporlar. Katman 1-3 compiler/serialization optimizasyonlarının önce/sonra referans
ölçümü budur (spec: vnext `docs/superpowers/specs/2026-08-23-script-perf-katman0-design.md`).

## Neyi ölçüyor

1. **Soğuk faz** — tek instance, ilk-dokunuş script derleme (compile-miss) maliyeti. Anlamlı olması
   için bileşenlerin **taze bir nonce** ile üretilmiş olması gerekir
   (`build-script-perf-lab.py --nonce N`) — aynı nonce'la ikinci koşu compile cache'ine çarpar ve
   sıcak-faz süresine yakın bir latency döner. `--skip-cold` ile atlanabilir.
2. **Sıcak faz** — `--iterations` tur × `--parallel` eş zamanlı instance; her instance 10 stage'in
   hepsinde bir `ScriptTask` ile instance data'yı `--payload-kb` boyutunda büyütür (append zinciri,
   B9 O(n²) profili), sonra `--fanout-count` item'i `FanOutTask` (type 21) ile HTTP child olarak
   fan-out eder (B6 branch klonu). Tüm turların latency'lerinden p50/p95/p99 hesaplanır.
3. **Metrics delta** — sıcak fazdan önce ve sonra hem orchestration (`{base-url}/metrics`) hem
   execution (`:4202/metrics`) uçlarından `script_` ile başlayan satırlar toplanır,
   `results/metrics-{before|after}-{timestamp}.txt`'ye yazılır; stdout'a
   `script_compilations_total{result}` (hit/miss) ve
   `script_execution_duration_seconds_count{script_type}` delta özeti basılır.

## Bağımlılıklar

Yalnız Python 3 standart kütüphanesi (`urllib`, `json`, `statistics`, `concurrent.futures`) — ek
kurulum gerekmez. Çalışan bir lokal vNext stack'i şart:

- vNext altyapısı (`etc/docker/run-docker.sh`, vnext reposunda)
- Orchestration (`--launch-profile http`, 4201) + Execution (`--launch-profile http`, 4202)
- MockLab (mevcut `fan-out-documents-collection.json` mock'u yeniden kullanılır — yeni seed yok)

## Publish sırası

`publish.py`, `COMPONENTS` listesindeki sırayla (**helper → task → workflow**) publish eder:
workflow'un `scripts.helpers` referansları ve `perf-fanout` state'inin
`script-perf-fanout-task` / `perf-item-http-task` referansları publish anında çözülür — ters sırada
referans bulunamaz. `definitions/publish` aynı key+version'ı içerik değişse de **409** ile
reddeder; bir `.csx` değiştiyseniz `build-script-perf-lab.py --nonce N` ile versiyonu yükseltmeden
publish sessizce eski script'i servis etmeye devam eder.

**Integration test suite'i (`Tests/ScriptPerfLab`) bunu kendisi yapar**
(`VNextTestEnvironment.EnableDomainPublish`) — bu script elle çalıştırma ve `perf-load.py --publish`
bayrağı içindir.

```bash
python3 api-tests/script-perf-lab/publish.py
```

## Komutlar

```bash
# soğuk + sıcak faz, publish dahil, varsayılan parametrelerle
python3 api-tests/script-perf-lab/perf-load.py --publish --parallel 20 --iterations 3 \
    --payload-kb 4 --fanout-count 25

# yalnız sıcak faz (bileşenler zaten publish edilmiş, taze nonce yok)
python3 api-tests/script-perf-lab/perf-load.py --skip-cold --parallel 20 --iterations 3

# soğuk ölçüm için taze nonce ile yeniden üret, sonra tek instance'lık soğuk+sıcak koşu
python3 core/Workflows/script-perf-lab/build-script-perf-lab.py --nonce 2
python3 api-tests/script-perf-lab/perf-load.py --publish --parallel 1 --iterations 1 \
    --payload-kb 4 --fanout-count 25 --timeout 300
```

| Parametre | Varsayılan | Anlamı |
| --- | --- | --- |
| `--base-url` | `http://localhost:4201` | orchestration base URL; execution `/metrics` ucu `:4201`→`:4202` ile türetilir |
| `--parallel` | 20 | sıcak faz tur başına eş zamanlı instance |
| `--iterations` | 3 | sıcak faz tur sayısı |
| `--payload-kb` | 4 | start body `chunkKb` — stage başına instance data büyümesi |
| `--fanout-count` | 25 | `perf-fanout` item sayısı (`fanoutItems`) |
| `--timeout` | 300 | instance başına settle bütçesi (s) |
| `--publish` | kapalı | ölçümden önce `publish.py`'yi çalıştırır |
| `--skip-cold` | kapalı | soğuk fazı atlar, doğrudan sıcak faza geçer |

## Başarısızlık eşikleri

| Kontrol | Eşik | Aşılırsa ne demek |
| --- | --- | --- |
| Faulted (`F`) | 0 | herhangi bir instance fault'landıysa **FAIL** — akış veya script hatası |
| TIMEOUT oranı | `≤ %5` | settle bütçesi (`--timeout`) içinde terminale ulaşmayan instance oranı yüksekse **FAIL** |
| START-FAIL | 0 | instance hiç başlamadıysa (start isteği reddedildi) **FAIL** — pratik ek guvence, F/TIMEOUT'a girmeyen bir başarısızlık sınıfı |

Hepsi geçerse çıkış kodu `0`, biri düşerse `1`.

## Sonucun okunması

- **Latency p50/p95/p99** — sıcak fazın tüm turlarından biriken settle süreleri (saniye). 20'den
  az örneklemde `statistics.quantiles` yerine sıralı liste indekslemesi kullanılır (küçük
  örneklemde quantile interpolasyonu güvenilmez).
- **`script_compilations_total{result}` delta** — `hit` sayısı arttıkça sağlıklı (compile cache
  isabet ediyor); ideal profilde sıcak faz boyunca yalnızca `hit` artar, `miss` **sabit kalır**
  (yeni script derlenmiyor). Sıcak fazda `miss` artıyorsa cache'in beklenmedik şekilde tahliye
  olduğu veya paylaşılan ALC'nin yeniden yüklendiği anlamına gelir.
- **`script_execution_duration_seconds_count{script_type}` delta** — script tipine göre (stage
  mapping'leri, fan-out item mapping'i, AlwaysTrueRule vb.) kaç script çalıştırıldığını gösterir;
  beklenen sayı `--iterations × --parallel × (10 stage + 1 fanout mapping + auto rule'lar)`
  civarında olmalı. Sapma, bir stage'in atlandığı veya beklenenden fazla tekrar ettiği anlamına
  gelebilir.
- **Sonuç dosyaları** `results/metrics-{before|after}-{timestamp}.txt` altında commit
  edilebilir durumda kalır (`.gitignore`'a eklenmez) — her koşu kendi zaman damgalı dosyasını
  yazar, üzerine yazma riski yoktur. `results/.gitkeep`, klasörün boşken de repoda görünmesi
  içindir.
- **Soğuk faz uyarısı** — script her koşuda "soğuk faz ancak taze nonce'la anlamlı" notunu basar;
  bu bir hata değil, ölçümün geçerlilik koşulunu hatırlatan bilgilendirmedir (script publish
  edilmiş bir nonce'ın taze olup olmadığını kendi başına tespit edemez).

## İlgili dosyalar

- `core/Workflows/script-perf-lab/README.md` — akışın kendisi (state şeması, neden var, senaryo
  bazlı beklenen sonuç).
- `tests/Core.IntegrationTests/Tests/ScriptPerfLab/` — doğruluk testi (perf iddiası taşımaz).
- `TEST-SCENARIOS.md` — senaryo indeksi, feature seti eşlemesi.
