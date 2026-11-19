# data-etag-subflow-lab — aktif SubFlow'da data function ETag'i (vnext#1084)

## Neyi denetliyor

**Tek iddia:** Aktif bir `S` SubFlow'u bekleyen instance'ın `functions/data` yanıtı, child'ın verisi
**aynı state'te** değiştiğinde (parent'ın verisi ve effective state'i yerinde dururken) eski ETag'e
`200` + yeni `extensions` döner; hiçbir şey değişmediyse `304` döner.

## Neden var

Parent, child'ın ömrü boyunca tek bir state'te bekler; kendi verisi değişmez. Ekran durumu child'da
yaşar ve parent'ın data function'ına `extensions` olarak (child'dan okunarak) gelir. ETag yalnız
parent'ı kapsadığında (vnext#1188 sonrası: veri + effective state) child'ın aynı state'teki veri yazımı
ETag'i oynatmıyordu: istemci ilk çağrıdan sonra hep `304` alıyor, child'ın extension çıktısını hiç
göremiyordu. Sahadaki karşılığı morph-idm 3FA: OTP ekranı `extensions.otpInput`'u (yanlış OTP bayrağı)
okur. Gateway yalnız parent'ın yolunu açtığı için istemci child'ı doğrudan da okuyamaz
(vnext#1084 yorumu, 2026-11-18).

Düzeltmeyle aktif SubFlow'da ETag `{parentEtag}.{childEtag}` olur; parent, child kısmını child'ın data
function'ına `If-None-Match` olarak iletir ve child'ın kendi fingerprint hızlı yolundan gelen `304`'ü
kendi `304`'ü sayar (zincirde hiçbir extension koşmaz).

## Akış

```
data-etag-subflow-lab (F)                 data-etag-subflow-lab-child (S)
  begin ──auto──▶ wait-otp (SubFlow) ───▶   otp ──wrong-otp──▶ otp   (veri yazar, state aynı)
                                              └─approve-otp──▶ otp-done
extension data-etag-subflow-lab-otp-input (type 3, scope 1, child'a bağlı):
  { maskedPhone, wrongOtp, attempt }  ← yalnız child'ın instance verisinden
```

Bileşenler `.csx` kaynaklarından üretilir:

```bash
python3 core/Workflows/data-etag-subflow-lab/build-data-etag-subflow-lab.py
```

## Beklenen sonuç

| Test | İddia |
| --- | --- |
| `ChildWritesDataInSameState_ParentDataAnswers200WithNewExtensions_Then304` | İlk okuma `200` + ETag, `attempt: 0`; aynı ETag'le `304`; `wrong-otp` (parent üzerinden proxy'lenir, child `otp`'de kalır) sonrası eski ETag'le **`200`**, yeni ETag, `wrongOtp: true, attempt: 1`; yeni ETag'le `304` |
| `ParentEtag_EndsWithTheChildsDataEtag` | Parent'ın ETag'i `.` + child'ın kendi data ETag'iyle biter (bileşik biçim) |

Ayırt edici koşu: düzeltme öncesi runtime'da ilk test `wrong-otp` sonrası `304` alır (kırmızı); ikinci
test bileşik biçimi bilmediği için kırmızıdır.

## Performans

`api-tests/data-etag-subflow-lab/data-etag-perf.py` aynı okuma senaryolarını (SubFlow'suz 304/200,
aktif SubFlow'da 304 / değişmiş child / koşulsuz 200) iki runtime'a karşı ölçer: istemci gecikmesi,
eşzamanlı 304, istek başına `Db.*` span ve sunucu transaction süresi (Elastic APM).

```bash
python3 api-tests/data-etag-subflow-lab/data-etag-perf.py run --label base   --out /tmp/base.json
python3 api-tests/data-etag-subflow-lab/data-etag-perf.py run --label branch --out /tmp/branch.json
python3 api-tests/data-etag-subflow-lab/data-etag-perf.py compare /tmp/base.json /tmp/branch.json
```

### Ölçüm — 2026-11-18 (dönüşümlü A/B, 2 koşu × 500 istek/senaryo)

Base: merge-base `5420efc1` (yan domain `etagbase`, :4241). Branch: düzeltme (`core`, :4201). İkisi aynı
infra'da, istekler sırayla birine sonra öbürüne gönderildi (makine yükü ~5–19 dalgalandığı için zaman
ayrık koşular karşılaştırılamıyordu). Değerler koşu 1 / koşu 2.

| Senaryo | Status | İstemci p50 ms (base → branch) | Sunucu tx p50 ms (base → branch) | `Db.*` span/istek |
|---|---|---|---|---|
| S1 SubFlow yok, 304 | 304 / 304 | 1.01→1.09 · 0.75→0.74 | 0.85→0.92 · 0.63→0.61 | 1 → 1 |
| S2 SubFlow yok, 200 | 200 / 200 | 3.04→3.22 · 2.68→2.72 | 2.86→3.02 · 2.53→2.56 | 3 → 3 |
| S3 aktif SubFlow, değişmedi | 304 / 304 | 0.80→1.44 · 0.81→1.46 | 0.68→1.31 · 0.70→1.35 | 1 → 3 |
| S4 aktif SubFlow, child değişti | 200 / 200 | 4.68→5.68 · 4.91→5.98 | 4.46→5.46 · 4.68→5.76 | 5 → 7 |
| S5 aktif SubFlow, koşulsuz | 200 / 200 | 4.75→5.93 · 4.58→5.69 | 4.52→5.71 · 4.37→5.47 | 5 → 7 |

SubFlow'suz yol (S1, S2) gürültü içinde aynı, komut sayısı aynı. Aktif SubFlow'da 304 ~+0.6 ms
(+2 indeksli okuma: açık korelasyon + child fingerprint), 200 ~+1.1 ms (aynı iki okuma). Base'in S3'teki
304'ü hatalıdır (child değişse de 304 döner); bedel doğruluğun bedelidir.

## Nasıl çalıştırılır

Lokal derlenen runtime'a karşı (`run-docker.sh up core`), sistem paketi yüklü; MockLab gerekmez.
Cross-domain child kapsamı bu senaryoda yok (gateway `If-None-Match`'i iletir; CrossDomainLab ile
doğrulanmadı). Ayrıntı: `TEST-SCENARIOS.md` § Koşum Öncesi Kontrol.
