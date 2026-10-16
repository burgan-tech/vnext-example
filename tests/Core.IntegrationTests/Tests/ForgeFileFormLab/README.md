# ForgeFileFormLab — integration test

vNext Forge'un Quick Run dosya widget'ının dayandığı şekil. `file-offload-lab`'dan farkı: start ve
`replace` transition'ı **transition rolünde** bir şema (`ffl-upload`) taşır — yükleme şekli
(`name`, `mimeType`, `size`, `content`) — ve workflow'un master şeması (`ffl-master`) aynı yolları
`x-storage` ile işaretler: bir alan (`document`) ve tek seviyeli bir dizi (`attachments[]`). Forge,
transition şemasında `x-storage` olmadığı için (input rolleri reddeder) dosya alanlarını master
şemanın `x-storage` yollarından bulur ve bu düğümlerde dosya seçici gösterir. Domain `core`, task yok —
MockLab gerekmez.

## Neyi denetliyor

| Test | Neyi denetliyor |
|---|---|
| `SchemaFunction_ServesTheUploadShape_ForTheForm` | `functions/schema?transitionKey=replace` → 200; gövdede `content` var, `x-storage` yok (Forge formu bu şemadan çizer) |
| `Start_WithFieldAndArrayUploads_StoresHandles` | `start?sync=true` ile `document` + iki `attachments[]` → üç handle (binding, size, eTag = SHA-256, owner); veride `content` yok; `functions/file` dizi elemanının baytlarını döner |
| `Replace_WithoutTheField_KeepsTheStoredFile` | `replace` gövdesinde `document` yok → saklanan handle (aynı `file` id) kalır, diğer alanlar yazılır |
| `Replace_WithANewUpload_ReplacesTheHandle_AndTheOldIdIs404` | `replace` ile yeni yükleme → yeni handle; yeni id 200 aynı baytlar, eski id 404 |

## Neden var

vnext-forge v0.0.100 uyumu (F4): Quick Run dosya widget'ı ve `file` fonksiyonu. Mevcut
`file-offload-lab` akışlarının transition'larında şema olmadığından Forge formu ham JSON gösteriyordu;
widget'ın uçtan uca (UI → base64 gövde → runtime handle → `functions/file`) doğrulanabilmesi için şemalı
bir akış gerekiyordu. Forge tarafındaki UI adımları: vnext-forge `docs/testing/e2e.md`.

## Bileşenler

- `core/Workflows/forge-file-form-lab/ffl-flow.json` — `collecting` (Initial) → `replace` (self, şemalı) / `submit` → `done`
- `core/Schemas/forge-file-form-lab/ffl-master.json` — `type: master`; `document` ve `attachments[]` üzerinde `x-storage: { binding: vnext-blob-local }`, `x-order`
- `core/Schemas/forge-file-form-lab/ffl-upload.json` — `type: transition`; yükleme şekli, `x-order`, `x-helpText`

## Dikkat noktaları

- Orchestration sidecar'ında `vnext-blob-local` binding'i gerekir (`run-docker.sh up core` kurar); yoksa start 503 `Instance:100047`.
- Upload şeması `content`'i zorunlu tutar; bu yüzden saklı dosyayı korumak için alan gönderilmez (echo `{ file }` bu şemada reddedilir).

## Koşum

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~ForgeFileFormLab" -v minimal
```

Son koşu: 2026-10-16, lokal runtime vnext `master` @ `f06961ec` (v0.0.100), yalnız core — **4/4 yeşil**.
