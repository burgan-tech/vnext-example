# account-opening

Template ile gelen ilk referans akış: dört ürün dalı olan wizard biçimli bir hesap açılışı, ortak bir
özet adımı ve iki otomatik kapı (`policy-validation`, `account-creation`). İki kapı da başvuruyu
`account-type-selection`'a **geri** gönderebilir; bu yüzden "akış ilerledi" ile "akış başarılı oldu"
aynı şey değildir, terminal assertion'lar başarı state'inin adını açıkça verir. Suite ayrıca
`authorize?queryRoles=true` ile state function'ın rol karşısındaki ayrışmasını pinler.

## Neyi denetliyor

- **Wizard state tipi** (`stateType: 5`) ve tek state'ten çıkan dört manuel transition'ın her birinin
  kendi bilgi adımına inmesi (`select-*` → `*-info`).
- **Transition şema doğrulaması admission'dan önce**: geçersiz `branchCode` 400 döner, instance
  `demand-deposit-info` / `A`'da kalır — Busy'ye hiç çekilmez, salınacak bir şey yoktur.
- **Auto transition zinciri**: `approve-account-opening` sonrası `policy-validation` ve
  `account-creation` onEntry HTTP task'larının sonucu kural ile değerlendirilir; instance kendi
  kendine `account-opening-success` / `C`'ye yürür.
- **Okuma yetkisi gateway'dedir**: rolsüz caller'a `authorize?queryRoles=true` 403 +
  `"allowed":false` döner, rollü caller'a 200; aynı rolsüz caller'a state function **200** döner.
- **Well-known `cancel` ve `exit`**: ayrı anahtarlar (`cancel-account-opening`, `exit-account-opening`),
  aynı hedef (`cancelled`), ikisi de terminal status üretir.

## Akış

`account-opening` v1.0.2 (`core/Workflows/account-opening/account-opening-workflow.json`), workflow
`queryRoles`: `morph-core.maker`, `timeout` PT15M → `timeouted`, helper `rsa-crypto`.

```
start: initiate-account-opening (schema initiate-account-opening, onExecute script-task/UserSessionMapping)
        │
        ▼
account-type-selection (Initial)  onEntry: 1 notify-state (type 10) · 2 set-or-get-cache
  ├─ select-demand-deposit    (manual) ─▶ demand-deposit-info     (Wizard)
  ├─ select-time-deposit      (manual) ─▶ time-deposit-info       (Wizard)
  ├─ select-investment-account(manual) ─▶ investment-account-info (Wizard)
  └─ select-savings-account   (manual) ─▶ savings-account-info    (Wizard)
                                   │ submit-*-info (manual, ürün şeması)
                                   ▼
                     account-summary (Wizard)
                                   │ approve-account-opening (manual, schema account-confirmation)
                                   ▼
policy-validation  onEntry: validate-account-policies (HTTP)
  ├─ policies-passed (auto) ─▶ account-creation  onEntry: create-bank-account (HTTP)
  │                              ├─ account-created-successfully (auto) ─▶ account-opening-success (Finish/Success)
  │                              └─ account-creation-failed      (auto) ─▶ account-type-selection
  └─ policies-failed (auto) ─▶ account-type-selection

cancel-account-opening / exit-account-opening (manual) ─▶ cancelled (Finish/Terminated)
```

## Testler

| Test | Kanıtladığı | Not |
|---|---|---|
| `HappyPath_OpensADemandDepositAccount` | Vadesiz dal uçtan uca `account-opening-success` / `C` | İki auto kapı da MockLab cevabına bağlı; 90 sn bütçe |
| `SelectingAProduct_RoutesToThatProductsWizardStep` | `select-time-deposit` kendi `time-deposit-info` adımına iner | Dört dalın temsilcisi |
| `SubmitInfo_RejectsAnInvalidBranchCode` | Şema reddi 400, instance yerinde ve `A` | `branchCode` dört hane olmalı |
| `WithoutACallerRole_AuthorizeRefusesTheReadButTheStateFunctionAnswers` | Rolsüz: `authorize?queryRoles=true` 403; rollü: 200; rolsüz state function 200 | 2026-10-05'te eski "state function 403 döner" testinin yerine geldi |
| `Cancel_MovesTheApplicationToCancelled` | `cancel-account-opening` → `cancelled`, terminal status | |
| `Exit_AlsoLandsOnCancelled` | `exit-account-opening` aynı hedefe iner | `cancel` ve `exit` ayrı well-known anahtarlar |

## Bağımlılıklar ve ön koşullar

| Bağımlılık | Durum |
|---|---|
| Runtime | `core`, `VNEXT_BASE_URL` (`test.runsettings`: `http://localhost:4201`), lokal build |
| System package | **Gerekir** — start'ın ve `extension-user-session`'ın task'ı `sys-tasks/script-task`, `@burgan-tech/vnext-core-runtime`'tan gelir (domain'in init container'ı) |
| MockLab | **Gerekir** — `account-opening-collection.json`: `POST api/banking/policies/validate-account-opening`, `POST api/banking/accounts/create` (`API_BASEURL` → `Example:ApiBaseUrl`, varsayılan `http://localhost:3001`) |
| Dapr | Akışa özgü bileşen yok (pubsub/state store/scheduler runtime'ın standart sidecar'ı); `set-or-get-cache` sistem paketindeki StateStore task'ı; standart `vnext-state` state store'una yazar |
| Caller rolleri / header'lar | `role` + `x-roles` = `morph-core.editor,morph-core.maker`; `x-device-id` (mapping'ler okur). Test tabanı `user_reference`, `x-device-id`, `x-token-id`, `x-request-id`'yi her istekte gönderir |
| Cross-domain | Gerekmez |
| morph-idm provider | Gerekmez (default provider; roller header'dan) |

## Nasıl çalıştırılır

```bash
dotnet test tests/Core.IntegrationTests \
  --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~AccountOpening"
```

Python scripti yok; elle sürmek için `api-tests/account-opening/account-opening.http`.

## Dikkat noktaları / bilinen istisnalar

- **Okuma kapısı runtime'da yok.** Runtime'ın `queryRoles` okuma kapısı 2026-09-23'te kaldırıldı:
  okuma fonksiyonları her caller'a cevap verir, kararı gateway `authorize?queryRoles=true` ile sorar.
  State function'dan 403 bekleyen bir test yazmayın (bkz. `RoleMatrixLab/QueryRoleGateTests`).
- **Transition `roles`'u execution'da uygulanmıyor.** `select-demand-deposit` yalnız
  `idm.full-authorizeds`'a izin veriyor, test ise `morph-core.*` rolleriyle onu çalıştırıyor ve yeşil;
  karar `authorize?transitionKey=`'in işi. Bu runtime'ın **belgelenmiş tasarım kuralıdır**
  (vnext `.claude/rules/vnext-workflow-developer.md`: "`transition.roles` is not enforced at execution, by
  design") — kusur değil, "düzeltilmemeli".
- **onEntry task'ları bugün temiz koşuyor (ölçüldü 2026-10-05).** `account-type-selection`'ın iki
  onEntry task'ı (`notify-state`, `set-or-get-cache`) artık sistem paketinden (`sys-tasks`) çözülüyor ve
  son koşudaki 18 çalıştırmanın hepsinde `isSuccess: true` döndü (`account_opening."InstanceTasks"`;
  `set-or-get-cache` → StateStore `vnext-state`'e `custom:integration-test-device` yazdı). Eski
  "tanımsız task / fault" kaydı sistem paketi yüklü olmayan ortama aittir — taze DB'de paketi init
  container'dan yüklemeyi unutmayın. Error boundary tanımlı olmadığı ve `AssertNotFaultedAsync` yalnız
  Faulted'ı yakaladığı için, bir onEntry task'ının sessizce başarısız olduğundan şüphelenirseniz
  `InstanceTasks.Response.isSuccess`'e bakın.
- **v1.0.2 öncesi kırmızıydı.** `UserSessionMapping` eksik header'da indexer ile patlıyor,
  `userSession` yazılmıyor ve `create-bank-account` boş gövdeyle 400 alıp `account-creation-failed`'a
  dönüyordu. Mapping'ler artık null dönen yardımcılarla okuyor.
- **`x-device-id`'siz start testi bilinçli olarak yok** — o testin "faulted" iddiası başlıksız
  start'ı normal start'tan ayırt edemiyordu.
- Publish versiyon-değişmezdir: fixture değişikliği patch bump ister (`1.0.2` → `1.0.3`).
