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
