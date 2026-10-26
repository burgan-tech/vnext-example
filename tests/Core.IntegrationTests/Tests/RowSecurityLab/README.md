# RowSecurityLab — integration test

Satır güvenliği (`attributes.rowSecurity`, vnext#1107, `DECISION-2026-10-23-row-security`): bir akış rol
başına bir filtre bildirir, runtime bunu **instance listesine** (GraphQL + `groupBy` string route'u,
aggregation'lar dahil) ve **tekil instance GET**'ine AND'ler; `GetInstances` / `GetInstance` trigger
task'ları da bu iki yüzeyden geçer. Domain `core` (cross-domain satırları: `CrossDomainLab`, partner
`rs-xd-target`). Sözleşme ve sonuç tabloları: vnext `docs/domain/row-security.md`.

## Neden var

Kolon düzeyindeki yetki (`x-roles`, `x-masking`, `x-encryption`) vardı, satır boyutu yoktu: şube çalışanı
yalnız kendi şubesinin başvurularını görmeli. Runtime mekanizmayı sağlar; hangi header'ın güvenilir
olduğu domain ekibinin (gateway ön koşulu) sorumluluğudur. Model: `default` yok, `rowSecurity`'li
akış **varsayılan olarak kapalıdır**; eşleşen statik kuralların filtreleri OR'lanır (filtresiz kural = tüm
satırlar), kimlik kuralları (`$InstanceStarter`, `$InstanceBehalfOfStarter`) yalnız bildirilmişse ve kimlik
çözülüyorsa eklenir, sonuç çağıranın filtresiyle AND'lenir. Rol çözümü başarısız ⇒ hiç satır yok.
`rowSecurity`'siz akış byte byte eskisi gibidir.

## Fixture'lar

Hepsi `core/Workflows/row-security-lab/build-row-security-lab.py` ile üretilir (elle düzenleme yok;
değişiklikte sürüm bump'ı — publish sürüm-değişmezdir). Hedef akışların şekli ortaktır:

```
start → active ─rs-close→ closed          updateData: rs-update ($self, gövde veriye merge olur)
```

| Akış | Master schema | `rowSecurity` |
|---|---|---|
| `rs-a` | `rs-lab-schema-plain` | Definition A: `branch-staff` → `branchCode eq $.context.Headers.x-branch-code` · `hq-auditor` (filtresiz) |
| `rs-a-xr` | `rs-lab-schema` (`protected` alanında `x-roles`) | Definition A — rol bir kez çözülür, x-roles budaması ve satır güvenliği aynı sonucu paylaşır |
| `rs-b` | plain | Definition B: A + `{ roles: [$InstanceStarter] }` |
| `rs-s` | plain | `hq-auditor` + yalnız `$InstanceStarter` |
| `rs-behalf` | plain | `hq-auditor` + `$InstanceStarter` (`status eq A`) + `$InstanceBehalfOfStarter` |
| `rs-params` (1.0.1) | plain | `hq-auditor` + parametre biçimi başına **ayrı rol** (aşağıdaki tablo) |
| `rs-plain` | plain | **yok** — değişmeyen akış kontrolü: liste/GET bayt bayt eskisi gibi |
| `rs-plain-xr` | `rs-lab-schema` | **yok** — x-roles'lu şemada ek rol çözümü olmadığının kontrolü |
| `rs-versioned` | plain | 1.0.0: `hq-auditor` (filtresiz) + `branch-staff → branchCode eq x-branch-code`. 1.0.1 (dosyada değil): `hq-auditor` (filtresiz) + `branch-staff → region eq x-region`; tek kaynak `RowSecurityLabTestBase.PublishVersionedV101Async()` (1.0.0 dosyasından üretir, idempotent: 409 "same version" = tamam) |
| `rs-reader` | — | **yok**; trigger task'larını gövdeden sürer (aşağıda) |
| partner `rs-xd-target` | partner `rs-xd-schema` | Definition A |
| `rs-pv-*` (koşuda üretilir) | `rs-pv-schema` | `PublishValidationTests` (Task 11D): `rs-a` dosyasından satır başına yeni anahtar `rs-pv-{satır}-{8 hex}`, yalnız `rowSecurity` değişir. `rs-pv-schema` korumalı yol sınıfı başına bir alan taşır: `protected` (`x-roles`), `secret.pin` (`x-encryption: encrypt`), `tckn` (`hash`), `iban` (`x-masking`); `kind` `required`, `branchCode` ne `required` ne `x-indexed`, `region` `x-indexed` |

**`rs-versioned` sürüm tuzağı.** 1.0.1 bir kez yayınlandıktan sonra bu runtime'da `rs-versioned` için "latest" sonraki **her** koşuda 1.0.1'dir (publish geri alınamaz); kural seti `GetFlowAsync(…, null)` ile pointer ya da latest'ten okunduğu için 1.0.0'ın kuralı yalnız `sys-version-registry` pointer'ı 1.0.0'a sabitliyken geçerlidir. Bu akışı okuyan her test pointer'ı **açıkça** set eder (`Registry.SetPointerAsync("rs-versioned", …)`) ya da temizler (`Registry.ClearPointerAsync`, `finally` içinde) ve hangi kuralın geçerli olduğunu varsaymaz.

Her opt-in akışta filtresiz bir `hq-auditor` kuralı vardır: seed okuması `hq-auditor` ile yapılır ve
`SeedAsync` yedi id'yi listeleyerek doğrular.

Master schema alanları: `testId` (`eq`), `branchCode` (`eq neq in nin isNull`, **`x-indexed` yok** — `eq`
GIN ile karşılanır, `in`/`nin`/`ne` RS-W2a uyarısı bilerek), `amount` (number, `x-indexed`, `eq gt gte
lt lte between`), `region` (`x-indexed`, `eq in`), `owner`, `positionCode` (`eq`), `touchedBy`,
`protected`. **Dikkat:** `x-filterOperators` şema adlarını kullanır: `ne` sorgu token'ıdır, şemada `neq`.

### `rs-params` rol → kural

Çağıran tek bir `rs-p-*` rolü taşıdığında tam bir kurala eşleşir; "hiç satır" sonucu hiçbir geri dönüşün
genişletmediğini kanıtlar. `H.` = `$.context.Headers.`, `Q.` = `$.context.QueryParameters.`,
`CU.` = `$.context.CurrentUser.`

| Rol | Filtre |
|---|---|
| `rs-p-header-eq` | `branchCode eq H.x-branch-code` |
| `rs-p-query-branch` | `branchCode eq Q.branch` |
| `rs-p-query-br` | `branchCode in { $param: Q.br }` (split yok: tekrarlanan anahtar eleman eleman; 1.0.1) |
| `rs-p-in-elements` | `branchCode in [H.x-b1, H.x-b2]` (Form A) |
| `rs-p-in-literal` | `branchCode in [H.x-b1, "BR-999"]` |
| `rs-p-in-split` | `branchCode in { $param: H.x-branches, split: "," }` (Form C) |
| `rs-p-in-param` | `branchCode in { $param: H.x-branches }` (split yok: virgül bölünmez) |
| `rs-p-nin-elements` | `branchCode nin [H.x-excl-1, H.x-excl-2]` |
| `rs-p-ne` | `branchCode ne H.x-branch-code` |
| `rs-p-between` | `amount between [H.x-min, H.x-max]` |
| `rs-p-ge-number` | `amount ge H.x-min` (number'a coerce) |
| `rs-p-not-in` / `rs-p-not-eq` | `not { branchCode in [H.x-b1, H.x-b2] }` / `not { branchCode eq H.x-branch-code }` |
| `rs-p-and-or` | `branchCode eq H.x-branch-code AND (region eq H.x-region OR amount ge H.x-min)` |
| `rs-p-region` | `region eq H.x-region` |
| `rs-p-position` | `positionCode eq CU.Position` |
| `rs-p-actor` / `rs-p-username` | `owner eq CU.ActorUserName` / `owner eq CU.UserName` |
| `rs-p-createdby-starter` | `createdBy eq $InstanceStarter` (statik rol kuralında kısaltma; 1.0.1) |

`rs-params` 1.0.1'dir (publish sürüm-değişmez): 1.0.0'da `rs-p-query-br` `eq` idi ve P-10'un çok değerli
`in` biçimini taşımıyordu, P-22'nin `createdBy eq $InstanceStarter` kuralı yoktu. Kural seti latest'ten okunur.

### `rs-reader`

Transition'lar (`?sync=true`, hedef `reader-ready`): `read-list` (task `rs-get-instances`, tip 15),
`read-one` (`rs-get-instance`, 19), `trigger-one` (`rs-direct-trigger`, 12), `reader-close`. Gövde:

| Alan | Anlam |
|---|---|
| `targetFlow` (zorunlu), `targetDomain` (vars. `core`), `useDapr` | hedef |
| `testId` / `filter` | list: `filter` (ham string; düz filtre ya da `groupBy`'lı zarf) yoksa `testId eq` |
| `targetId` / `targetKey` | get / trigger hedefi; `transitionName` (vars. `rs-update`) |
| `headers` | `{ ad: değer }` task header'larına eklenir (`x-branch-code`, `role`, `sub`, `act_sub` …). Yoksa **eşleme yok**: task yalnız çağıranın kimlik header'larını taşır (`sub`, `act_sub`, `position`, `client_id`, `role`) |

Sonuç veriye yazılır: `readerList { isSuccess, statusCode, count, ids, response }`,
`readerGet { isSuccess, statusCode, id, response }`, `readerTrigger { isSuccess, statusCode, response }`.
Görülemeyen satır bir task hatası olarak fırlatılmaz, `statusCode: 404` diye kaydedilir. Her çağrı için
yeni bir `rs-reader` instance'ı kullan (merge, önceki dizileri taşıyabilir).

## Popülasyon (`RowSecurityLabTestBase.SeedAsync`)

Her test kendi `testId`'siyle yedi satır açar (çağıran `hq-auditor`):

| Satır | branchCode | act_sub / sub | amount | region | owner | positionCode | not |
|---|---|---|---|---|---|---|---|
| r1 | BR-001 | alice / — | 100 | north | alice | POS-A | |
| r2 | BR-001 | carol / — | 200 | north | carol | POS-B | |
| r3 | BR-002 | alice / bob | 300 | south | alice | POS-A | `createdByBehalfOf = bob` |
| r4 | BR-002 | carol / — | 400 | south | carol | POS-B | `rs-close` ⇒ status `C` |
| r5 | BR-003 → **BR-001** | rs-seeder | 500 | east | dave | POS-C | updateData ile taşındı |
| r6 | BR-999 | rs-seeder | 10 | west | erin | POS-C | |
| r7 | — | rs-seeder | 700 | north | frank | POS-A | branchCode yok |

**"Own (BR-001)" en son veri üzerinden değerlendirilir: `{r1, r2, r5}`**. Tekil GET'in probu da
`?version=` eski bir veri sürümü istese bile instance'ın son verisine bakar.

Taban API'si: `SeedAsync(flow, version?)`, `ListIdsAsync(flow, caller, callerFilter?)` (testId her zaman
AND'lenir; `RawNoTestId` hiç filtre göndermez), `ListRawAsync`, `GetAsync(flow, idOrKey, caller,
ifNoneMatch?)` (status, gövde, ETag), `AssertVisibleAsync(flow, population, caller, expected…)` (bir L+G
satırı: liste tam eşit, beklenen her id GET 200, diğerleri 404), `PublishAsync(JsonObject)`,
`LoadWorkflow(key)`, `Registry` (`VersionRegistryClient`: `sys-version-registry` pointer'ını set/clear; `set-default` kural setini de değiştirir), `ProviderIs(…)`.
`Caller(Sub, ActSub, Role, Headers, Query)` yalnız verilen header'ları gönderir — `user_reference` yok.

## Nasıl koşulur

Ön koşul: vnext `run-docker.sh up core` (lokal derleme, row-security dahil), sistem paketi yüklü
(`sys-version-registry` ≥ 1.0.1). SDK `core/**`'ı kendisi publish eder; elle: `wf domain use core &&
wf domain active && wf check && wf sync`.

```bash
# D koşusu (default provider: rol = `role` header'ı)
VNEXT_BASE_URL=http://localhost:4201 dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~RowSecurityLab"

# M koşusu (morph-idm): runtime CallerRoleProvider__Provider=morph-idm ile yeniden başlatılır,
# MockLab seed'i (etc/docker/config/seed/morph-idm-roles-collection.json, rs-idm-* kullanıcıları)
# değiştiyse ÖNCE `run-docker.sh down -v` + yeniden up (seed değişimi volume sıfırlaması ister).
CallerRoleProvider__Provider=morph-idm   # runtime tarafı
VNEXT_CALLER_ROLE_PROVIDER=morph-idm VNEXT_BASE_URL=http://localhost:4201 \
  dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~RowSecurityLab"

# Python smoke (yalnız fonksiyonel fixture, temsili değil)
python3 api-tests/row-security/rs-smoke.py --base-url http://localhost:4201
```

morph-idm seed kullanıcıları (`header.sub` anahtarlı; resolver `act_sub` ya da `client_id` yoksa IDM'e
gitmez, testler ikisini de gönderir): `rs-idm-branch` → `[branch-staff]`, `rs-idm-hq` → `[hq-auditor]`,
`rs-idm-both` → `[branch-staff, hq-auditor]`, `rs-idm-menu` → `[menu.view]`, `rs-idm-blank` → 200 boş
gövde, `rs-idm-emptyroles` → `{"roles":[]}`, `rs-idm-garbage` → 200 `not-json`. Mevcut `idm-empty`
(204) ve `idm-broken` (500) yeniden kullanılır.

## Geçme kriteri + kanıt

`Pinned_*` dışında hepsi yeşil; satır güvenliği olmayan (base) runtime'da Definition/Parameter/GET
satırları kırmızıdır (eski runtime bilinmeyen `rowSecurity` üyesini yok sayar — duman testi 7 satır görür). Yeşil koşu tek başına
kanıt değildir: OpenObserve'te (`vnext` stream) her `vnext.rowsecurity.outcome` değeri için bir trace,
EventId 20505–20511 log'ları; parametre/header değeri hiçbir log/span'de geçmez (canary). Trace sunucusu
yoksa sonuç "testler geçti, trace'e karşı doğrulanmadı" diye etiketlenir.

**Pin listesi** (bilinen, kabul edilmiş davranışı sabitler; yeşil olması beklenir, değişirse bilinçli karar):
H-1 (`role: hq-auditor` header'ı default provider'da genişletir — gateway ön koşulu), H-2 (morph-idm'de
istemci `role` header'ı IDM cevabının yerine geçer), P-30 (`nin` r7'yi dışlar, `not eq` r7'yi içerir),
K-1…K-4 (faz 1 bilinen açığı: `functions/state`/`data`, `GetInstanceData` filtrelenmez; SubFlow çocuğu
kuralları miras almaz, RS-W7), X-6 (cross-domain eşleme `sub`/`act_sub`'ı değiştirirse uzak okuma o
kimlikle yapılır).
V-4 (`V4_Pinned_*`: `allOf`/`anyOf` kural rolü JSON bağlamada reddedilir — 400 `App:900006`, yayın yok, RS-E03 yok; vnext-schema validate önce yakalar).

## Dikkat noktaları

- `rs-idm-slow` (`delayMs: 8000`) **seed'de yok**: MockLab gecikmesi mock başınadır, kural başına değil
  (`CatchAllController`: `delayMs` eşleşen kuraldan bağımsız uygulanır); aynı route'ta ikinci bir mock
  `header.sub` ile ayrıştırılamaz. IDM timeout satırı için MockLab'de kural düzeyinde gecikme gerekir.
- `wf sync`, csx gömülü JSON dosyalarını yeniden yazar (son satır sonu düşer); bu dosyalardaki ilgisiz
  diff'leri commit'leme. `wf sync` fixture'ları düz sürümle (`1.0.0`) yayınlar, SDK `-pkg.` sürümüyle;
  önce hangisi yayınlarsa o kalır (`FlowDefaultVersion` SubProcess testi `-pkg.` bekler).
- **Satır → test (Task 11C):** `ParameterTests` P-1…P-25, P-27…P-30 (`P17` tekrarlanan header satırları için
  ham HTTP/1.0 soketiyle gider — HttpClient tekrarlanan header'ı tek satıra katlar); `CallerFilterAndGroupingTests`
  C-1…C-6, U-route, G-1…G-4 (G-2'nin task yarısı `rs-reader` `read-list` ile, string-route yarısı ayrı `groupBy`
  sorgu parametresiyle); `SingleGetTests` E-1…E-6, E-7a/b/c, E-8. E-7 `rs-versioned` pointer'ını taşır, her satır
  `finally` içinde `ClearPointerAsync` çağırır.
- **P-25b (NUL):** `?branch=%00` kural parametresi olarak Task 11C'de PostgreSQL'e ulaşıp 500 (`22P05`)
  dönüyordu; vnext 5d6ac9e5 ile planlayıcı NUL içeren değeri coercion olarak düşürür (kural katkısız ⇒ 200
  boş / 404). Çağıranın kendi filtresinde `\u0000` hâlâ 500 döner (filtre motoru, satır güvenliği dışı).
- **P-10 / P-17 ayırt edicidir:** P-10 `?br=BR-003&br=BR-002` ⇒ {r3,r4} (ilk-değer bağlama {} verirdi); P-17
  önce iki `x-branches` satırının iki ayrı değer olarak geldiğini (`rs-p-in-param` ⇒ {r1…r5}) kanıtlar, sonra
  iki özdeş geçerli `x-min: 100` satırıyla `between`'in arity nedeniyle düştüğünü gösterir.
- **Satır → test (Task 11D):** `PublishValidationTests` V-1…V-11, V-13…V-20, P-26 (hata: 400 `App:900006`, RS-E kodu
  doğrulama mesajında, `sys-flows/instances/{key}` 404 ⇒ yayınlanmadı) ve W-1, W-2a/b/c, W-4, W-5, W-6a/b (uyarı
  yalnız log'dadır, R-F8: 200, yanıtta `RS-` yok, akış `sys-flows`'ta ve listesi 200). Her W satırı anahtarını,
  kodunu ve UTC zamanını test çıktısına `W-ROW …` satırı olarak yazar (log sorgusu için). Her koşu yeni anahtarlar
  üretir; uyarı satırları runtime'da birikir (zararsız, hiçbir test bu akışları okumaz).
- C-1…C-5 ve U-route yalnız listedir (tasarım gereği: çağıran filtresinin GET karşılığı yok).
- `npm run validate` `attributes.rowSecurity`'yi tanıyan vnext-schema sürümünü ister (o sürüm yayımlanana
  kadar lokal bağlanmış vnext-schema ile koşuldu).
