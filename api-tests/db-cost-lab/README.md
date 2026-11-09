# db-cost-lab — transition başına veritabanı maliyeti

Bu lab tek bir soruya cevap arar: bir transition, bir state değişimi ya da bir SubFlow turu
veritabanına kaç komut ve kaç commit gönderiyor, ve bir runtime değişikliği bunu ne kadar
değiştiriyor? Gecikme bu sorunun yan ürünüdür; asıl ölçülen komut ve commit sayısıdır.

Test ortamında (Elastic APM, 2026-11-05) en basit `instances/start` 18, sıradan bir sync transition
50, SubFlow'lu bir iş akışı 170–610 veritabanı komutu üretiyordu. Kod okuması her hop'ta 9–11 ayrı
autocommit gösterdi (hop'un unit of work'ü transactional değil). Bu lab o sayıları lokal runtime'da,
önce ve sonra aynı yöntemle ölçmek için var.

## Akışlar

Yeni bir bileşen yok; mevcut, task'ı yalnız script olan (MockLab gerektirmeyen) akışlar kullanılır:

| Senaryo | Akış | Ne kapsar |
|---|---|---|
| `manual` | `payload-modes` | start → `submit-payload` (veri) → `complete`: task'sız manuel hop'lar, admission, data append |
| `tasks-timer` | `chain-busy-leaf` (root olarak) | onEntry script task'ları, auto hop, 30 dk'lık timer kurulumu; `finish-leaf`'te timer iptali + onExit + onExecute task + Finish |
| `subflow` | `subflow-depth-lab-l4` (derinlik 2) | S SubFlow girişi, transition'ın yaprağa proxy'lenmesi, yaprağın tamamlanması, parent resume + auto finish |
| `chain` | `chain-busy-root` (derinlik 3) | üç seviyede script task'lı zincir kurulumu ve kök üzerinden `finish-leaf` ile çözülmesi |

Bütün istekler `sync=true`; istek döndüğünde zincir dinlenme noktasındadır. İstekler varsayılan olarak
`sub`/`act_sub` ile bir çağıran kimliği taşır (audit kolonları doldurulur, üretime yakın);
`--caller ''` anonim çağıranı ölçer.

## Çalıştırma

Ölçüm **lokal derlenmiş runtime'a** karşı yapılır (container image eski kodu taşır):

```bash
cd ../vnext/etc/docker && ./run-docker.sh up core
cd ../../../vnext-example
wf domain use core && wf domain active && wf check && wf sync
python3 api-tests/db-cost-lab/db-cost.py run --label base --iterations 20 --out /tmp/db-cost-base.json
# runtime'ı değiştir (ör. diğer branch'le `up core --skip-migrate`), sonra:
python3 api-tests/db-cost-lab/db-cost.py run --label branch --iterations 20 --out /tmp/db-cost-branch.json
python3 api-tests/db-cost-lab/db-cost.py compare /tmp/db-cost-base.json /tmp/db-cost-branch.json
```

Script yalnız Python standart kütüphanesini kullanır; Elasticsearch'e (`ES_URL`, varsayılan
`http://localhost:9200`) ve `docker exec vnext-postgres psql` ile Postgres'e erişir.

## Ne ölçülür, nasıl okunur

Her senaryo önce `--warmup` kez (ölçülmeden) koşar, sonra `--iterations` kez sırayla koşar.
Ölçüm penceresi son isteğin ardından `--settle` saniye daha açık kalır; post-commit relay'leri,
Inbox yedekleri ve outbox flush'ı pencereye girer.

1. **Pencere toplamı — asıl sayı.** Penceredeki bütün `Db.*` span'leri (servis ve komut tipine göre)
   sayılır, koşudan önce ölçülen boştaki oran (outbox/inbox polling) düşülür ve iterasyon sayısına
   bölünür. Ayrı trace'e düşen post-commit işi ve worker'lar da dahildir.
2. **İstek başına.** `x_request_id` etiketi script'in verdiği id'yi taşıyan span'ler; hangi çağrının
   (`start`, `submit-payload`, `finish-leaf` …) ne ödediğini gösterir. Bir id'yi taşımayan
   yan lane'ler burada görünmez, pencere toplamında görünür.
3. **Commit.** `pg_stat_database.xact_commit` farkı, boştaki oran düşülerek. Hop'un unit of work'ü
   transactional olmadığı için her yazım komutu kendi commit'idir; bu sayı WAL flush sayısının
   üst sınırıdır (`commit_delay` grup commit'i ayrıca birleştirebilir).

İstemci gecikmesi (adım başına p50/p95) de raporlanır, ama tek makinede ve düşük yükte gürültülüdür;
karar sayısı komut ve commit farkıdır. `compare` iki koşuyu tablo olarak yan yana koyar.

Script bir onay testi değildir. Bir istek 2xx dışında dönerse ya da instance beklenen son state'e
varmazsa çıkış kodu 1 olur ve hatalar JSON'da listelenir.

## Sonuçlar

### 2026-11-05 — transition hop DB round-trip paketi (vnext `claude/transition-pipeline-db-perf-eb10dc`)

Baz: vnext `6de7df16` (v0.0.103). Branch: aynı baz + beş değişiklik — sync reserve'den sonra
Busy'nin tracker'a hizalanması (ikinci `UPDATE Instances SET Status` yok), admission'ın aggregate'i
yeniden okumaması, latest-only hop yüklemesinin 3 yerine 2 komut olması, timer iptalinde detaylı
load yerine PK probe'u, SubFlow resume'da eşleşemeyecek Busy CAS'in atlanması. Aynı makine, aynı
veritabanı, aynı akışlar, 20 iterasyon, 3 warm-up; her runtime için iki koşu (`a / b`).

**Kimlikli çağıran (`sub`/`act_sub`, varsayılan — üretime yakın):**

| Senaryo | DB komutu / iterasyon | `Instances` satır UPDATE | İstek başına DB komutu |
|---|---:|---:|---|
| `manual` | 38 / 38 → 32 / 32 (−15,8%) | 10 / 10 → 8 / 8 | start 12 → 10, `submit-payload` 10 → 8, `complete` 12 → 10 |
| `tasks-timer` | 41 / 44 → 40,6 / 38 | 5 / 5,5 → 6,2 / 4 | start 18 → 17, `finish-leaf` 19 → 17 |
| `subflow` | 101,3 / 101,6 → 102,6 / 94,8 | 12 / 12 → 14,1 / 12 | start 40,1 → 38,1, `leaf-finish` 37,5 → 32,8 |
| `chain` | 194,1 / 193,5 → 183,5 / 183,2 (−5,4%) | 21 / 21 → 21 / 21 | start 78 → 74, `finish-leaf` 67,5 → 61,2 |

**Anonim çağıran (`--caller ''`):**

| Senaryo | DB komutu / iterasyon | `Instances` satır UPDATE | Transaction (salt-okur dahil) |
|---|---:|---:|---:|
| `manual` | 38 / 38 → 32 / 32 (−15,8%) | 10 → 7 | 81,1 / 78,7 → 68,2 / 68,1 |
| `tasks-timer` | 41 / 41 → 38 / 38 (−7,3%) | 5 → 4 | 95,5 / 94,1 → 86,2 / 86,2 |
| `subflow` | 101,0 / 101,3 → 95,0 / 94,6 (−6,3%) | 12 → 11 | 194,2 / 195,4 → 187,2 / 186,3 |
| `chain` | 192,8 / 193,5 → 184,7 / 184,4 (−4,5%) | 21 → 19 | 391,6 / 394,7 → 381,9 / 380,2 |

Okuma notları:
- **İstek başına sayım kararlıdır**, pencere toplamı değil: pencereye zaman zaman bir arka plan işi
  düşüyor (kimlikli `tasks-timer` baz `b` 44, branch `a` 40,6; `subflow` branch `a` 102,6 — aynı
  koşuda `Instances` UPDATE da sapıyor). İstek başına satırlar her koşuda aynı.
- Kaldırılan `UPDATE Instances SET "Status"` ayrı bir komut değildi; EF onu transition kaydının
  INSERT'üyle aynı komuta batch'liyordu. Bu yüzden `Db.UPDATE` komut sayısı değil satır sayacı düşer.
  SQL kanıtı (aynı `submit-payload`): baz INSERT komutu `INSERT InstanceTransitions; UPDATE Instances
  SET "Status"`, branch yalnız INSERT; baz 5 SELECT, branch 3 SELECT.
- Kimlikli koşuda instance'ın **ilk** değişikliği (start) bu yazımı korur: Aether'ın audit
  interceptor'ı boş `ModifiedBy`'ı o yazımda çağıranla dolduruyordu ve `metadata.modifiedBy` aynı
  kalmalı (RowSecurityLab `UnchangedFlowTests` bunu yakaladı). Sonraki hop'larda yazım düşer.
  Bu yüzden kimlikli `manual`'da `Instances` UPDATE 10 → 8, anonimde 10 → 7.
- WAL fsync değişmez: paket okuma ve gereksiz yazım komutunu azaltır, commit sayısını değil. Commit
  birleştirme (epilog transaction'ı, transition kaydının tek yazımı) ayrı bir karar konusu.
- İstemci p50'si host yeniden başlatma ve veritabanı büyümesine çok duyarlı; ilk koşu soğuk host'ta
  2–4× yavaştı. Karar sayısı komut farkıdır, gecikme iddia edilmez.

Doğruluk: aynı runtime'a karşı tam suite (CrossDomainLab hariç) 621 geçti / 8 kırmızı / 60 skip;
kırmızıların hepsi master'da da kırmızı (ScriptPluginLab 7 — plugin dizini lokal host'ta yok,
`FlowDefaultVersionTests.SubProcess_Local_FollowsDefault_RunningChildStays` — baz runtime'da da
kırmızı doğrulandı).

## Bilinen kısıtlar

- Tek makine, tek domain, broker gecikmesi yok. Komut sayısı ortamdan bağımsızdır; gecikme değildir.
- Boştaki oran sabit varsayılır; pencere kısa olduğunda polling'in rastgele zamanlaması ±1–2
  komut/iterasyon oynatabilir. Karşılaştırmayı aynı makinede, aynı `--iterations` ile yapın.
- `x_request_id` etiketi bir yan lane'e taşınmıyorsa o lane'in komutları istek başına tabloda değil,
  yalnız pencere toplamında görünür.
