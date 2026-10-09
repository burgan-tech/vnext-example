# vNext Build Plan — Cross-Domain x-storage (`file-offload-xd`)

> Durum: **ONAYLI** — kullanıcı, 2026-10-09 ("cross-domain lab'ı kaldır ve testi ekle"). Runtime:
> vnext `feature/file-offload-x-storage` (vnext-client-sdk-core#101), lab imajları o dalın worktree'sinden.
> Lab topolojisi ve bağlam: [VNEXT-BUILD-PLAN.md](VNEXT-BUILD-PLAN.md) (core :4201, partner :4211,
> discovery :4231).

## 0. Özet & Kapsam

**Ne:** Aynı domain'de yeşil olan x-storage dosya akışını domain sınırında doğrulamak:
1. `core`'daki üst akışa gönderilen dosyalı hareket, aktif alt akışı **partner**'da olduğunda gövdeye
   dokunulmadan iletilir; dosyayı partner **kendi master şeması ve kendi binding'iyle** depolar.
2. Bir `core` eşlemesi `GetFileAsync("partner", …)` ile partner'daki dosyanın baytlarını okur
   (`internal/file` + discovery + Dapr service invocation).

**Neden:** Proxy kararının asıl gerekçesi cross-domain yapraktı (kullanıcı, 2026-10-08); tek-domain lab bunu
kanıtlamıyordu. `GetFileAsync`'in uzak yolu hiç koşmamıştı.

**Kapsam dışı:** SubProcess / trigger task'larla dosya taşıma (TrustedPayload cross-domain taşınmıyor —
bilinen kısıt); yük testi; S3/MinIO.

## 1. Kabul Kriterleri

- **XS-01** `core/fo-xd-parent` `enter-child` ile partner'da `fo-xd-child` başlatır; üst akış state'i
  child'ı gösterir.
- **XS-02** Üst akışa (core) **sync** `child-upload {passport:{content}}` → 200, yanıt id = üst akış id.
  Partner child verisinde handle: `component` = partner'ın binding'i, `owner = {domain: partner, flow:
  fo-xd-child, instance: <child>}`, `size`/`eTag` gönderilen baytlarla tutarlı. Core üst akış verisinde ve
  hareket geçmişinde `content` yok.
- **XS-03** Aynısı **async** → 202, üst akış id; dinlenme sonrası XS-02'deki koşullar.
- **XS-04** Partner `functions/file?file=<id>` (partner URL) → 200, SHA-256 = `eTag`; core üst akışın
  `functions/file`'ı aynı id ile → 404 (iniş yok).
- **XS-05** Core'daki `fo-xd-reader` akışının script task'ı `GetFileAsync("partner", "fo-xd-child",
  childId, fileId)` ile baytı okur ve SHA-256'yı veriye yazar → `eTag`'e eşit.
- **XS-06** Nesne partner'ın orchestration sidecar'ının deposunda, core'unkinde değil (`docker exec`).

## 2. Bileşen Envanteri

| Yol | Tür | Not |
|---|---|---|
| `core/Workflows/file-offload-xd/fo-xd-parent.json` (+ master şema) | workflow | `child` state'i `stateType:4`, `subFlow.process.domain:"partner"`, flow `fo-xd-child` |
| `core/Workflows/file-offload-xd/fo-xd-reader.json` (task `fo-script` + mapping) | workflow | `read` hareketi: gövde `{sourceDomain, sourceFlow, sourceInstance, sourceFileId}` (anahtar bilerek `file` değil — runtime `file` üyesini dosya referansı sayar) → onEntry script `GetFileAsync` → `checksum` |
| `partner/Workflows/file-offload-xd/fo-xd-child.json` + `partner/Schemas/.../fo-xd-child-master.json` | workflow S | master: `passport` `x-storage: { binding: vnext-blob-local }`; `child-waiting` ─`child-upload`→ `child-waiting` ─`child-finish`→ `child-done` (uygulamada: yaprak aktif kalsın diye upload kendine döner — XS-04'ün 404'ü böylece gerçek bir "iniş yok" kontrolü) |
| `tests/Core.IntegrationTests/Tests/CrossDomainLab/FileOffloadCrossDomainTests.cs` | test | `CrossDomainLabTestBase`, `SkippableFact`; partner publish mevcut fixture'dan |

## 3. Altyapı

- Lab imajları vnext worktree'sinden: `VNEXT_SRC_DIR=../vnext-file-offload lab.sh images`.
- Her domain'in orchestration sidecar'ına `vnext-blob-local` (`bindings.localstorage`, rootPath
  `/tmp/vnext-blobs`, container içi) — runtime şablonunun `vnext/orchestration/dapr/components/` klasörü
  (lab'ın yerel kopyası). Test dosyaları küçük (≤ 64 KB): sidecar'ın 4 MiB varsayılan gövde sınırı yeterli.

## 4. Doğrulama

`dotnet test --filter "FullyQualifiedName~FileOffloadCrossDomain"` lab'a karşı (partner URL set);
`docker exec` ile iki sidecar'ın deposu; `TEST-SCENARIOS.md` satırı + CrossDomainLab README bölümü aynı
commit'te.

## 5. Sonuç (2026-10-09)

Bileşenler `core/Workflows/file-offload-xd/build-file-offload-xd.py` ile üretildi. `FileOffloadCrossDomainTests`
5/5 yeşil (XS-01..XS-05); tüm `CrossDomainLab` 17 geçti / 2 skip / 0 kırmızı. XS-06 elle (`docker cp`; sidecar
distroless): nesneler yalnız partner sidecar'ında, SHA-256'lar eTag'lere eşit, core deposu boş. Runtime kusuru
bulunmadı. Ayrıntı: `tests/Core.IntegrationTests/Tests/CrossDomainLab/README.md` § x-storage.
