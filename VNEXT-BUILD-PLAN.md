# vNext Build Plan — OTP Auth SubFlow (`otp-auth`)

## 0. Özet & Kapsam

**Ne:** Parent akışlar tarafından `stateType: 4` ile tüketilecek, `attributes.type: "S"` tipinde bir **OTP kimlik doğrulama SubFlow**'u. Akış; `actor` ile simple-profile sorgular, `scopeGroup`'a göre dallanır, OTP gönderir (Kurumsal'da SIM-bloke durumunda FAST'e yükseltir), kodu doğrular ve başarı hâlinde **authorization code** üretip `success` ile sonlanır.

**Nerede:** Bu repo — `/Users/U0B006/Documents/repos/burgan-tech/vnext-example`, domain **`core`**, componentsRoot `core`. runtimeVersion `0.0.61`, schemaVersion `0.0.52`, `referenceResolution.strictMode: true`.

**Mevcut durum — SIFIRDAN.** Demirleme sonucu:
- TFS'te `otp auth` / `scopeGroup` ile eşleşen iş kaydı **yok**.
- Bu repoda hiçbir branch'te `otp` izi **yok** (`git log --all --grep=otp` boş, `core/**.json` içinde `otp` geçmiyor).
- Devam edilecek bir implementasyon yok → tamamen yeni inşa.

**Girdiler nereden geliyor:** Hepsi **parent'ın subflow start payload'ından** (form veya header değil). Bu bir SubFlow; dış istemci doğrudan başlatmaz, parent `ISubFlowMapping.InputHandler` ile besler. Tek istisna: OTP kodunun kendisi, akış içindeki manuel `verify-otp` transition'ının payload'ı olarak gelir.

**Kullanıcı arayüzü:** **UI'lı** (kullanıcı kararı). OTP kod girişi bu subflow'un içinde `pseudo-ui` view ile render edilir.

**"Bitti" tanımı:**
1. `core/Workflows/otp-auth/otp-auth.json` + tüm bağımlı component'ler yazıldı.
2. `npm run validate` hatasız geçiyor.
3. MockLab seed yüklü ve `otp-auth.http` üzerinden §7'deki **9 senaryo** runtime'da (`localhost:4201`) uçtan uca koşuyor.
4. Harness parent workflow'u ile subflow çağrısı (`stateType: 4`) doğrulandı; `otpAuthResult` parent'a ulaşıyor.

### Kapsam dışı
- Parent/tüketen gerçek iş akışı (yalnızca **test harness** parent'ı üretilir).
- Gerçek OAuth token/redeem/consent state makinesi (`vnext-idm` `token-1.0.0.json`) — biz yalnızca auth-code üreten servise HTTP çağrısı yaparız.
- IVR kanalı (`onboarding` domain'indeki `otp-general-workflow`'da var, bizde yok).
- Gerçek APISIX/Asgard/onboarding-business uçlarına bağlanmak — hepsi MockLab arkasında.
- Müşteri oluşturma/güncelleme (simple-profile **salt-okunur**).
- `onboarding` domain'indeki `otp-general-workflow`'u değiştirmek veya ona cross-domain referans vermek.
- Rate-limit / fraud / device-binding politikaları.

---

## 1. Kabul Kriterleri

### Profil sorgusu
- **AC-01** — Akış başlatıldığında simple-profile `GET .../customers/{actor}/simple-profile` **tam 1 kez** çağrılır; yanıt instance data'ya (`profileFound`, `profilePhone`, `profileEmail`) yazılır.
- **AC-02** — `scopeGroup = Bireysel` ve simple-profile **404 veya 400** dönerse akış `profile-not-found` final'inde (stateType 3 / subType 2) durur; **send-otp hiç çağrılmaz**.
- **AC-03** — simple-profile 5xx/timeout dönerse akış `otp-technical-failure` final'ine düşer; bu AC-02'den **ayrı** state'tir, "kayıt yok" ile karışmaz.
- **AC-04** — `scopeGroup = Kurumsal` ve profil bulunamazsa akış **devam eder** (profil bilgi amaçlıdır — kullanıcı kararı). `profileGate = "pass"`.

### Telefon match
- **AC-05** — `Bireysel` + profil var + `user_phone` ≠ profil telefonu (normalize edilmiş) → akış `phone-mismatch` final'inde durur; send-otp çağrılmaz.
- **AC-06** — `Bireysel` + telefonlar eşleşir → send-otp çağrılır.
- **AC-07** — `Kurumsal`'da telefon match kontrolü **yapılmaz** (kullanıcı kararı); kullanıcının ilettiği `user_phone` doğrudan kullanılır.

### OTP gönderimi ve SIM bloke
- **AC-08** — İlk gönderimde request gövdesinde `OtpType == "Otp"`, `Phone.Prefix` = numaranın ilk 3 hanesi, `Phone.Number` = kalan haneler, `OtpRetryAttempt == otpAttempt`, `OtpDuration == otpTtl` gider.
- **AC-09 (Kurumsal FAST fallback)** — `Kurumsal` + `data.sendOtpStatus == "OtpBlacklisted"` → `simBlocked = true` latch'lenir, `otpType = "Fast"` olur ve **`otp-resending-fast`** state'inde `OtpType: "Fast"` ile yeniden gönderim yapılır.
- **AC-10 (Bireysel FAST YASAK)** — `Bireysel` + `OtpBlacklisted` → akış `sim-blocked` final'inde durur; `OtpType: "Fast"` içeren **hiçbir** çağrı yapılmaz.
- **AC-11 (latch kalıcı)** — `simBlocked = true` bir kez set edildikten sonra, akışın kalanındaki **her** gönderim (resend dâhil) `OtpType: "Fast"` ile yapılır; `"Otp"` bir daha asla gönderilmez.
- **AC-12** — `otp-resending-fast` state'inde tekrar `OtpBlacklisted` gelirse akış `sim-blocked` final'ine gider (sonsuz FAST döngüsü yok).
- **AC-13** — `sendOtpStatus == "SendOtpSuccess"` → akış `otp-awaiting-code` state'ine geçer; `otpSentAt` ve `otpExpiresAt = otpSentAt + otpTtl` yazılır.

### Attempt sayacı
- **AC-14** — `otpAttempt = N` iken kullanıcı arka arkaya N kez yanlış kod girerse N'inci hatadan sonra akış `attempt-exceeded` final'inde durur; öncesinde `otp-awaiting-code`'a geri döner.
- **AC-15** — Her yanlış girişte `attemptUsed` **tam 1** artar; doğru girişte artmaz.
- **AC-16 (sıfırlanmaz)** — `attemptUsed`, yeniden gönderimde (`resend-otp`) **sıfırlanmaz**; instance ömrü boyunca toplam haktır (kullanıcı kararı).

### TTL ve yeniden gönderim
- **AC-17** — `otp-awaiting-code`'a her girişte TTL timer'ı kurulur; süre **mutlak** `otpExpiresAt` üzerinden hesaplanır (`remaining = otpExpiresAt − now`, alt sınır 1 sn, üst sınır `otpTtl`). Hatalı deneme döngüsü TTL'i **uzatmaz**.
- **AC-18** — TTL dolunca akış `otp-ttl-gate`'e geçer; `resendUsed < resendLimit` ise **`otp-awaiting-resend`** (kullanıcıya manuel "tekrar gönder" hakkı), aksi hâlde `ttl-expired` final'i (stateType 3 / subType **8 Timeout**).
- **AC-19** — `otp-awaiting-resend`'de kullanıcı `resend-otp`'u tetiklerse `resendUsed` 1 artar ve akış `otp-sending`'e döner; latch'liyse gönderim `Fast` tipiyle yapılır.
- **AC-20** — Tüm timer/auto transition'larda `view` alanı `null`'dır.

### Auth code & success
- **AC-21** — validate-otp başarılı olunca auth-code servisi `client_id`, `client_secret`, `grant_type`, `scopeGroup`, `sub`, `actor`, `otpReference` ile çağrılır.
- **AC-22** — Başarı hâlinde akış `otp-success` final'inde (stateType 3 / subType **1 Successful**) durur ve `authorizationCode` instance data'da bulunur.
- **AC-23** — Auth-code servisi hata dönerse akış `auth-code-failed` final'inde durur; `authorizationCode` boş kalır.

### SubFlow sözleşmesi
- **AC-24** — Parent `stateType: 4` state'inden çağırdığında, child final'e ulaşınca parent'ın `ISubFlowMapping.OutputHandler`'ı `context.Body` üzerinden `otpAuthResult`, `otpAuthResultDetail`, `authorizationCode` okuyabilir.
- **AC-25** — Tüm final state'ler ayırt edilebilir `key` ve doğru `stateSubType` taşır; `subType: 1` **yalnızca** `otp-success`'te.
- **AC-26 (hassas veri)** — `client_secret` ve `otpCode` parent'a geri merge **edilmez**.

### Kalite kapısı
- **AC-27** — `npm run validate` hatasız; tüm referanslar `{key, domain, flow, version}` nested biçimde ve strict mode'a uygun.
- **AC-28** — §7'deki 9 senaryo `.http` dosyasından uçtan uca koşar.

---

## 2. Component Envanteri

Yollar `vnext.config.json`'dan türetildi: `componentsRoot: core`, `workflows: Workflows`, `tasks: Tasks`, `schemas: Schemas`, `views: Views`, `mappings: Mappings`.

| Tip | key / dosya | Ne yapıyor | Durum |
|---|---|---|---|
| Workflow | `core/Workflows/otp-auth/otp-auth.json` | Ana SubFlow (`type: "S"`), 19 state | **YENİ** |
| Workflow | `core/Workflows/otp-auth/otp-auth-harness.json` | Subflow'u `stateType: 4` ile tüketen test parent'ı | **YENİ** |
| Schema | `core/Schemas/otp-auth/otp-auth-master.json` | Instance/master şeması (`additionalProperties: true`) | **YENİ** |
| Schema | `core/Schemas/otp-auth/otp-auth-start-payload.json` | `startTransition.schema` — 12 parent girdisi | **YENİ** |
| Schema | `core/Schemas/otp-auth/otp-code-payload.json` | `verify-otp` transition payload'ı | **YENİ** |
| Task | `core/Tasks/otp-auth/get-simple-profile.json` | HTTP `"6"` GET — profil sorgusu | **YENİ** |
| Task | `core/Tasks/otp-auth/send-otp.json` | HTTP `"6"` POST — OTP gönderimi (Otp/Fast) | **YENİ** |
| Task | `core/Tasks/otp-auth/validate-otp.json` | HTTP `"6"` POST — kod doğrulama | **YENİ** |
| Task | `core/Tasks/otp-auth/issue-authorization-code.json` | HTTP `"6"` POST — auth code üretimi | **YENİ** |
| Task | `core/Tasks/otp-auth/otp-script-task.json` | Script `"7"` — saf state bookkeeping | **YENİ** |
| View | `core/Views/otp-auth/otp-code-entry-view.json` | pseudo-ui OTP kod giriş formu | **YENİ** |
| View | `core/Views/otp-auth/otp-resend-view.json` | pseudo-ui "kodun süresi doldu / tekrar gönder" ekranı | **YENİ** |
| Mapping | `core/Mappings/otp-auth/otp-auth-helpers.json` + `src/OtpAuthHelpers.csx` | Paylaşılan yardımcılar (`MergeInstance`, `Str`, `Int`, `Bool`, `GateIs`, `SplitPhone`) | **YENİ** |
| `.csx` mapping | `core/Workflows/otp-auth/src/*Mapping.csx` (12 dosya, §3.6) | Task I/O + gate hesabı + sonuç yazımı | **YENİ** |
| `.csx` timer | `core/Workflows/otp-auth/src/OtpTtlTimer.csx` | `ITimerMapping`, mutlak deadline | **YENİ** |
| `.csx` rule | `core/Workflows/otp-auth/src/*Rule.csx` (17 dosya, §3.6) | Gate eşitlik kontrolleri | **YENİ** |
| Mock seed | `etc/docker/config/seed/otp-auth-collection.json` | 4 endpoint + kural varyantları | **YENİ** |
| Test | `core/Workflows/otp-auth/otp-auth.http` | 9 senaryo | **YENİ** |
| Function | — | **GEREKMEZ** — LOV/lookup yok; durum sorgusu runtime `functions/state` ile | — |
| Extension | — | **GEREKMEZ** — cross-cutting enrichment ihtiyacı yok | — |
| SubFlow deseni | `core/Workflows/subflow-orchestration/` | Referans desen (parent stateType 4 + child `type "S"`) | **ZATEN-VAR** — değiştirilmez |
| Timer deseni | `core/Workflows/money-transfer/src/PushTimeoutTimer.csx` | `ITimerMapping` örneği (sabit süre) | **ZATEN-VAR** |
| errorBoundary deseni | `core/Workflows/money-transfer/money-transfer.json` | `onError[].errorCodes/action/transition/priority` | **ZATEN-VAR** |
| HTTP task zarfı | `core/Tasks/account-opening/create-bank-account.json` | `type: "6"` + `API_BASEURL` placeholder | **ZATEN-VAR** |
| Secret deseni | `core/Workflows/secret-cache-lab/src/SecretProbeMapping.csx` | `GetSecretAsync("vnext-secret","workflow-secret",…)` | **ZATEN-VAR** |
| MockLab altyapısı | `docker-compose.yml`, `etc/docker/config/seed/` | Mock servis katmanı | **ZATEN-VAR** |
| Banka-geneli benzer akış | `onboarding` / `otp-general-workflow` (vnext-onboarding) | Kontrat kaynağı (SIM-bloke, OtpType, body şekli) | **ZATEN-VAR (başka domain)** — kopyalanmaz, yalnız kontratı alınır |

---

## 3. State Machine Tasarımı

### 3.0 Çekirdek desen — "gate discriminator"

Aynı state'ten çıkan birden fazla `triggerType: 1` transition'ın **karşılıklı dışlayıcı ve bütün** olması motorun en kırılgan noktasıdır. Bunu rule'lara dağıtmak yerine tek yerde çözüyoruz:

> Her task'ın `IMapping.OutputHandler`'ı instance data'ya **tek bir string gate alanı** yazar (`profileGate`, `sendGate`, `verifyGate`, `authGate`, `resendGate`). Mapping içindeki switch **total**'dir — tanınmayan/beklenmeyen her durum `"failed"` yazar, `catch` bloğu dâhil. Rule dosyaları yalnızca eşitlik kontrolü yapar.

Sonuç: rule örtüşmesi imkânsız (tek alan, ayrık değerler), boşta kalan dal yok (her yolda bir değer yazılır), rule dosyaları tek satır.

**`triggerKind: 10` kullanımı:** Yalnızca gerçekten koşulsuz, tek çıkışlı `otp-initializing` state'inde. Ruled kardeşlerin yanına fallback **konmuyor** — repoda `triggerKind: 10`'un ruled kardeşlerle birlikte değerlendirme sırası kanıtlanmamış (bkz. §9-R1).

### 3.1 startTransition

| Alan | Değer |
|---|---|
| `key` | `start-otp-auth` |
| `target` | `otp-initializing` |
| `triggerType` | `0` (şema `const: 0`) |
| `versionStrategy` | `Major` |
| `schema` | `{ key: "otp-auth-start-payload", domain: "core", flow: "sys-schemas", version: "1.0.0" }` |
| `onExecutionTasks[0]` | `otp-script-task` (type 7) + `./src/OtpAuthStartMapping.csx` |

`OtpAuthStartMapping.OutputHandler` normalizasyonu: `user_phone` → `phonePrefix` (ilk 3) / `phoneNumber` (kalan) / `phoneCountryCode = "90"`; `otpAttempt` yoksa 3; `otpTtl` yoksa 180; `resendLimit` yoksa 3; `attemptUsed = 0`; `resendUsed = 0`; `simBlocked = false`; `otpType = "Otp"`; `otpAuthResult = "in-progress"`.

### 3.2 State listesi (19)

| # | key | stateType | subType | view | onEntries | errorBoundary |
|---|---|---|---|---|---|---|
| 1 | `otp-initializing` | 1 Initial | 0 | `null` | — | — |
| 2 | `profile-lookup` | 2 | 5 Busy | `null` | `get-simple-profile` + `GetSimpleProfileMapping` | ✔ |
| 3 | `profile-gate-recheck` | 2 | 0 | `null` | — | — |
| 4 | `otp-sending` | 2 | 5 Busy | `null` | `send-otp` + `SendOtpMapping` | ✔ |
| 5 | `otp-resending-fast` | 2 | 5 Busy | `null` | `send-otp` + `SendOtpMapping` (aynı) | ✔ |
| 6 | `otp-awaiting-code` | 2 | 6 Human | **`otp-code-entry-view`** | — | — |
| 7 | `otp-verifying` | 2 | 5 Busy | `null` | `validate-otp` + `ValidateOtpMapping` | ✔ |
| 8 | `otp-ttl-gate` | 2 | 0 | `null` | — | — |
| 9 | `otp-awaiting-resend` | 2 | 6 Human | **`otp-resend-view`** | — | — |
| 10 | `auth-code-issuing` | 2 | 5 Busy | `null` | `issue-authorization-code` + `IssueAuthorizationCodeMapping` | ✔ |
| 11 | `otp-success` | 3 Final | **1** Successful | `null` | — | — |
| 12 | `profile-not-found` | 3 | **2** Error | `null` | — | — |
| 13 | `phone-mismatch` | 3 | **2** Error | `null` | — | — |
| 14 | `sim-blocked` | 3 | **2** Error | `null` | — | — |
| 15 | `attempt-exceeded` | 3 | **2** Error | `null` | — | — |
| 16 | `ttl-expired` | 3 | **8** Timeout | `null` | — | — |
| 17 | `auth-code-failed` | 3 | **2** Error | `null` | — | — |
| 18 | `otp-technical-failure` | 3 | **2** Error | `null` | — | — |
| 19 | `otp-cancelled` | 3 | **7** Cancelled | `null` | — | — |

**View yerleşimi gerekçesi:** `otp-awaiting-code` ve `otp-awaiting-resend` Wizard (`stateType 5`) değil, insan-bekleyen Intermediate state'lerdir; runtime state'e girildiği anda formu servis eder. Bu, `onboarding/otp-general-workflow`'un `otp-entry-ivr` state'iyle **aynı desendir** (state.view dolu, manual transition `view: null`). Manuel transition yalnızca `schema` taşır.

### 3.3 Transition haritası

**`otp-initializing`** — koşulsuz tek çıkış (burada `triggerKind: 10` meşru)

| key | target | triggerType | triggerKind | rule |
|---|---|---|---|---|
| `init-to-profile-lookup` | `profile-lookup` | 1 | **10** | yok (`rule/view/mapping: null`) |

**`profile-lookup`** — `profileGate` ∈ `pass | not-found | phone-mismatch | failed`

| key | target | triggerType | rule | anlamı |
|---|---|---|---|---|
| `profile-pass` | `otp-sending` | 1 | `ProfileGatePassRule` | **Kurumsal (her hâlükârda)** veya Bireysel + kayıt var + telefon eşleşiyor |
| `profile-missing` | `profile-not-found` | 1 | `ProfileGateNotFoundRule` | Bireysel + kayıt yok |
| `profile-phone-mismatch` | `phone-mismatch` | 1 | `ProfileGateMismatchRule` | Bireysel + kayıt var + telefon uyuşmuyor |
| `profile-gate-failed` | `otp-technical-failure` | 1 | `ProfileGateFailedRule` | beklenmeyen yanıt |
| `profile-lookup-not-found` | `profile-gate-recheck` | **0** | — | errorBoundary (404/400) tetikler; `otp-script-task` + `ProfileNotFoundFallbackMapping` |
| `profile-lookup-technical-failure` | `otp-technical-failure` | **0** | — | errorBoundary (`*`); `SetTechnicalFailureResultMapping` |

**`profile-gate-recheck`** — 404/400 sonrası scope-farkındalıklı yeniden yönlendirme. `ProfileNotFoundFallbackMapping` `profileFound = false` yazar ve `profileGate`'i scopeGroup'a göre hesaplar (**Kurumsal → `pass`**, Bireysel → `not-found`). Bu state, `profile-lookup`'ın ilk 4 auto transition'ının **aynı rule dosyalarını** referans eden kopyalarını taşır (`recheck-pass`, `recheck-missing`, `recheck-phone-mismatch`, `recheck-failed`), aynı hedeflere gider.

**`otp-sending`** — `sendGate` ∈ `sent | escalate-fast | blocked | failed`

| key | target | triggerType | rule | koşul |
|---|---|---|---|---|
| `send-otp-sent` | `otp-awaiting-code` | 1 | `SendGateSentRule` | `sendOtpStatus == "SendOtpSuccess"` |
| `send-otp-escalate-fast` | `otp-resending-fast` | 1 | `SendGateEscalateFastRule` | `OtpBlacklisted` **&&** Kurumsal **&&** `simBlocked == false` |
| `send-otp-blocked` | `sim-blocked` | 1 | `SendGateBlockedRule` | `OtpBlacklisted` **&&** (Bireysel **‖** `simBlocked == true`) |
| `send-otp-failed` | `otp-technical-failure` | 1 | `SendGateFailedRule` | diğer/eksik statü |
| `send-otp-technical-failure` | `otp-technical-failure` | **0** | — | errorBoundary (`*`) |

**`otp-resending-fast`** — aynı `sendGate` alanı, aynı rule dosyaları yeniden kullanılır. **`escalate-fast` transition'ı burada TANIMLANMAZ** — latch zaten `true` olduğu için üretilmesi de imkânsızdır; tanımlanırsa sonsuz FAST döngüsü açılır.

| key | target | triggerType | rule |
|---|---|---|---|
| `fast-otp-sent` | `otp-awaiting-code` | 1 | `SendGateSentRule` (yeniden kullanım) |
| `fast-otp-blocked` | `sim-blocked` | 1 | `SendGateBlockedRule` (yeniden kullanım) |
| `fast-otp-failed` | `otp-technical-failure` | 1 | `SendGateFailedRule` (yeniden kullanım) |
| `fast-otp-technical-failure` | `otp-technical-failure` | **0** | errorBoundary |

**`otp-awaiting-code`** — insan etkileşimi

| key | target | triggerType | schema / timer | notlar |
|---|---|---|---|---|
| `verify-otp` | `otp-verifying` | **0** Manual | `schema: otp-code-payload` | `view: null` (form state.view'da), `rule/timer: null` |
| `otp-ttl-expired` | `otp-ttl-gate` | **2** Scheduled | `timer: { type: "L", location: "./src/OtpTtlTimer.csx" }` | `rule/schema/view/mapping` **null zorunlu**; `onExecutionTasks[0] = otp-script-task + EvaluateResendGateMapping` |

**`otp-verifying`** — `verifyGate` ∈ `verified | retry | exceeded | expired | failed`. `attemptUsed` **burada**, `ValidateOtpMapping.OutputHandler` içinde artar; `attemptUsed >= otpAttempt` karşılaştırması da aynı mapping'te yapılıp `retry`/`exceeded` ayrışır. Rule'lar sayı karşılaştırması yapmaz.

| key | target | triggerType | rule |
|---|---|---|---|
| `otp-verified` | `auth-code-issuing` | 1 | `VerifyGateVerifiedRule` |
| `otp-retry-available` | `otp-awaiting-code` | 1 | `VerifyGateRetryRule` |
| `otp-attempt-exceeded` | `attempt-exceeded` | 1 | `VerifyGateExceededRule` |
| `otp-code-expired` | `otp-ttl-gate` | 1 | `VerifyGateExpiredRule` (servis "expired" derse — resend hakkı varsa kullanıcıya verilir) |
| `otp-verify-failed` | `otp-technical-failure` | 1 | `VerifyGateFailedRule` |
| `verify-technical-failure` | `otp-technical-failure` | **0** | errorBoundary |

**`otp-ttl-gate`** — `resendGate` ∈ `allowed | exhausted` (kullanıcı kararı: manuel tekrar gönder)

| key | target | triggerType | rule |
|---|---|---|---|
| `ttl-resend-allowed` | `otp-awaiting-resend` | 1 | `ResendGateAllowedRule` (`resendUsed < resendLimit`) |
| `ttl-resend-exhausted` | `ttl-expired` | 1 | `ResendGateExhaustedRule` + `SetTtlExpiredResultMapping` |

**`otp-awaiting-resend`** — kullanıcıya manuel "tekrar gönder" hakkı

| key | target | triggerType | timer | notlar |
|---|---|---|---|---|
| `resend-otp` | `otp-sending` | **0** Manual | — | `onExecutionTasks: otp-script-task + IncrementResendMapping` → `resendUsed++`, yeni `otpExpiresAt` gönderimde yazılır. **`attemptUsed` sıfırlanmaz** (AC-16). Latch'liyse `otpType` zaten `"Fast"`. |
| `resend-abandoned` | `ttl-expired` | **2** Scheduled | `AbandonTimer.csx` (sabit, ör. PT5M) | Kullanıcı hiç dönmezse instance sonsuza kadar açık kalmasın |

**Workflow seviyesi:** `cancel: { key: "cancel-otp-auth", target: "otp-cancelled", triggerType: 0, onExecutionTasks: [otp-script-task + SetCancelledResultMapping] }`. `updateData` ve `exit` **eklenmez** (gereksiz dış yüzey).

### 3.4 SIM-bloke LATCH'i

| Adım | Nerede | Ne olur |
|---|---|---|
| 1 | `OtpAuthStartMapping` | `simBlocked = false`, `otpType = "Otp"` |
| 2 | `SendOtpMapping.InputHandler` | body'ye `OtpType = data.otpType` yazar |
| 3 | `SendOtpMapping.OutputHandler` | `sendOtpStatus == "OtpBlacklisted"` **&&** `scopeGroup == "Kurumsal"` **&&** `simBlocked == false` → `simBlocked = true`, `simBlockedAt = utcNow`, `otpType = "Fast"`, `sendGate = "escalate-fast"` |
| 4 | `otp-resending-fast` | Aynı task/mapping; `InputHandler` artık `OtpType = "Fast"` gönderir |
| 5 | Tekrar `OtpBlacklisted` | 3. adımdaki `simBlocked == false` koşulu artık **false** → `sendGate = "blocked"` → `sim-blocked` final. **Bir daha `Otp` denenmez.** |
| 6 | `resend-otp` sonrası | `otpType` hâlâ `"Fast"` → gönderim Fast ile yapılır (AC-11) |

Bireysel'de 3. adımın `scopeGroup == "Kurumsal"` koşulu tutmadığı için `escalate-fast` hiç üretilmez → **FAST yolu Bireysel için tanım gereği erişilemez** (AC-10).

### 3.5 TTL timer'ı — mutlak deadline

`OtpTtlTimer.csx` (`ITimerMapping`), `otp-awaiting-code`'a **her girişte** yeniden kurulur (retry dönüşü dâhil). Sabit süre **vermez**; `otpExpiresAt` (SendOtpMapping'in `utcNow + otpTtl` olarak yazdığı mutlak an) üzerinden kalanı hesaplar:

```
remaining = otpExpiresAt - utcNow;
if (remaining <= 0)     remaining = 1sn;    // derhal ateşle
if (remaining > otpTtl) remaining = otpTtl; // güvenlik tavanı
return TimerSchedule.FromDuration(remaining);
```

Kritik faydası: yanlış kod → `otp-verifying` → `otp-awaiting-code` döngüsü TTL'i **uzatmaz**. Sabit `FromDuration(otpTtl)` kullanılsaydı kullanıcı her hatalı denemede kendine yeni pencere kazandırırdı. Repodaki mevcut iki timer (`PushTimeoutTimer.csx`, `chain-busy/src/LeafExpireTimer.csx`) sabit süre kullanıyor; buradaki dinamik varyant yeni.

### 3.6 `.csx` envanteri

**Mapping'ler** (`ScriptBase, IMapping`; `OutputHandler` daima önce `context.Instance.Data`'yı kopyalar, sonra üzerine yazar):

| Dosya | Sorumluluk |
|---|---|
| `OtpAuthStartMapping.csx` | Girdi normalizasyonu, telefon parçalama, default'lar, sayaç/latch sıfırlama |
| `GetSimpleProfileMapping.csx` | URL/başlık kurulumu, `profileFound/profilePhone/profileEmail`, **`profileGate`**, terminal ise `otpAuthResult` |
| `ProfileNotFoundFallbackMapping.csx` | 404/400 sonrası scope-farkındalıklı `profileGate` |
| `SendOtpMapping.csx` | OTP body kurulumu, `sendOtpStatus`, **`sendGate`**, **LATCH**, `otpSentAt`/`otpExpiresAt`/`otpReference` |
| `ValidateOtpMapping.csx` | Kod doğrulama, `attemptUsed++`, **`verifyGate`** |
| `EvaluateResendGateMapping.csx` | **`resendGate`** = `resendUsed < resendLimit ? "allowed" : "exhausted"` |
| `IncrementResendMapping.csx` | `resendUsed++` |
| `IssueAuthorizationCodeMapping.csx` | Auth code çağrısı, `authorizationCode`, **`authGate`**, `otpAuthResult` |
| `SetTtlExpiredResultMapping.csx` | `otpAuthResult = "ttl-expired"` |
| `SetCancelledResultMapping.csx` | `otpAuthResult = "cancelled"` |
| `SetTechnicalFailureResultMapping.csx` | `otpAuthResult = "technical-failure"` + detay |
| `SetAuthCodeFailedResultMapping.csx` | `otpAuthResult = "auth-code-failed"` |
| `OtpTtlTimer.csx` | **`ITimerMapping`** — mutlak deadline |
| `AbandonTimer.csx` | **`ITimerMapping`** — sabit terk süresi |
| `HostToOtpAuthSubFlowMapping.csx` (harness) | **`ISubFlowMapping`** — parent↔child sözleşmesi |

**Rule'lar** (`ScriptBase, IConditionMapping`, tek satır eşitlik): `ProfileGate{Pass,NotFound,Mismatch,Failed}Rule`, `SendGate{Sent,EscalateFast,Blocked,Failed}Rule`, `VerifyGate{Verified,Retry,Exceeded,Expired,Failed}Rule`, `ResendGate{Allowed,Exhausted}Rule`, `AuthGate{Issued,Failed}Rule` → **17 dosya**. `profile-gate-recheck` ve `otp-resending-fast` bunları yeniden kullanır.

### 3.7 errorBoundary yerleşimi

`profile-lookup` state seviyesinde, öncelik sıralı (desen: `money-transfer.json`):

| priority | errorCodes | action | transition |
|---|---|---|---|
| 10 | `["404","400","Task:404","Task:400"]` | 4 Notify | `profile-lookup-not-found` |
| 100 | `["*"]` | 4 Notify | `profile-lookup-technical-failure` |

`otp-sending` / `otp-resending-fast` / `otp-verifying`: tek kural `["*"]` → ilgili `*-technical-failure`. Opsiyonel sertleştirme: priority 10'da `action: 1` Retry + `retryPolicy { maxRetries: 2, initialDelay: "PT2S", backoffType: 1 }`.
`auth-code-issuing`: `["*"]` → `auth-code-technical-failure` → `auth-code-failed`.

### 3.8 Final state'ler ve parent'a dönen sonuç

Çıkış alanı **`otpAuthResult`** (+ serbest metin `otpAuthResultDetail`). Ayrı bir "sonuç yazan" state yok — sonuç, hedefi belirleyen **gate'i hesaplayan mapping'in içinde aynı anda** yazılır (timer/cancel yolları hariç; onlar transition `onExecutionTasks`'ıyla yazar).

| Final state | subType | `otpAuthResult` | Yazan |
|---|---|---|---|
| `otp-success` | 1 | `success` (+`authorizationCode`) | `IssueAuthorizationCodeMapping` |
| `profile-not-found` | 2 | `profile-not-found` | `GetSimpleProfileMapping` / `ProfileNotFoundFallbackMapping` |
| `phone-mismatch` | 2 | `phone-mismatch` | `GetSimpleProfileMapping` |
| `sim-blocked` | 2 | `sim-blocked` | `SendOtpMapping` |
| `attempt-exceeded` | 2 | `attempt-exceeded` | `ValidateOtpMapping` |
| `ttl-expired` | **8** | `ttl-expired` | `SetTtlExpiredResultMapping` |
| `auth-code-failed` | 2 | `auth-code-failed` | `IssueAuthorizationCodeMapping` / `SetAuthCodeFailedResultMapping` |
| `otp-technical-failure` | 2 | `technical-failure` | ilgili gate mapping'i / `SetTechnicalFailureResultMapping` |
| `otp-cancelled` | **7** | `cancelled` | `SetCancelledResultMapping` |

---

## 4. Şema Alanları

### 4.1 `otp-auth-master` — `$id: urn:vnext:res:schema:core:otp-auth-master`, `additionalProperties: true`

| Alan | Tip | Zorunlu | Validasyon / enum | Lokalizasyon (tr/en) |
|---|---|---|---|---|
| `sub` | string | ✔ | `minLength: 1` | Müşteri / Customer |
| `actor` | string | ✔ | simple-profile `customerId`'si **budur** | Kullanıcı / User |
| `client_id` | string | ✔ | — | İstemci / Client |
| `grant_type` | string | ✔ | — | Yetki Tipi / Grant Type |
| `client_secret` | string | ✔ | **hassas — log'lanmaz, view'a bağlanmaz** | — |
| `user_phone` | string | ✔ | `pattern: "^[1-9][0-9]{9}$"` | Telefon / Phone |
| `user_email` | string | — | `format: "email"` | E-posta / Email |
| `user_name` | string | — | — | Ad / Name |
| `user_surname` | string | — | — | Soyad / Surname |
| `scopeGroup` | string | ✔ | `enum: ["Bireysel","Kurumsal"]` | Kapsam / Scope Group |
| `otpAttempt` | integer | — | `min 1, max 5, default 3` | Deneme Hakkı / Attempts |
| `otpTtl` | integer (sn) | — | `min 30, max 600, default 180` | Süre / TTL |
| `resendLimit` | integer | — | `min 0, max 5, default 3` | Tekrar Gönderim Hakkı |
| `phonePrefix` / `phoneNumber` / `phoneCountryCode` | string | — | türetilmiş (3 / kalan / `"90"`) | — |
| `profileFound` | boolean | — | — | — |
| `profilePhone` / `profileEmail` | string | — | — | — |
| `profileGate` | string | — | `enum: ["pass","not-found","phone-mismatch","failed"]` | — |
| `otpType` | string | — | `enum: ["Otp","Fast"]`, default `"Otp"` | — |
| **`simBlocked`** | boolean | — | default `false` — **LATCH** | — |
| `simBlockedAt` | string | — | `format: date-time` | — |
| `sendOtpStatus` | string | — | ham servis statüsü | — |
| `sendGate` | string | — | `enum: ["sent","escalate-fast","blocked","failed"]` | — |
| `otpReference` | string | — | validate + auth-code çağrılarında taşınır | — |
| `otpSentAt` / `otpExpiresAt` | string | — | `format: date-time` | — |
| `attemptUsed` | integer | — | `min 0`, default 0 | Kullanılan Deneme |
| `resendUsed` | integer | — | `min 0`, default 0 | Kullanılan Tekrar Gönderim |
| `verifyGate` | string | — | `enum: ["verified","retry","exceeded","expired","failed"]` | — |
| `resendGate` | string | — | `enum: ["allowed","exhausted"]` | — |
| `otpVerified` | boolean | — | — | — |
| `authorizationCode` | string | — | başarı çıktısı | Yetki Kodu / Auth Code |
| `authGate` | string | — | `enum: ["issued","failed"]` | — |
| **`otpAuthResult`** | string | — | `enum: ["in-progress","success","profile-not-found","phone-mismatch","sim-blocked","attempt-exceeded","ttl-expired","auth-code-failed","technical-failure","cancelled"]` | Sonuç / Result |
| `otpAuthResultDetail` | string | — | serbest metin | — |

### 4.2 `otp-auth-start-payload` — `$id: urn:vnext:res:schema:core:otp-auth-start-payload`, `additionalProperties` belirtilmez (D2)

`required: ["sub","actor","client_id","grant_type","client_secret","user_phone","scopeGroup"]`
Alanlar: master'daki **12 parent girdisi + `resendLimit`** birebir (aynı tip/pattern/enum; `otpAttempt`/`otpTtl`/`resendLimit` opsiyonel + default). Türetilmiş/runtime alanları burada **yer almaz**. Planlanan `additionalProperties: false` sıkılaştırması UYGULANMADI (bkz. §11/D2): SubFlow InputHandler'ın taşıyabileceği fazladan bir alan akışı 400 ile düşürürdü.

### 4.3 `otp-code-payload` — `$id: urn:vnext:res:schema:core:otp-code-payload`, `additionalProperties: false`

| Alan | Tip | Zorunlu | Validasyon | `x-labels` |
|---|---|---|---|---|
| `otpCode` | string | ✔ | `pattern: "^[0-9]{4,8}$"` | tr: "SMS Kodu", en: "OTP Code" |

Master `attributes.type: "workflow"` (**`required` YOK**, `additionalProperties: true` — runtime her instance-data merge'inde doğrular). İki payload `attributes.type: "schema"` (`required` dolu). Şema component enum'unda `transition` diye bir tip yoktur; repo konvansiyonu payload için `schema` kullanır (`money-transfer-input`, `role-matrix-decision`).

---

## 5. View Tasarımı

Renderer: **`pseudo-ui`** (platformun resmî UI SDK'sı). `$schema: https://amorphie.io/meta/view-vocabulary/1.0`.

### 5.1 `otp-code-entry-view` — `core/Views/otp-auth/otp-code-entry-view.json`

| Alan | Değer |
|---|---|
| Bağlandığı yer | `otp-awaiting-code` state'inin `view`'ı (`state.view`, transition'ın değil — §3.2 gerekçesi) |
| `dataSchema` | `urn:vnext:res:schema:core:otp-code-payload` (**transition payload şeması** — input view kuralı) |
| Layout niyeti | `ScrollView > Column`: başlık `Text`, maskeli telefon bilgisi `Text` (`$instance.phoneNumber` son 2 hane görünür), `TextField` (`bind: "otpCode"`, numeric, maxLength 6), kalan deneme `Text` (`$instance.attemptUsed` / `$instance.otpAttempt`), gönder `Button` |
| Buton | `{ "action": "submit", "command": "urn:vnext:flow:transition:core:otp-auth:verify-otp" }` — `submit` varsayılan olarak validasyon çalıştırır |
| İkonlar | Material Symbols snake_case: `sms`, `schedule`, `lock` |

### 5.2 `otp-resend-view` — `core/Views/otp-auth/otp-resend-view.json`

| Alan | Değer |
|---|---|
| Bağlandığı yer | `otp-awaiting-resend` state'inin `view`'ı |
| `dataSchema` | `urn:vnext:res:schema:core:otp-auth-master` (**display view** — `$instance` okur, form yok) |
| Layout niyeti | `Column`: "Kodun süresi doldu" `Text`, kalan tekrar gönderim hakkı `Text` (`$instance.resendUsed` / `$instance.resendLimit`), `Button` |
| Buton | `{ "action": "dispatch", "command": "urn:vnext:flow:transition:core:otp-auth:resend-otp", "validate": false }` |
| İkon | `refresh` |

**Final state'lerde view yok.** Sonuç ekranını parent akış render eder — subflow yalnızca `otpAuthResult`'ı döndürür. (Bu bir varsayım; §9-S1'de teyit.)

---

## 6a. Backend/DB Kontratı — DEMİRLENMİŞ

Bu akışın **kendi DB tablosu yok**; tüm durum vNext instance data'sında. Aşağıdakiler tükettiği dış servis kontratlarıdır.

### simple-profile (Asgard)
| Alan | Değer | Kanıt |
|---|---|---|
| Metot / path | Prod: `GET {base}/customers/{customerId}/simple-profile`. **Lab: `GET {base}/api/core/customers/simple-profile?customerId={actor}`** — MockLab route parametresi desteklemiyor (R12 gerçekleşti, öngörülen azaltım uygulandı). | `vnext-idm` → `idm-utils/src/BBT.IdmUtils.Infrastructure/Customer/CustomerAsgardHttpClient.cs:37-39` |
| `{customerId}` | **`actor`** (işlem yapan kullanıcı) | Gereksinim: "actor bilgisi ile sorgulanır" |
| **404 / 400** | **"kayıt yok"** — exception değil, iş sonucu | `CustomerAsgardHttpClient.cs:42-47` (Provider_bff deseni: `return null`) |
| Yanıt | Kimlik + profil (business line, kanal segmenti) + **telefon** + e-posta + ortaklık ilişkileri | Servis kataloğu: `asgard-customer-simple-profile`, repo `Asgard`, ns `intprod-asgard`, DB `CustomerDB`/`SSOV3` |
| Prod route | `/asgard-customer-simple-profile/*` | APISIX servis kataloğu |

### send-otp / validate-otp (onboarding-business)
| Alan | Değer | Kanıt |
|---|---|---|
| Metot / path | `POST {base}/onboarding-business/otp/send-otp` · `POST .../otp/validate-otp` | `vnext-onboarding` → `onboarding/Tasks/http-task-send-otp.1.0.0.json:16-17`; APISIX route id `605729611426301053` / `…054` |
| Task tipi | `"6"` HTTP, `timeoutSeconds: 30`, `validateSsl: true` | aynı dosya |
| Request body | `{ Phone: { Number, Prefix, CountryCode }, CitizenshipNumber, OtpRetryAttempt, OtpDuration, Language, MessageTemplate, OtpType, InstanceId, SmsSender, AppType, ProcessName, ProcessIdentity, Tags }` | `otp-general-workflow.1.0.0.json` → `SendOtpMapping.csx` (base64 çözümlendi) |
| Telefon parçalama | `Prefix` = ilk 3 hane, `Number` = kalan | aynı mapping |
| **Başarı** | `data.sendOtpStatus == "SendOtpSuccess"` | `OtpSentSuccessRule.csx` |
| **SIM bloke** | `data.sendOtpStatus == "OtpBlacklisted"` | `OtpSentSimBlockRule.csx` |
| **FAST** | `OtpType = "Fast"` (varsayılan `"Otp"`) | `SendOtpMapping.csx` içi yorum: *"OtpType varsayılan 'Otp'; SIM bloke onayında parent 'Fast' gönderir"* |
| Ek yanıt alanları | `data.sendInfo.smsTraceId`, `.message`, `.httpStatusCode` | `SendOtpMapping.OutputHandler` |

**`CitizenshipNumber` doldurma kararı:** Bireysel'de `sub == actor == TCKN`. Kurumsal'da `sub` tüzel müşteri numarası olduğu için OTP servisine **`actor`** (işlem yapan gerçek kişinin TCKN'si) gönderilir — SMS gerçek kişiye gidiyor. → §9-S3'te teyit.

### authorization-code
**LOKALDE DOĞRULA.** Gerçek endpoint/şekil demirlenemedi. Demirlenen bağlam: `vnext-idm` (Amorphie, domain `idm`) içinde `idm/Workflows/token/token-1.0.0.json` OAuth2 token workflow'u var — `evaluated → prepare-authorization-code → waiting-redeem → redeem → active`; `client`, `user`, `consent`, `scope` fact key'leriyle `POST /api/v1/idm/workflows/token/instances/start` ile başlatılıyor. Bu akışta **MockLab'e karşı** varsayılan kontrat kullanılacak:
`POST {base}/auth/authorization-code` · body `{ client_id, client_secret, grant_type, scopeGroup, sub, actor, otpReference }` · yanıt `{ authorizationCode, expiresIn }`. → §9-S2'de gerçek kontrat teyidi.

---

## 6b. Transport / Çağrı Yolu — DEMİRLENMİŞ

### Bu repoda (lab, MockLab) — UYGULANACAK OLAN
| Konu | Değer | Kanıt |
|---|---|---|
| Task `url` placeholder | **`API_BASEURL`** | `core/Tasks/account-opening/create-bank-account.json:"url": "API_BASEURL/api/banking/accounts/create"` |
| Çözümleme | `httpTask.SetUrl(httpTask.Url.Replace("API_BASEURL", GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001")))` | `core/Workflows/data-integrity-lab/src/LabParStep1Mapping.csx:20-23` |
| Secret okuma | `await GetSecretAsync("vnext-secret", "workflow-secret", "<key>")` — mapping `async` olmalı | `core/Workflows/secret-cache-lab/src/SecretProbeMapping.csx:29-31,57` |
| Mock hedefi | `http://localhost:3001/api/{domain}/{resource}/{action}` | repo CLAUDE.md |
| Runtime | `http://localhost:4201` | repo CLAUDE.md |

> ⚠️ **`APISIX-BASE-URL` yazmayın.** Prod (`vnext-onboarding`) `APISIX-BASE-URL` + `GetConfigValue("APISIX-BASE-URL")` kullanıyor; bu repo `API_BASEURL` + `GetConfigValue("Example:ApiBaseUrl", …)` kullanıyor. Yanlış placeholder MockLab'e düşmez, sessizce boş base URL üretir.

### Prod karşılığı (taşınırsa — LOKALDE DOĞRULA)
| Konu | Değer | Kanıt |
|---|---|---|
| Gateway | `https://intprod-apisix.burgan.com.tr` | APISIX route `605729611426301053` |
| OTP route'ları | `/onboarding-business/otp/send-otp`, `/onboarding-business/otp/validate-otp` — POST | aynı |
| Route plugin'leri | `key-auth`, `consumer-restriction`, `proxy-rewrite` | `apisix_route_get` |
| API key header | **`X-APISIX-KEY`** | `SendOtpMapping.csx` (`httpTask.AddHeader("X-APISIX-KEY", apiKey)`) |
| Prod secret kaynağı | `GetSecret("vnext-onboarding-secret", "workflow-secret", "APISIX_KEY_VNEXT_CUSTOMERONBOARDING")` — **onboarding domain'ine ait**; `core` domain'i için karşılığı **LOKALDE DOĞRULA** | aynı mapping |
| Consumer | `vnext_customeronboarding` (consumer-restriction) — `core` domain'i için yeni consumer gerekir | servis kataloğu |
| simple-profile consumer'ları | 20+ iç tüketici (`vnext_customeronboarding` dâhil) | servis kataloğu |

**Gönderilecek header'lar (lab):** `Content-Type: application/json` + `X-APISIX-KEY: <secret veya boş>`. Lab'de secret store'da anahtar yoksa `GetSecretAsync` `null` dönebilir — mapping bunu **tolere etmeli** (`?? ""`), yoksa MockLab çağrısı bile patlar.

---

## 7. Test Senaryoları + Fixture + Doğrulama

### 7.1 MockLab seed — `etc/docker/config/seed/otp-auth-collection.json`

| Mock | Route | Kurallar |
|---|---|---|
| simple-profile | `GET api/core/customers/{customerId}/simple-profile` | default 200 + eşleşen telefon; rule `route.customerId == <MISMATCH_TCKN>` → 200 + **farklı** telefon; rule `route.customerId == <NOTFOUND_TCKN>` → **404** |
| send-otp | `POST api/core/onboarding-business/otp/send-otp` | default → `{"data":{"sendOtpStatus":"SendOtpSuccess","sendInfo":{"smsTraceId":"{{helpers.guid()}}","httpStatusCode":200}}}`; rule `body.OtpType == "Fast"` → `SendOtpSuccess` (priority 0); rule `body.CitizenshipNumber == <SIMBLOCK_TCKN>` → `{"data":{"sendOtpStatus":"OtpBlacklisted",…}}` (priority 1); rule `body.CitizenshipNumber == <HARDBLOCK_TCKN>` → her zaman `OtpBlacklisted` (Fast dâhil) |
| validate-otp | `POST api/core/onboarding-business/otp/validate-otp` | `body.OtpCode == "123456"` → valid; `== "000000"` → expired; else invalid |
| authorization-code | `POST api/core/auth/authorization-code` | default 200 `{"authorizationCode":"{{helpers.guid()}}","expiresIn":600}`; rule `body.client_id == <FAIL_CLIENT>` → 400 |

> MockLab aynı isimli koleksiyonu **atlar** → seed ekledikten sonra: `docker compose down -v && docker compose up -d mocklab`.

### 7.2 Fixture bulma sorgusu (gerçek veriyle çalışılacaksa)

Bu akış lab'de MockLab fixture'larıyla çalışır; gerçek TCKN gerekmez. Gerçek ortamda doğrulama gerekirse Asgard `CustomerDB` üzerinden (salt-okunur):

```sql
-- Bireysel: profili ve telefonu olan bir müşteri (çıktı KVKK maskeli gelir; gerçek değeri geliştirici lokalde çözer)
SELECT TOP 5 c.ExternalClientNo, c.CustomerType, p.PhoneNumber
FROM Customer c JOIN CustomerPhone p ON p.CustomerId = c.Id
WHERE c.CustomerType = 'Individual' AND p.PhoneNumber IS NOT NULL;

-- Kurumsal: tüzel müşteri + adına işlem yapan kullanıcı
SELECT TOP 5 ExternalClientNo, CustomerType FROM Customer WHERE CustomerType = 'Corporate';
```
> **LOKALDE DOĞRULA** — tablo/kolon adları (`Customer`, `CustomerPhone`, `ExternalClientNo`) demirlenmedi; `db_schema` ile teyit edilmeli.

### 7.3 Senaryolar — `core/Workflows/otp-auth/otp-auth.http`

| # | Senaryo | Girdi | Beklenen final | Beklenen `otpAuthResult` |
|---|---|---|---|---|
| 1 | Kurumsal happy path | scopeGroup=Kurumsal, kod `123456` | `otp-success` | `success` + `authorizationCode` dolu |
| 2 | Kurumsal SIM bloke → FAST | sub=`<SIMBLOCK_TCKN>` | `otp-success` | `success`; `simBlocked=true`, ikinci çağrıda `OtpType="Fast"` |
| 3 | Kurumsal çift bloke | sub=`<HARDBLOCK_TCKN>` | `sim-blocked` | `sim-blocked`; **`Otp` tipiyle 1, `Fast` ile 1 çağrı — toplam 2** |
| 4 | Bireysel happy path | scopeGroup=Bireysel, telefon eşleşen | `otp-success` | `success` |
| 5 | Bireysel kayıt yok | actor=`<NOTFOUND_TCKN>` | `profile-not-found` | `profile-not-found`; send-otp **0 çağrı** |
| 6 | Bireysel telefon uyuşmaz | actor=`<MISMATCH_TCKN>` | `phone-mismatch` | `phone-mismatch`; send-otp **0 çağrı** |
| 7 | Bireysel SIM bloke (FAST yok) | Bireysel + `<SIMBLOCK_TCKN>` | `sim-blocked` | `sim-blocked`; **`OtpType="Fast"` içeren çağrı 0** |
| 8 | Attempt tükenmesi | otpAttempt=2, iki kez yanlış kod | `attempt-exceeded` | `attempt-exceeded`; `attemptUsed == 2` |
| 9 | TTL → manuel resend → başarı | otpTtl=30, bekle, `resend-otp`, doğru kod | `otp-success` | `success`; `resendUsed == 1`, **`attemptUsed` sıfırlanmamış** |
| 9b | TTL + resend hakkı bitti | resendLimit=0, TTL dolar | `ttl-expired` | `ttl-expired` (subType 8) |
| 10 | Kurumsal profil yok → devam | Kurumsal + `<NOTFOUND_TCKN>` | `otp-success` | `success` (AC-04) |

### 7.4 Doğrulama komutları

```bash
# Instance durumu
GET  http://localhost:4201/api/v1/core/workflows/otp-auth/instances/{id}/functions/state

# MockLab'in gerçekten çağrıldığını ve OtpType'ı gör
docker compose logs -f mocklab | grep -i "otp"

# Component doğrulama (kullanıcı onayıyla — bkz. §10)
npm run validate
```

---

## 8. Uygulama Adımları

Her adımda **repodaki `CLAUDE.md` + ilgili `vnext-ai-toolkit` skill'inin kurallarına uy**; component envelope, auto-transition çiftleri, version bump, `{"attributes":{…}}` payload zarfı gibi yazım kuralları buraya kopyalanmadı.

| # | Adım | Skill | Doğrulama |
|---|---|---|---|
| 1 | 3 şemayı yaz (`otp-auth-master`, `otp-auth-start-payload`, `otp-code-payload`); `$id` URN'lerini elle kontrol et | `vnext-ai-toolkit:schema-design` | `npm run validate` |
| 2 | MockLab seed'ini yaz; `docker compose down -v && up -d mocklab`; 4 ucu `curl` ile dene | — | curl 200/404 doğru mu |
| 3 | 4 HTTP task + 1 script task'ı oluştur (`API_BASEURL` placeholder!) | `vnext-ai-toolkit:component-task` | `npm run validate` |
| 4 | `otp-auth-helpers` sys-mappings component'i + `OtpAuthHelpers.csx` | `vnext-ai-toolkit:component-mapping` | validate |
| 5 | Task mapping `.csx`'lerini yaz; her `OutputHandler`'da **gate ataması + try/catch → `"failed"`** | `vnext-ai-toolkit:workflow-scaffold` | mock'a karşı tek tek |
| 6 | State makinesinin **dallanmasız iskeletini** kur: initial → profile-lookup → otp-sending → otp-awaiting-code → otp-verifying → auth-code-issuing → otp-success | `vnext-ai-toolkit:workflow-scaffold` | Senaryo 1 uçtan uca |
| 7 | scopeGroup dallanmasını + `profile-gate-recheck` + `profile-not-found` / `phone-mismatch` final'lerini ekle | `workflow-scaffold` | Senaryo 5, 6, 10 |
| 8 | SIM bloke + FAST fallback + **latch**'i ekle (`otp-resending-fast`; `escalate-fast` transition'ı orada **tanımlanmaz**) | `workflow-scaffold` | Senaryo 2, 3, 7 |
| 9 | `attemptUsed` sayacı + `attempt-exceeded` final'i | `workflow-scaffold` | Senaryo 8 |
| 10 | `OtpTtlTimer.csx` (mutlak deadline) + `otp-ttl-gate` + `otp-awaiting-resend` + `AbandonTimer.csx` | `workflow-scaffold` | Senaryo 9, 9b |
| 11 | errorBoundary'leri yerleştir (§3.7) | `workflow-scaffold` | 5xx mock ile |
| 12 | 2 pseudo-ui view'ı yaz; Material Symbols isimlerini katalogdan doğrula | `vnext-ai-toolkit:view-design` | validate + renderer |
| 13 | `cancel` transition + `otp-cancelled` final'i | `workflow-scaffold` | manuel cancel |
| 14 | Harness parent (`otp-auth-harness.json`) + `HostToOtpAuthSubFlowMapping.csx` | `workflow-scaffold` | AC-24 |
| 15 | `otp-auth.http` — 10 senaryo | `vnext-ai-toolkit:integration-test` | hepsi yeşil |
| 16 | `npm run validate` (kullanıcı onayıyla) + kabul kriterleri geçişi | `vnext-ai-toolkit:validate-and-fix` | AC-01…AC-28 |

---

## 9. Açık Sorular / Riskler

### Karara bağlananlar (kullanıcı onayladı — plan bunlara göre yazıldı)
| Konu | Karar |
|---|---|
| OTP kod girişi | **UI'lı** — pseudo-ui form, subflow içinde render edilir |
| TTL davranışı | **Manuel "tekrar gönder"** — `otp-ttl-gate` + `otp-awaiting-resend`, `resendLimit` ile sınırlı |
| Kurumsal'da profil kontrolü | **Yok** — profil bilgi amaçlı; kayıt yoksa da telefon uyuşmasa da akış devam eder |
| Attempt sayacı | **Sıfırlanmaz** — instance ömrü boyunca toplam hak |

### Açık sorular
- **S1 — Final state'lerde view olacak mı?** Plan: hayır (sonuç ekranını parent render eder). UI'lı seçim yapıldığı için hata/sonuç ekranlarının da subflow'da olması istenebilir → +7 view.
- **S2 — Authorization-code servisinin gerçek sözleşmesi.** Endpoint/metot/alan adları ve başarı yanıtındaki alan adı **demirlenemedi** (§6a). `client_secret` gerçekten body'de mi gidecek, yoksa secret store'dan mı çekilecek? `scopeGroup` (Bireysel/Kurumsal) ile OAuth `scope` aynı şey mi, map'lenecek mi? → **LOKALDE DOĞRULA**; şimdilik MockLab kontratı kullanılıyor.
- **S3 — Kurumsal'da `CitizenshipNumber` ne gitmeli?** Plan `actor` (gerçek kişi TCKN'si) varsayıyor. `sub` (tüzel no) gitmeli mi?
- **S4 — `user_email` / `user_name` / `user_surname` ne işe yarıyor?** Hiçbir adımda kullanılmıyor. Pass-through mu, SMS şablonunda kişiselleştirme mi, auth-code servisine mi gidiyor?
- **S5 — `otpAuthResult` enum isimlerini kim sabitliyor?** Parent ekiplerle sözleşme; enum'a sonradan değer eklemek **breaking** (parent rule'ları etkiler).
- **S6 — SIM bloke dışındaki `sendOtpStatus` değerleri.** Geçersiz numara, servis limiti vb. hangi değerlerle geliyor? Plan hepsini `failed` → `otp-technical-failure`'a yönlendiriyor.
- **S7 — Bu akışın gerçek evi `core` mi `idm` mi?** Plan bu repoda (`core`, MockLab'e bağlı) inşayı öngörüyor — burada çalışıyorsunuz ve bu bir lab domain'i. Ancak `client_id`/`grant_type`/`client_secret`/`scopeGroup`/authorization-code **üretimdeki karşılığı `vnext-idm` (domain `idm`)**: orada `idm/Schemas/scope/scope-group-master-1.0.0.json`, `idm/Schemas/client/client-master-1.0.0.json`, `idm/Workflows/token/token-1.0.0.json`, `idm/Workflows/user/user-login.1.0.0.json` zaten var. Üretime taşınacaksa hedef domain `idm` olmalı ve `user-login` akışıyla hizalanmalı.
- **S8 — `vnext.config.json` `exports`.** Parent aynı domainde (`core`) kaldığı sürece değişiklik gerekmez. Başka domainden çağrılacaksa `exports.workflows`/`schemas`/`tasks`/`mappings` doldurulmalı.

### Riskler
| # | Risk | Etki | Önlem |
|---|---|---|---|
| R1 | **`triggerKind: 10` + ruled kardeş** birlikte kullanımının değerlendirme sırası repoda kanıtlanmamış | Kritik | Tasarımda karıştırılmıyor; bütünlük mapping'in total switch'iyle sağlanıyor. `triggerKind: 10` yalnız `otp-initializing`'de tek başına. |
| R2 | **Gate alanı yazılmazsa** hiçbir rule true dönmez, hata da yok → instance sessizce asılı kalır | Yüksek | Her `OutputHandler` `try/catch`; `catch` bloğunda gate'e `"failed"` yazılır. Kod review kriteri: **her çıkış yolunda** gate ataması. |
| R3 | **`ScriptResponse.Data` merge semantiği** — yalnız dönen alanlar yazılırsa gerisi silinebilir | Yüksek | Zorunlu desen: önce `context.Instance.Data`'yı `IDictionary<string,object>`'e kopyala, sonra üzerine yaz. Helper'da `MergeInstance(context)` olarak tek yerde. Repo kanıtı: `ParentSharedCommonTransitionMapping.csx`, `UpdateProgressCounterMapping.csx`. |
| R4 | **HTTP non-2xx mapping'e mi errorBoundary'ye mi düşüyor** runtime'a göre değişebilir — 404="kayıt yok" semantiği buna bağlı | Yüksek | İki yol da kurulu: mapping `context.Body.statusCode` okur **ve** `profile-lookup` errorBoundary'sinde 404/400 kuralı var; ikisi aynı hedefe gider, çift tetikleme olmaz (biri task döndüğünde, biri task patladığında). İlk koşuda MockLab'i 404 ile deneyip hangi yolun aktif olduğunu log'dan doğrula. |
| R5 | **`errorCodes` formatı** belirsiz — lock hataları `Task:Unknown:{taskKey}` biçiminde geliyor | Orta | 404 kuralına `"404","400","Task:404","Task:400"` varyantlarını birlikte yaz; priority 100'de `["*"]` catch-all bırak. |
| R6 | **Timer + manual yarış** — TTL timer'ı ile kullanıcının `verify-otp` çağrısı çakışır | Orta | Kaybeden taraf instance lock hatası alır (409 değil, `Task:Unknown:*`). Timer'a +2 sn grace; asıl doğrulama sunucu tarafında (`verifyGate == "expired"`), o dal da `otp-ttl-gate`'e gider → sonuç deterministik. |
| R7 | **Sonsuz retry döngüsü** `otp-awaiting-code ⇄ otp-verifying` | Yüksek | İki bağımsız kilit: `attemptUsed` sayacı (`exceeded` gate'i) + **mutlak** `otpExpiresAt` timer'ı. Sabit `FromDuration(otpTtl)` **kullanma**. |
| R8 | **Sonsuz resend döngüsü** `otp-awaiting-resend → otp-sending → … → otp-awaiting-resend` | Yüksek | `resendLimit` + `resendGate`; `otp-awaiting-resend`'de ayrıca `AbandonTimer` ile terk süresi. |
| R9 | **Sonsuz FAST döngüsü** | Orta | `otp-resending-fast`'te `escalate-fast` transition'ı **tanımlanmaz**; latch zaten `true`. |
| R10 | **Hassas veri persist'i** — `client_secret`, `otpCode` instance data'ya yazılırsa saklanır | Yüksek (KVKK) | `otpCode`'u `ValidateOtpMapping.OutputHandler`'da merge **etme** (transition payload root'a merge edildiği için alanı açıkça düşür). `client_secret` parent'a geri merge edilmez; kullanıldıktan sonra `null`'lanması değerlendirilmeli. |
| R11 | **Versiyonlama** — subflow parent'ta `version: "1.0.0"` ile pinli | Orta | `otpAuthResult` enum'una değer eklemek **breaking** → Major. Yeni state/rule → Minor. |
| R12 | **GERÇEKLEŞTİ** — repodaki hiçbir seed route parametresi kullanmıyor | Düşük | Azaltım uygulandı: route `api/core/customers/simple-profile`, koşul `query.customerId`, URL `GetSimpleProfileMapping.InputHandler`'da `?customerId=` ile kuruluyor. |
| R13 | ~~`attributes.scripts` yayılımı örneklenmemiş~~ — **KAPANDI** | — | `account-opening-workflow.json` workflow seviyesi `attributes.scripts.helpers` kullanıyor; aynısı uygulandı. |

---

## 10. Ortam Gotcha'ları / Bilinen Tuzaklar

| Tuzak | Ayrıntı |
|---|---|
| **Yanlış base URL placeholder** | Bu repo `API_BASEURL` + `GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001")` kullanır. Prod `vnext-onboarding` `APISIX-BASE-URL` + `GetConfigValue("APISIX-BASE-URL")` kullanır. Karıştırılırsa URL sessizce boş base ile kurulur, MockLab'e hiç gitmez. |
| **Secret store adı** | Lab: `GetSecretAsync("vnext-secret", "workflow-secret", "<key>")` (`SecretProbeMapping.csx`). Prod onboarding: `GetSecret("vnext-onboarding-secret", "workflow-secret", "APISIX_KEY_VNEXT_CUSTOMERONBOARDING")`. `core` domain'i için prod karşılığı **demirlenmedi**. Lab'de anahtar yoksa `null` döner → `?? ""` ile tolere et. |
| **Geçerli header değeri** | Prod APISIX route'ları `key-auth` + `consumer-restriction` taşıyor; header **`X-APISIX-KEY`**, consumer `vnext_customeronboarding`. `core` domain'i için yeni consumer tanımlanmadan prod'a çağrı **401** alır. Lab'de bu geçerli değil. |
| **MockLab seed skip-if-exists** | MockLab, DB'de aynı isimli koleksiyon varsa seed'i **atlar**. Her seed değişikliğinden sonra `docker compose down -v && docker compose up -d mocklab`. |
| **`npm run validate` URN denetlemez** | Şema `$id`'lerinin `urn:vnext:res:schema:core:{key}` olduğunu, transition `command` URN'lerini ve `domain` segmentini **elle** kontrol et. |
| **`.csx` base64** | `.csx` dosyalarını elle base64'lemeyin — vNext VS Code eklentisi `mapping.code` alanını kaydederken üretir. Prod onboarding reposunda karşılığı `wf csx --all`. |
| **Auto/timer transition'da view yasak** | `triggerType` 1 ve 2 transition'larda `view: null` zorunlu. Form `otp-awaiting-code` / `otp-awaiting-resend` state'lerinin `state.view`'ında. |
| **Instance lock hata kodu** | Lock contention `409` değil, `Task:Unknown:{taskKey}` biçiminde gelir; errorBoundary `errorCodes` buna göre yazılmalı. |
| **Payload zarfı** | ~~`{"attributes": {…}}`~~ **YANLIŞ.** Bu repoda start ve transition payload'ları **çıplak** gönderilir (`{ "otpCode": "123456" }`); `attributes` yalnız *yanıt* gövdesinde görülür. Transition'ı senkron koşturmak için `?sync=true`. |
| **Master şemada `additionalProperties`** | Master/workflow şemalarında `additionalProperties: true` **korunur**; `false` yapılmaz. Start payload şemasında `false` bilinçli sıkılaştırmadır. |
| **`x-lov` yerine static** | Bu akışta LOV/lookup yok; view'lar static layout + hardcoded URN command kullanır. |
| **Material Symbols** | `Icon.name` / `Button.icon` → lowercase `snake_case` (`sms`, `schedule`, `lock`, `refresh`). kebab-case veya Font Awesome geçersiz. |

---

## 11. Uygulama Sapmaları (execute sırasında gerçeğe göre düzeltilenler)

| # | Plan ne diyordu | Gerçek | Uygulanan |
|---|---|---|---|
| D1 | Üç şema da `type: "workflow"` | Şema enum'unda `transition` yok; master'da `required` yasak | Master `workflow` (required'sız, `additionalProperties: true`); iki payload `schema` (required'lı) |
| D2 | start-payload `additionalProperties: false` | Repo konvansiyonu belirtmiyor; SubFlow fazladan alan taşıyabilir | Kaldırıldı |
| D3 | simple-profile path parametresi | MockLab route parametresi desteklemiyor (R12) | `?customerId=` query'sine çevrildi |
| D4 | `.csx` gömme yöntemi belirsiz (CLAUDE.md elle base64'ü yasaklıyor) | Repoda 5 workflow `build-*.py` üreticisiyle yazılıyor | `core/Workflows/otp-auth/build-otp-auth.py` üreticisi; `.csx` diskte tek doğruluk, `encoding: "B64"` üretici tarafından |
| D5 | errorBoundary `priority` 10/100 | Şemada `priority` **minimum 1** | Değişiklik gerekmedi (10/100 geçerli) |
| D6 | Payload zarfı `{"attributes":…}` | Repoda çıplak payload | `.http` çıplak payload + `?sync=true` |
| D7 | R7 riski: `verify-otp` retry döngüsünde task atlanabilir mi | `secret-cache-lab` self-loop'u aynı transition'ı defalarca geçiyor ve task her turda koşuyor | Risk kapandı; ek önlem gerekmedi |
| D8 | `otp-code-expired` → `otp-ttl-gate` | Gate task'ı yalnız timer transition'ındaydı; bu yoldan gelince `resendGate` boş kalırdı | `EvaluateResendGateMapping` bu transition'a da eklendi |

### Doğrulama durumu
- Pinned `vnext-schema@0.0.52` şemalarına karşı **ajv ile doğrulandı**: 2 workflow, 3 şema, 5 task, 1 mapping, 2 view — hepsi geçerli.
- Yapısal öz-denetim temiz: tüm transition target'ları var, auto/timer transition'larda `view: null`, errorBoundary referansları kendi state'inde, final state'lerde transition yok, gömülü base64 ile diskteki `.csx` **birebir aynı** (drift yok).
- Beş gate'in (`profileGate`, `sendGate`, `verifyGate`, `resendGate`, `authGate`) **tüm enum değerleri** rule ile tüketiliyor — boşta dal yok.
- `npm run validate` ve runtime senaryoları **henüz koşulmadı** (kullanıcı onayı bekliyor).
