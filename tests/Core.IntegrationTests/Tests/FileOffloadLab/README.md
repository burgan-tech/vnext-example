# FileOffloadLab — integration test

Master şemada `x-storage` ile işaretli dosya alanlarının (vnext-client-sdk-core#101, runtime yarısı)
uçtan uca davranışı. İstemci dosyayı bir kez base64 `content` olarak gönderir; runtime baytları Dapr
output binding'ine (`vnext-blob-local`, `bindings.localstorage`) yazar ve o andan itibaren instance
verisi, transition kaydı, job/outbox satırı ve her okuma küçük bir **handle** taşır (`component`, `file`
GUID, `size` = çözülmüş bayt sayısı, `eTag` = SHA-256 hex, `owner` = domain/flow/instance). Baytlar
yalnız `functions/file` ve `ScriptBase.GetFileAsync` ile geri okunur. Domain `core`, yalnız script
task'lar — MockLab gerekmez. Davranış referansı: vnext `docs/runtime/file-storage.md`.

## Neyi denetliyor

| Test | Neyi denetliyor |
|---|---|
| `StartSync_ContentBecomesAHandle_AndNoRecordCarriesTheBytes` | `start?sync=true` → 200; `functions/data`'da handle (binding, D-GUID, size, eTag = sha256, owner = `core/fo-flow/<id>`), yanıtta, veride ve `transitions` gövdesinde `content` yok |
| `StartAsync_ContentBecomesAHandle_AndNoRecordCarriesTheBytes` | Aynısı `sync=false` → 202, settle sonrası |
| `AsyncTransition_With3MbFile_Is202_AndRestsWithAHandle` | `add-document?sync=false` ile 3 MiB `files[]` → 202 (yanıtta instance id); settle sonrası handle; `functions/file` baytları aynı sha256 ile döner |
| `Echo_TheStoredHandle_KeepsTheStoredMetadata` | Okunan handle'ın aynen geri gönderilmesi, yalnız `{ file }` ve değiştirilmiş `mimeType/name/size/eTag` → 200, saklanan handle değişmez |
| `Reference_ToAnotherInstancesFile_Is400` | Başka instance'ın `file` id'si → 400 `Instance:100048`, veri yazılmaz |
| `ContentAndFile_Together_Is400` | `content` + `file` → 400 `Instance:100048` |
| `Reference_OnStart_Is400` | `start`'ta referans (LatestData yok) → 400 `Instance:100048` |
| `InvalidBase64_Is400` | Bozuk base64 → 400 `Instance:100048` |
| `MissingBinding_Start_Is503_AndCreatesNoInstance` | `fo-missing-binding` (binding `vnext-blob-missing` yok) → 503 `Instance:100047`; o `key` ile instance oluşmaz |
| `ForwardedToActiveSubFlow_Sync_LeafSwapsAgainstItsOwnSchema` | Aktif S child'ı olan parent'a `child-upload` (sync) → 200 + parent id; handle child'da (`owner` = `fo-child/<childId>`); parent verisinde/geçmişinde `content` ve child'ın dosyası yok; parent'ın `functions/file`'ı child'ın id'sine 404, child'ınki 200 |
| `ForwardedToActiveSubFlow_Async_Is202WithTheParentId_AndTheLeafOwnsTheFile` | Aynısı `sync=false` → 202 + parent id |
| `TransitionMapping_RelocatesUploadToPassport_AndGetFileAsyncReadsTheBytes` | `to-review` mapping'i `upload` → `passport` taşır; mapping çıktısının Trusted offload'u handle üretir; `upload` veriye ulaşmaz; geçmiş gövdesinde `content` yok; `review` onEntry'sinde `GetFileAsync` ile okunan baytların SHA-256'sı (`checksum`) = handle `eTag` |
| `FileFunction_ServesTheBytes_WithTheContractHeaders` | 200 baytlar; tırnaklı `ETag`, `Accept-Ranges: bytes`, `Cache-Control: private, max-age=31536000, immutable`, `nosniff`, `Content-Security-Policy: sandbox; default-src 'none'`, `Content-Type` = saklanan mimeType, `Content-Disposition: inline` + `filename*=UTF-8''kimlik-%C3%B6n.pdf` |
| `FileFunction_ConditionalRangeAndHead` | `If-None-Match` → 304; `Range: bytes=0-1` → 206 + `Content-Range: bytes 0-1/100`; aralık dışı → 416; `HEAD` → başlıklar, gövde yok |
| `FileFunction_ActiveType_IsAnAttachment` | `text/html` → `Content-Disposition: attachment` |
| `FileFunction_StateQueryRoles_DenyIs403` | `locked` state'inin `queryRoles`'u (`fo.reader`): rolsüz 403 (bilinmeyen file id'de de 403 — dosya aranmadan karar), `fo.reader` ile 200 |
| `FileFunction_XRolesHiddenPath_Is404_AndVisibleWithTheRole` | `secret` (`x-roles` yalnız `fo.auditor`): `data` alanı budar; `functions/file` rolsüz 404, `fo.auditor` ile 200 |
| `FileFunction_AnotherInstancesFile_Is404` | Başka instance'ın dosyası → 404 `Instance:100049` |
| `ReplacingTheFile_OldIdIs404_NewIdIs200` | `passport` yeni upload ile değişince eski id 404, yeni id 200 |

## Neden var

vnext-client-sdk-core#101 (2026-10-08): kimlik belgesi gibi dosyalar base64 olarak instance verisine,
transition gövdesine, job ve outbox satırlarına giriyor; satırlar MB'larca büyüyor ve her okuma baytları
taşıyordu. Runtime yarısı (vnext `feature/file-offload-x-storage`) baytları binding'e alıp her yerde
handle bırakıyor. Bu lab sözleşmenin yazım kurallarını, okuma başlıklarını ve yetki/sahiplik sınırlarını
lokal runtime'a karşı pinler.

## Akış şekli

`core/Workflows/file-offload-lab/`, `build-file-offload-lab.py` ile `./src/*.csx`'ten üretilir (şemalar
`core/Schemas/file-offload-lab/`, task `core/Tasks/file-offload-lab/fo-script.json`):

```
fo-flow (fo-master: passport · files[] · secret[x-roles fo.auditor] x-storage):
  start → waiting ─add-document→ waiting
                  ─to-review [mapping upload→passport]→ review [onEntry: GetFileAsync → checksum] ─finish→ done
                  ─lock→ locked [queryRoles fo.reader] ─finish→ done
fo-parent (fo-parent-master: aynı passport alanı) : start → p-sub [S SubFlow fo-child] ─auto→ p-done
fo-child  (fo-child-master)                       : start → c-waiting ─child-upload→ c-waiting ─child-finish→ c-done
fo-missing-binding (binding vnext-blob-missing)   : start → m-waiting
```

Kritik adımlar: admission'daki swap (start/transition), parent'ın forward ettiği isteğin **leaf**'te
swap'ı (parent da aynı yolda `x-storage` tanımlıyor; parent swap etseydi `owner` parent olurdu), ve
`CreateTransitionRecordStep`'teki mapping çıktısı offload'u.

## Nasıl koşulur

Ön koşul: vnext `feature/file-offload-x-storage`'tan lokal derlenmiş runtime (`run-docker.sh up core`),
orchestration sidecar'ı `vnext-blob-local` bileşenini yüklemiş (`etc/orchestration/dapr/components/
vnext-blob-local.yaml`, `rootPath: /blobs` → vnext'in `etc/docker/data/blobs`).

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~FileOffloadLab" -v minimal
```

Fixture değişikliği: `src/*.csx`'i düzenle, `build-file-offload-lab.py`'deki `VERSION`'ı artır (publish
sürüm-değişmezdir), betiği koş.

## Geçme kriteri + kanıt

Testlerin yeşili tek başına kanıt değildir. Postgres ile (şema = flow key, `-` → `_`):

```sql
-- Satırlar küçük kalmalı, hiçbirinde "content" olmamalı
SELECT max(length("Data"::text)), count(*) FILTER (WHERE "Data"::text LIKE '%"content"%') FROM fo_flow."InstancesData";
SELECT max(length("Body"::text)), count(*) FILTER (WHERE "Body"::text LIKE '%"content"%') FROM fo_flow."InstanceTransitions";
-- async transition job'u (3 MiB upload): payload birkaç KB, content yok
SELECT length("Payload"::text), position('"content"' in "Payload"::text)
  FROM sys_queues."BackgroundJobs" WHERE "Payload"::text LIKE '%<instanceId>%';
-- missing binding: hiç instance yok
SELECT count(*) FROM fo_missing_binding."Instances";
```

Ve `ls -la <vnext>/etc/docker/data/blobs` — her upload için GUID adlı, upload boyutunda bir dosya.

## Bilinen sınırlar / kapsam dışı

- **Cross-domain `GetFileAsync`** (`internal/file` üzerinden başka domain'den okuma) kapsanmıyor: partner
  domain ayakta değil.
- Silme/değiştirme yok: `passport` değişince eski nesne store'da kalır (runtime'ın bilinen sınırı
  `file-store-no-delete`); test yalnız eski id'nin `functions/file`'dan 404 döndüğünü pinler.
- `System.Security.Cryptography.SHA256` mapping derlemesinde çözülmüyor (CS0103), bu yüzden
  `FoChecksumMapping` SHA-256'yı düz C# ile hesaplar.
- SDK boşluğu: `VNextApiClient` ve `WorkflowTestBase.SendRawAsync` yanıt başlıklarını/baytlarını vermez,
  `HEAD`/`Range`/`If-None-Match` gönderemez; test kendi `HttpClient`'ını kullanır.
