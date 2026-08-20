# role-matrix-lab

## Neyi denetliyor

vNext'in **yetkilendirme yüzeylerinin birbiriyle tutarlı olduğunu** ve her birinin kendi kuralını
doğru uyguladığını denetler: `queryRoles`, `transition.roles`, `availableIn` rol daraltması,
`function.roles`, master şemadaki `x-roles` alan budaması ve bunların hepsini raporlayan
`authorize` function'ı.

Tek cümlelik iddia: **bir client'a gösterilen ile `authorize`'ın söylediği ve okuma
function'larının yaptığı asla birbirinden ayrışmaz.**

Bu bir *davranış* (pipeline) senaryosu değildir — akış kasıtlı olarak sıkıcıdır. Her state, her
transition ve her alan, **farklı bir grant kombinasyonunu görünür kılmak** için seçilmiştir.

## Neden var

İki geliştirme aynı anda bu fixture'ı doğurdu (2026-08-19, `feature/caller-role-provider`):

1. **Provider bazlı caller-role çözümü.** Rol kaynağı `ICallerRoleResolver` arkasına alındı;
   `default` (mevcut `ICurrentUser.Roles` + `role` header) yanına `morph-idm` provider'ı eklendi.
   Rol setinin **nereden geldiği** değişirken, grant motorunun davranışının **hiç değişmemesi**
   gerekiyor. Bu suite o değişmezliğin ölçüsüdür: provider'ı `morph-idm`'e alıp aynı testleri
   koşturmak, yalnızca kaynağın değiştiğini kanıtlar.

2. **Custom function'lardan rol denetiminin kaldırılması.** `FunctionAccessPolicy` artık yalnız
   `scope` denetler; `function.roles` sadece `authorize` tarafından okunur. Bu ayrım her iki yönde
   de yanlışlıkla "bug" sanılabilir, bu yüzden iki yarısı da
   [`CustomFunctionAuthorizationTests`](CustomFunctionAuthorizationTests.cs) içinde **yan yana**
   pinlenmiştir.

Ayrıca daha önce gerçekten ayrışmış olan iki yüzey burada kalıcı olarak bağlanıyor: `authorize`
bir zamanlar instance'ın current state'ini yok sayıyordu, ve bir yüzey request context'siz
değerlendirme kurduğunda dynamic grant'ler bir tarafta eşleşip diğerinde eşleşemiyordu.

## Akış şeması

```
                    start-role-matrix
                           │  (SeedCaseMapping → caseRef, decisionNote, auditTrail)
                           ▼
                      ┌─────────┐
                      │ intake  │  queryRoles: YOK → root'a düşer
                      └────┬────┘  root: maker ALLOW, approver ALLOW, auditor ALLOW  (viewer ⇒ 403)
                           │  submit-for-review   [maker ALLOW, approver ALLOW, viewer DENY]
                           ▼
                      ┌─────────┐
                      │ review  │  queryRoles: approver ALLOW, auditor ALLOW, maker DENY
                      └────┬────┘  ← state seti ROOT'U EZER: maker başlatabilir ama okuyamaz
          ┌────────────────┼────────────────┬──────────────────┐
          │ approve        │ reject         │ escalate         │ open-review-note
          │ [approver      │ [auditor DENY] │ [$InstanceStarter│ [grant YOK]
          │  ALLOW]        │  = blacklist   │  ALLOW]          │  → herkese açık
          │  = allowlist   │                │  = predefined    │
          ▼                ▼                ▼                  ▼ ($self)
     ┌──────────┐    ┌──────────┐    ┌────────────┐
     │ approved │    │ rejected │    │ escalated  │ queryRoles: auditor ALLOW (tek)
     └──────────┘    └──────────┘    └─────┬──────┘ → approver bile 403
                                           │ resolve-escalation [auditor ALLOW]
                                           ▼
                                      ┌──────────┐
                                      │ approved │
                                      └──────────┘

shared: record-note ($self)
        roles              = maker ALLOW, approver ALLOW
        availableIn        = ["intake", {state:"review", roles:[approver ALLOW]}]
        ⇒ intake'te maker VE approver · review'de YALNIZ approver          ← AND daraltması

well-known: cancel-role-matrix   [maker, approver]   availableIn: intake, review
            update-role-matrix-data [maker]          target: $self
            exit-role-matrix     [auditor]           availableIn: {review, [auditor]}
```

### Kritik adımlar

| Adım | Neden kritik |
|---|---|
| `intake → review` | State `queryRoles`'un root'u **ezdiğini** (birleşmediğini) kanıtlayan tek geçiş. Aynı caller, aynı instance, cevap 200'den 403'e döner. |
| `record-note` / `review` | `availableIn` rol daraltmasının **AND** olduğunu gösterir. OR ya da per-state grant'leri yok sayan bir implementasyonda transition her iki state'te de görünür kalır. |
| `reject` | Deny-only set = **blacklist**. Allowlist gibi yorumlanırsa transition herkes için kaybolur — sessiz ve fark edilmesi zor bir regresyon. |
| `escalate` | `$InstanceStarter` rol string'ine değil **caller kimliğine** bağlıdır. Provider değişiminden sonra bu test kırmızıya dönerse, bozulan kimlik hattıdır, rol hattı değil. |
| `decisionNote` (x-roles) | Alan seviyesinde **DENY kazanır**: maker+approver caller instance'ı okur ama alanı kaybeder. Instance seviyesindeki "bir ALLOW yeter" kuralının tam zıddı. |

## Nasıl çalıştırılır

Ön koşullar: altyapı ayakta (`cd etc/docker && ./run-docker.sh` — vNext çalışma alanında),
migration gerekiyorsa DbMigrator bir kez, ve 4 app `--launch-profile http` ile.
MockLab **gerekmez** — bu senaryoda HTTP task yok, hepsi script task.

```bash
dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~RoleMatrixLab"
```

Lokal runtime'a bağlamak için (geliştirme sırasında doğru yol — container image eski kodu taşır)
`tests/Core.IntegrationTests/test.runsettings` içindeki satırı yorumdan çıkar:

```xml
<VNEXT_BASE_URL>http://localhost:4201</VNEXT_BASE_URL>
```

Bileşenler değiştiğinde workflow ve function JSON'ları **üretilir**, elle düzenlenmez:

```bash
python3 core/Workflows/role-matrix-lab/build-role-matrix-lab.py && npm run validate
```

## Beklenen sonuç / başarı kriteri

59 test yeşil. Anlamlı olan üçü:

- `Authorize_AgreesWithTheStateFunctionListing_ForEveryTransitionAndRole` — beklenen cevabı
  **kodlamaz**, yalnızca iki yüzeyin ayrışamayacağını doğrular. Suite'in en değerli assertion'ı.
- `InvocationSucceeds_WhileAuthorizeDenies_ForTheSameCaller` — aynı caller, aynı function:
  çalıştırma 200, `authorize` 403. İkisi de doğru.
- `ACallerHoldingBothAnAllowedAndADeniedRole_LosesTheField` — DENY'ın alan seviyesinde kazandığı.

### Bilinen kısıtlar

- **`transition.roles` çalıştırma anında zorlanmaz** (tasarım kararı). Bu yüzden hiçbir test
  "rolü olmayan caller transition'ı çalıştıramaz" demez — sadece **gösterilmediğini** ve
  `authorize`'ın **reddettiğini** doğrular.
- **Python yük testi yok, bilinçli.** Bu senaryo eşzamanlılık değil doğruluk ölçer; yetkilendirme
  kararları instance state'ine ve caller kimliğine bağlıdır, yüke değil. Yük altında ölçülecek
  bir şey çıkarsa (ör. morph-idm provider'ının request başına tek çağrı garantisi) `api-tests/`
  altına o zaman eklenir.
- **morph-idm provider'ı bu suite ile henüz koşulmadı.** Provider `default` iken yazıldı;
  Aether'a `ICurrentUser.Position` eklendikten sonra `CallerRoleProvider:Provider = "morph-idm"`
  ile tekrar koşulması gerekir — asıl doğrulama odur.
