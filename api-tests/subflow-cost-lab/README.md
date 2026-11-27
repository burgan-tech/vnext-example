# subflow-cost-lab — bir SubFlow geçişinin sync start'a eklediği maliyet

morph-idm ekibi 2026-11-27'de, tek iş yapan bir akışı SubFlow olarak çağırmanın aynı akışı tek başına
çalıştırmaya göre sunucuda ~100 ms eklediğini ölçtü. Bu lab o şekli task'sız kurar ve iki şeyi istek
başına sayar: süre ve veritabanı komutu. Amaç runtime değişikliklerini aynı makinede önce/sonra
karşılaştırmaktır; bir onay testi değildir.

## Akışlar

`core/Workflows/subflow-cost-lab/` altında iki workflow var, ikisi de üreticiden çıkar:

```bash
python3 core/Workflows/subflow-cost-lab/build-subflow-cost-lab.py
```

- `subflow-cost-lab-parent` (F): `start` → `p-init` → auto `start-subflow` → `p-sub` (SubFlow `S`,
  input/output mapping script'i) → auto `subflow-completed` → `p-done`.
- `subflow-cost-lab-child` (S): `start` → `c-init` → auto `auto-complete` → `c-done`.

Başlangıç transition'larında mapping yoktur; istemcinin `attributes`'ı ilk veri sürümü olarak saklanır.
Çocuk tek başına da başlatılır (type `S` akışın API'den başlatılması desteklenir), böylece aynı çocuğun
iki çalıştırması karşılaştırılır. Hiç task yoktur: ölçülen süre runtime'ın kendi işidir.

## Çalıştırma

Ölçüm **lokal derlenmiş runtime'a** karşı yapılır (`../vnext/etc/docker/run-docker.sh up core`).

```bash
python3 api-tests/subflow-cost-lab/cost-latency.py --publish --rounds 60 --warmup 3 --label base
```

`--publish` iki akışı `definitions/publish` ile yükler (wf CLI gerekmez). Her turda parent ve çocuk
`sync=true` sırayla başlatılır. Her istek kendi W3C `traceparent`'ını taşır; script trace'i yerel Elastic
APM'de (`--es-url`, varsayılan `http://localhost:9200`) bulur ve istek başına sayar. Yalnız Python standart
kütüphanesi kullanılır.

## Sonucun okunması

Kolonlar: istemci p50/p90, sunucu p50 (kök transaction), istek başına DB komutu (SELECT/INSERT/UPDATE),
veri yazma turu (`Db.LockAndReadHead`: transaction + satır kilidi + head okuması + commit), DB süresi ve span
sayısı. Komut sayıları deterministiktir ve iki koşu arasındaki asıl karşılaştırmadır. Süre yerelde gürültülüdür:
bir Debug build ilk birkaç yüz istekte ısınır, o yüzden aynı build'e ardışık iki-üç koşu yapıp düzleşmiş
olanları karşılaştırın. Yerelde bir DB turu ~1 ms'dir; test ortamında ~3 ms olduğu için aynı komut farkı orada
daha büyük süre farkı demektir.

## Ölçümler

2026-11-27, aynı makine ve altyapı, Debug build, 60 tur (ısınma koşusu sonrası):

| Build | Akış | DB komutu | SELECT | Veri yazma turu | Span | DB süresi p50 | Sunucu p50 |
|---|---|---|---|---|---|---|---|
| vnext `ebbe5f53` | parent | 44 | 20 | 5 | 197 | 40–42 ms | 85–89 ms |
| vnext `ebbe5f53` | child | 12 | 5 | 2 | 57 | 10–11 ms | 20–21 ms |
| + başlangıç yazımı / yanıt okuması düzeltmesi | parent | 42 | 18 | 3 | 188 | 37–38 ms | 88–93 ms |
| + başlangıç yazımı / yanıt okuması düzeltmesi | child | 10 | 3 | 1 | 50 | 10 ms | 19 ms |

Komut farkı beklenenle birebirdir: her başlangıçta bir veri yazma turu (parent'ta parent ve çocuk için iki)
ve sync yanıtın iki SELECT'lik yeniden okuması düştü. Yerel süre farkı gürültünün içindedir.
