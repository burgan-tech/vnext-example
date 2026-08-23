# l1-cache-lab — L1 Component Cache Davranış Senaryosu

## Neyi denetliyor

Runtime'daki **generation-anahtarlı L1 (in-process) component cache**'inin publish
görünürlüğünü bozmadığını: yeni bir component versiyonu publish edildiği an — hiçbir
re-initialize/bekleme olmadan — `latest` ve range (`"1"`) çözümlemelerinin **sıcak bir L1'e
rağmen** yeni versiyona dönmesini, pinned instance'ların ise eski versiyonda kalmasını doğrular.

## Neden var

vNext runtime'a eklenen L1 component cache (bkz. vnext repo,
`docs/domain/domain-cache-context.md` + `docs/superpowers/specs/2026-08-20-component-cache-l1-design.md`,
branch `feature/component-cache-l1`, 2026-08-20) Dapr state (Redis) okuma maliyetini düşürür.
Tasarımın korkulan riski "versiyon cache'de kaldı" senaryosudur: CD tamamlandığında yeni
sürümler **mutlaka** geçerli olmalıdır. L1 anahtarları generation token'ı içerdiği için bu
teorik olarak imkânsızdır; bu senaryo o iddiayı uçtan uca, gerçek publish akışıyla kanıtlar
ve gelecekteki cache değişikliklerinde regresyon bekçisi olarak durur.

## Akış şeması

```
start-l1-cache-lab ──> ready ──probe──> probed ──finish──> lab-done
                        ^                 │
                        └──────back───────┘         (cancel ──> lab-cancelled)
```

- `ready` ve `probed` state'leri `l1-lab-view`'ı **`"1"` (major range)** ile referanslar.
- `probe` transition'ı `l1-lab-task`'ı (HTTP task, MockLab `POST /api/test/process`)
  **`"1"` range** ile koşturur; task config body'sindeki `taskVersion` alanı hangi task
  SÜRÜMÜNÜN çalıştığının kanıtı olarak `InstanceTasks.Request`'e persist olur.
- Kritik adımlar: (1) publish öncesi aynı view 3 kez okunarak L1 **ısıtılır** — test soğuk
  cache'i değil, sıcak L1'in bayatlamamasını ölçer; (2) yeni versiyonlar publish edilir
  edilmez, re-initialize çağrılmadan görünürlük assert edilir.

## Component dosyaları

Her versiyon **ayrı dosyadır** (kanonik örnekler):

```
components/l1-lab-task.1.0.0.json    components/l1-lab-task.1.1.0.json
components/l1-lab-view.1.0.0.json    components/l1-lab-view.1.1.0.json
components/l1-cache-lab.1.0.0.json   components/l1-cache-lab.1.1.0.json
```

Script, `--minor N` ile bu dosyalardan çalışma sürümlerini (`1.{2N}.0` / `1.{2N+1}.0`)
türetir; böylece aynı runtime'a defalarca koşulabilir (`latest` asserti ancak taze sürümle
anlamlıdır). `flowVersion` alanı component şemasının sürümüdür, bilerek değiştirilmez.

## Nasıl çalıştırılır

Ön koşullar: docker altyapısı (mocklab dahil) + orchestration (4201) + execution (4202) ayakta.

```bash
python3 api-tests/l1-cache-lab/l1-cache-behaviour-test.py --minor 0
# tekrar kosum: --minor 1, 2, ...
```

Bağımlılık yok (yalnızca Python 3 stdlib + docker CLI; task sürüm kanıtı için
`vnext-postgres` konteynerinden psql okur).

## Beklenen sonuç / başarı kriteri

`18/18 PASS`. Kritik assertler:

| Assert | Kanıtladığı şey |
|---|---|
| B.flowVersion == yeni sürüm | `latest` çözümü publish'in hemen ardından taze (L1 bayat değil) |
| A.flowVersion eski sürümde sabit | Pinned instance publish'ten etkilenmez |
| A'nın view marker'ı yeni sürüme döner | `"1"` range referansı sıcak L1'e rağmen anında yeni versiyona çözülür |
| A'nın 2. probe Request'i yeni taskVersion taşır | Task range referansı da anında yeni versiyona çözülür |

Herhangi bir FAIL = L1 bayat servis ediyor demektir (veya publish kırık) — ikisi de release blocker.

### Bonus: L1'in gerçekten devrede olduğunun kanıtı

Test yeşilken L1'in bypass edilmediğini doğrulamak için, tekrarlı view okumaları sırasında
Redis'i dinleyin:

```bash
docker exec vnext-redis sh -c "timeout 4 redis-cli monitor" | grep -c "l1-lab"
```

Beklenen desen: okuma başına yalnızca `...:gen` key'lerine `HGETALL` (küçük generation
token'ı), `res:`/`full:` gövde key'lerine **sıfır** okuma — gövdeler L1'den servis edilir.
2026-08-20 koşusunda 6 view çağrısı = 12 gen okuması + 0 gövde okuması gözlendi.

### Faz 2 (GenerationMemoSeconds) açıkken

Orchestration `ComponentCache:GenerationMemoSeconds: 5` ile koşarken (2026-08-20 doğrulaması):

- Senaryo yine **18/18 PASS** — tek pod'da publish kendi memo'sunu düşürdüğü için tazelik
  assertleri memo penceresi içinde bile geçer (`BumpAsync` sözleşmesinin uçtan uca kanıtı).
- Redis trafiği (account-opening start ile ölçüldü): memo kapalı = 22 gen okuması/start;
  memo açık + ≥5 sn boşta = **8** (tekil component başına 1; istek içi tekrarlar bedava);
  memo açık + pencere içinde (sürekli trafik) = **0**.
- Restart sonrası ilk dokunuş soğuk desendir: tekil component başına 1 gen + 1 gövde okuması
  (bir kerelik L1 doldurma).
- **Çapraz-pod ≤5 sn penceresi tek pod'lu lokal stack'te gözlenemez** — publish eden pod anında
  tazedir. Pencere, multi-replica ortamda bir pod'dan publish edip diğerinden `latest` poll'layarak
  ölçülür. CI/CD sözleşmesi: vnext repo `docs/runtime/component-cache-generation-memo.md`.
