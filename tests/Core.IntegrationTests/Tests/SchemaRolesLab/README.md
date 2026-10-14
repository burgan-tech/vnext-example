# schema-roles-lab — `x-computed` (runtime) ve `x-encryption` `source` (runtime / client)

## Neyi denetliyor

**İddialar** (vnext-schema rol sözlükleri revizyonunun runtime tarafı):

1. **`x-computed` `evaluatedBy: "runtime"`** (flag yok, her zaman açık):
   - **Okuma yüzeyleri:** değer her okuma yüzeyinde hesaplanır. Kapsananlar: instance GET, list (filtreli), data function ve `?sync=true` transition yanıtı.
   - **Çağıranın görünümü:** hesaplama, çağıranın göreceği veri üzerinden yapılır (`x-roles` budamasından sonra). `lastName`'i göremeyen çağıran, `fullName`'i `"Ada"` olarak alır.
   - **Kural davranışı:**
     - İlk eşleşen case kazanır; aksi halde fallback kullanılır.
     - Güncellemeden sonraki ilk okuma yeni veriyle hesaplanır.
     - Çözülemeyen değer `whenAbsent`'e göre döner: varsayılan `""`, tanımlıysa `null` (`b = 0` iken `ratio`).
     - İç içe alan kendi parent nesnesine yazılır. Parent yoksa oluşturulmaz.
   - **Saklama:** computed alan hiç saklanmaz. Client'ın start'ta veya transition'da gönderdiği değer, instance data'ya yazılmadan atılır.
   - **`client` modu:** `evaluatedBy: "client"` runtime'da hesaplanmaz.
2. **`x-encryption` `source: "client"`**:
   - Master şemada da, transition şemasında da kabul edilir.
   - **Hiç işlenmez:** client'ın gönderdiği ciphertext olduğu gibi saklanır, her çağırana olduğu gibi döner ve üzerinde filtre çalışır.
   - Aynı şemadaki **`source: "runtime"`** (`encrypt`) davranışı değişmez: muaf olmayan çağırana token (`ENCRYPTED:AES256:i1:`), muaf çağırana plaintext döner.
3. **Publish kuralları** (`POST api/v1/definitions/publish`):
   - **Reddedilir:**
     - `x-filterOperators` içinde sorgu token'ı (`ge`, `like`) veya bilinmeyen ad (`foo`); mesaj yazılması gereken adı söyler.
     - `evaluatedBy: "server"` ("use 'runtime'").
     - `transition` şemasında runtime computed alan.
   - **Kabul edilir:**
     - `source: client`: `master`, `transition`, legacy `workflow` ve type'sız şemada.
     - `master` şemada runtime computed alan.

## Neden var

vnext-schema #153 ve #155 revizyonunun kararları (2026-10-13/14):
- `x-encrypt` kaldırıldı; alanları `x-encryption` içinde `source: client` olarak kullanılıyor.
- Master şema `client` kaynağını kabul ediyor, ama runtime onu işlemiyor.
- `x-computed`'da `server` adı `runtime` oldu ve runtime bu alanları flag olmadan her zaman hesaplıyor.
- `x-filterOperators`'taki geçersiz değerler publish'te engelleniyor.

vnext branch: `feature/schema-roles-runtime`.

## Akış şekli

```
schema-roles-lab   start-srl → srl-edit [Initial, Human] ──srl-close──▶ srl-done [Finish]
                                 └── srl-update ($self, schema: schema-roles-lab-update)
```

- **master** (`schema-roles-lab-master`, `type: master`):
  - `fullName`: runtime computed; case `kind = corporate` ise `title`, aksi halde fallback `firstName lastName`.
  - `total`: `a + b`.
  - `ratio`: `a / b`, `whenAbsent: null`.
  - `address.label`: `district, city`.
  - `nickname`: client computed.
  - `lastName`: `x-roles` ile yalnız `srl.admin` görür.
  - `pin`: `x-encryption` `client` + `x-filterOperators: ["eq"]`.
  - `secret`: `x-encryption` `runtime` / `encrypt`, `srl.admin` muaf.
- **transition** (`schema-roles-lab-update`, `type: transition`): `passwordEnc` alanı `x-encryption` `client` taşır; `fullName` burada düz bir alandır.

**Kritik adım:** okuma yolunun sonunda computed değerin şekillendirilmiş görünüm üzerinden hesaplanması (`InstanceDataReadService` → `ComputedFieldReadService`) ve yazma yolunda atılması (`InstanceDataWriteService.StripComputedFields`).

## Nasıl koşulur

Ön koşul: yalnız core (`etc/docker/run-docker.sh up core`, vnext `feature/schema-roles-runtime` veya sonrası). MockLab gerekmez.

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~SchemaRolesLab" -v minimal
```

## Geçme kriteri ve bilinen sınırlar

- 16/16 yeşil.
- Kalıcı veri kanıtı Postgres'ten alınır (`schema_roles_lab."InstancesData"`):
  - `fullName`, `total`, `ratio`, `label` ve `stale-from-*` içeren satır **0**;
  - `pin` olduğu gibi saklanmış;
  - `secret` token olarak saklanmış, plaintext **0**.
- **Bilinen sınır:** `InstanceTransitions.Body` request body'yi client'ın gönderdiği haliyle saklar (`fullName: "stale-from-transition"` dahil). Computed alanlar yalnız instance data'dan atılır.
- **Ayırt edicilik:** publish testleri yalnız yeni runtime'da var olan hata mesajlarını (`declare 'gte'`, `use 'runtime'`) assert eder. Eski runtime'da `source` alanı publish'te reddedildiği için akış hiç yayımlanmaz.
- Python yük testi yok (bilinçli): bu bir doğruluk senaryosu.
