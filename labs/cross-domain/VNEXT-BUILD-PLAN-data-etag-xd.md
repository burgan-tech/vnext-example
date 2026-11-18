# vNext Build Plan — Cross-Domain Data Function ETag (`data-etag-xd`)

> Durum: **ONAYLI — UYGULANDI** — kullanıcı, 2026-11-19 ("Approved, go ahead"). Sonuçlar: `tests/Core.IntegrationTests/Tests/CrossDomainLab/README.md` § Data function ETag. Runtime: vnext `claude/issue-1084-analysis-2a54ea` (#1084, commit yok),
> lab imajları o dalın worktree'sinden (`VNEXT_SRC_DIR=<worktree> lab.sh images`). Lab topolojisi:
> [VNEXT-BUILD-PLAN.md](VNEXT-BUILD-PLAN.md) (core :4201, partner :4211, discovery :4231).

## 0. Özet & Kapsam

**Ne:** #1084 düzeltmesinin (aktif `S` SubFlow'da data function ETag'i = `{parentEtag}.{childEtag}`, parent
child'a `If-None-Match` iletir) **child başka domain'deyken** çalıştığını kanıtlamak ve maliyetini ölçmek.

**Neden:** Tek-domain senaryosu (`DataEtagSubFlowLab`) in-process gateway'i sınıyor. Cross-domain'de child okuması
`RemoteInstanceQueryAppService` üzerinden HTTP ya da Dapr ile gidiyor; dört nokta koşulmadı:
child ETag'inin uzak yanıtta (JSON gövdesi) parent'a ulaşması, `If-None-Match` + 304'ün Dapr service invocation'dan
bozulmadan geçmesi, iletilen çağıran başlıklarıyla child ETag'inin poll'lar arasında sabit kalması, her 304'ün
partner'a bir hop olmasının bedeli.

**Kapsam dışı:** 3. domain (credit), child'ın kendi SubFlow'u (zincir `p.c.g`), fingerprint projeksiyon optimizasyonu.

## 1. Kabul Kriterleri

- **DX-01 304 → 200 → 304 domain sınırında** — core `de-xd-parent` partner `de-xd-child`'ı SubFlow olarak bekler.
  Parent data ilk okuma 200 + ETag (`extensions.deXdOtpInput.attempt = 0`); aynı ETag → 304; child'da `wrong-otp`
  (parent üzerinden proxy, child aynı state'te kalır) → eski ETag'e **200**, yeni ETag, `wrongOtp: true, attempt: 1`;
  yeni ETag → 304.
- **DX-02 Bileşik ETag** — parent ETag'i `.` + partner'daki child'ın (doğrudan :4211'den okunan) data ETag'iyle biter.
- **DX-03 İki sağlayıcı** — DX-01/02 hem `dapr` (varsayılan `lab.sh up`) hem `VNEXT_LAB_DISCOVERY_PROVIDER=http`
  ile yeşil.
- **DX-04 Ayırt edici koşu** — master (`5420efc1`) imajlarıyla DX-01 eski ETag'e 304 alır (kırmızı), DX-02 kırmızı.
- **DX-05 Ölçüm** — aynı runtime'da (dal), core-içi child'lı parent (`data-etag-subflow-lab`) ile partner child'lı
  parent (`de-xd-parent`) dönüşümlü A/B: 304 ve 200 için istemci p50/p95, sunucu tx p50, `Db.*` span/istek, descent
  span süresi (`Subflow.Descend/de-xd-child`, `vnext_descent_transport`). Master karşılaştırması zaman-ayrık
  (imaj değişimi gerekir) — gürültü notuyla raporlanır.

## 2. Bileşenler (yeni, hepsi `1.0.0`)

| Domain | Bileşen | Not |
|---|---|---|
| partner | `Extensions/data-etag-xd/de-xd-otp-input.json` | type 3, scope 1, `script-task` (partner sistem paketi); `DataEtagSubFlowLab`'ın `OtpInputExtensionMapping.csx`'inin aynısı |
| partner | `Workflows/data-etag-xd/de-xd-child.json` | type `S`; `otp` ⟶ `wrong-otp` (target `otp`, veri yazar) / `approve-otp` ⟶ `otp-done` |
| core | `Workflows/data-etag-xd/de-xd-parent.json` | `begin` ⟶ auto ⟶ `wait-otp` (stateType 4, `subFlow.process` = `{domain: partner, key: de-xd-child}`) |

Üretim: bir build script'i (`build-data-etag-xd.py`) `.csx`'ten base64'ü üretir (`encode-scripts.py` deseniyle).
Mevcut `xd-child` / `xd-parent`'a **dokunulmaz** (CrossDomainLab'ın 11 testi onlara bağlı).

## 3. Testler

`tests/Core.IntegrationTests/Tests/CrossDomainLab/DataEtagCrossDomainTests.cs` (2 test, `CrossDomainLabTestBase`,
partner yoksa skip); partner bileşenleri koleksiyon fixture'ında yayınlanır (mevcut desen). Ölçüm
`api-tests/data-etag-subflow-lab/data-etag-perf.py`'ye `--xd` seçeneği.

## 4. Doğrulama & Kayıt

- OpenObserve / Elastic APM: `Subflow.Descend/de-xd-child` span'i `vnext_descent_function=data`,
  `vnext_descent_transport=remote`; dapr modunda caller/callee sidecar span'leri.
- README (`CrossDomainLab/README.md` bölümü) + `TEST-SCENARIOS.md` satırı aynı commit'te; vnext
  `docs/runtime/state-function-cache-and-etag.md`'deki "Cross-domain" maddesi ölçümle güncellenir.

## 5. Geri Alma

Yalnız yeni dosyalar; mevcut lab bileşenleri değişmez. Silmek yeter.
