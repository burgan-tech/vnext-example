# script-perf-lab

Script compiler ve `ScriptContext` sıcak yollarının yük altındaki maliyetini ölçmek için kurulmuş
akışın **doğruluk** bekçisi. Entegrasyon testi tek bir instance'ı uçtan uca sürer ve veri setinin tam
olduğunu doğrular; **performans iddiası taşımaz** — sayılar `api-tests/script-perf-lab/perf-load.py`
ve akışın kendi README'sinin (`core/Workflows/script-perf-lab/README.md`) işidir.

## Neyi denetliyor

- **Auto transition zinciri**: `perf-initial` → `perf-stage-1..10` → `perf-fanout` → `perf-done`, hepsi
  `AlwaysTrueRule` ile; manuel adım yok.
- **Instance data append zinciri**: her stage'in onEntry script task'ı (`script-perf-task`, type 7)
  `stage{n}` altına `stamp` + `chunk` merge eder; on stage'in hepsi son veride bulunmalıdır.
- **`scripts.helpers` çok üyeli helper seti**: `perf-chunk-helper` 1.0.1 + `perf-stamp-helper` 1.0.0
  yalnız bu workflow'da bildirilir; `stamp`'ın `perf:{n}:` ile başlaması helper'ın çözüldüğünün kanıtı.
- **`FanOutTask` (type 21) inline mod**: `$.fanoutItems` üzerinden item başına HTTP child
  (`perf-item-http-task`), `ordered: true`, `join.policy: allSettled`, `resultKey: perfItemResults`;
  default paketleme `perfItemResults` satırlarını + `perfItemResultsSummary`'yi yazar.

## Akış

`script-perf-lab` v1.0.6 — JSON `build-script-perf-lab.py` ile üretilir.

```
start-script-perf-lab ─▶ perf-initial (Initial)
  ─auto─▶ perf-stage-1  onEntry: script-perf-task (StageMapping1: stage1 = {stamp, chunk[]})
  ─auto─▶ perf-stage-2 … perf-stage-10   (her biri aynı desen, StageMapping{n})
  ─auto─▶ perf-fanout   onEntry: script-perf-fanout-task (FanOut type 21, inline,
                                  maxDegreeOfParallelism 4, item 20 s, batch 120 s)
  ─auto─▶ perf-done (Finish/Success)
cancel-script-perf-lab ─▶ perf-cancelled
```

## Testler

| Test | Kanıtladığı | Not |
|---|---|---|
| `TenStages_WithHelpersAndFanOut_ReachDoneWithFullDataset` | `perf-done`'a varılır; `stage1..10`'un her birinde `stamp` (`perf:{n}:`) ve ≥2 segmentli `chunk` dizisi; `perfItemResults` 3 satır, hepsi `isSuccess`, `itemKey` sırası girişle aynı; summary `succeeded=3`, `failed=0`, `timedOut=false` | `chunkKb = 2`, 3 item, 180 sn bütçe |

## Bağımlılıklar ve ön koşullar

| Bağımlılık | Durum |
|---|---|
| Runtime | `core`, `VNEXT_BASE_URL` (`test.runsettings`: `http://localhost:4201`), lokal build |
| System package | Gerekmez — task'lar `core/Tasks/script-perf-lab/`, helper'lar `core/Mappings/script-perf-lab/` altında |
| MockLab | **Gerekir** — `fan-out-documents-collection.json`: `POST api/fan-out/documents/process` (fan-out item task'ı; `API_BASEURL` → `Example:ApiBaseUrl`) |
| Dapr | Akışa özgü bileşen yok |
| Caller rolleri / header'lar | Gerekmez |
| Cross-domain | Gerekmez |
| morph-idm provider | Gerekmez |

## Nasıl çalıştırılır

```bash
dotnet test tests/Core.IntegrationTests \
  --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~ScriptPerfLab"

# Yük / baseline (soğuk + sıcak faz, /metrics snapshot) — eşikler api-tests/script-perf-lab/README.md
python3 api-tests/script-perf-lab/perf-load.py --publish --parallel 20 --iterations 3 \
  --payload-kb 4 --fanout-count 25
```

## Dikkat noktaları / bilinen istisnalar

- **State başarıyı söylemez.** `allSettled` + koşulsuz `perf-fanout → perf-done` yüzünden kısmi item
  hatası da `perf-done`'a varır; başarı `perfItemResultsSummary`'den assert edilir.
- **`chunk` bir string değil, düğüm-zengin bir dizidir** (~1 KB'lık `{i, stage, seg}` nesneleri) —
  helper'ın per-node maliyet profilini tetiklemek için kasıtlı şekil; "boyut" kontrolünü string
  uzunluğuna çevirmeyin.
- **`npm run validate` fan-out task'ını reddeder** (`type: "21"`; kurulu vnext-schema enum'u `"20"`de
  bitiyor). Engel değil: SDK `definitions/publish` ile runtime'ın kendi doğrulamasını kullanır.
- **Helper referansları tam versiyonla authorlanmış** ve tam versiyon exact-match çözülür: yeni bir
  helper sürümü publish etmek tek başına görünmez; workflow'un referansı kısmi (`"1.0"`) olmalı ya da
  güncellenmelidir (akış README'si › Helper-hotfix canlı doğrulaması). O doğrulama runtime'da ekstra
  helper/workflow sürümleri bırakmıştır.
- Bileşenleri yeniden üretmek ya da soğuk cache için nonce bumplamak:
  `python3 core/Workflows/script-perf-lab/build-script-perf-lab.py --nonce N`. Publish
  versiyon-değişmezdir; değişen kaynak yeni bir sürümle publish edilmelidir.
