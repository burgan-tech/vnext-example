# subflow-override-lab

## Ne kontrol ediyor

Bir parent'ın `state.subFlow.overrides` ile tükettiği SubFlow çocuğuna verdiği iki yeni override'ın
uçtan uca davranışını:

| Override | Beklenen |
|---|---|
| `overrides.states.<childState>.interaction.longPoll.fallbackTimeoutSeconds` | Pencere parent'ınki olur; state gövdesi, arm edilen job ve ack aynı değeri görür |
| `overrides.states.<childState>.interaction.longPoll.roles` | Liste **bütün olarak** değişir (merge yok); yazılmayan alan çocuğunkini korur |
| long-poll'u OLMAYAN bir state'e long-poll override'ı | Yok sayılır, long-poll **eklenmez** (EventId 20305) |
| `overrides.states.<childState>.views.<viewKey>` | Çocuğun kendi kuralının seçtiği view'un referansı değişir |
| `overrides.transitions.<childTransition>.views.<viewKey>` | Yalnız o transition'ın view'u; state override'ı transition view'una düşmez |
| Çözülemeyen view override'ı | Çocuğun kendi view'u döner (EventId 20101) |
| Eski `overrides.views` haritası | Bugünkü gibi: parent-taraflı, yalnız parent poll edildiğinde |

Override'lar çocuğa başlangıçta damgalanır (`subflow.state_role_overrides` /
`subflow.transition_role_overrides`) ve **çocuk tarafında**, çocuğun kendi `CurrentState`'i üzerinden
çözülür: çocuk doğrudan adreslendiğinde de geçerlidir ve **tek hop**'tur (P'nin override'ı torun G'ye
ulaşmaz). Her iddia hem parent üzerinden hem çocuk doğrudan adreslenerek ölçülür.

## Neden var

Runtime değişikliği `feature/subflow-override-interaction-views` (spec
`2026-09-23-subflow-override-interaction-and-views`). Long-poll penceresi çocuğun pipeline'ında
(`HandleLongPollTerminationStep`, order 75) tüketilir ve orada parent'ın tanımı kapsamda değildir; bu
değişiklikten önce parent'ın bunu ayarlamasının yolu yoktu. Düz `viewKey → Reference` haritası da bir
view'u her yerde birden değiştiriyordu.

## Neden ayrı bir akış

`authorization-chain-lab`'in leaf'i zaten bir long-poll taşıyor ve `AckAndParentRetainedTests` onun
`roles = [chain.admin]` olduğunu varsayıyor; pencereyi ya da rolleri override etmek o suite'in ack
sözleşmesini değiştirirdi.

## Akışlar

Üretici: `core/Workflows/subflow-override-lab/build-subflow-override-lab.py` (view'lar
`core/Views/subflow-override-lab/`).

```
CHILD (S)        child-initial --auto--> lp-wait
                 lp-wait: longPoll { terminate, fallback 600, roles [ovr.child-ack] }, view child-lp-view
                          transitions: confirm (view child-confirm-view), note (view child-lp-view)
PLAIN-CHILD (S)  plain-initial --auto--> plain-wait (long-poll YOK)

PARENT         -> CHILD   lp-wait: fallback 120 (yalnız süre) + views{child-lp-view->parent-lp-view}
                          transitions.confirm.views{child-confirm-view->parent-confirm-view}
PARENT-SHORT   -> CHILD   lp-wait: fallback 5 (pencerenin davranışsal kanıtı)
PARENT-ROLES   -> CHILD   lp-wait: roles [ovr.parent-ack] (yalnız roller) + views{child-lp-view->YOK olan view}
PARENT-NOLP    -> PLAIN   plain-wait: fallback 90 (yok sayılmalı)
PARENT-LEGACY  -> CHILD   overrides.views{child-lp-view->legacy-view} (eski harita)
TOP -> MID (S, override'sız) -> CHILD
                          TOP'un override'ı `lp-wait`'i adlandırır — MID'de yok, torunda var
```

**Rol başlıkları çocuğa iletilir.** `SubflowStarter` çocuğa yalnız input mapping'in döndürdüğü
header'ları gönderir, ve long-poll arm'ı (order 75) yalnız **tetikleyen çağıran** etkin long-poll
rollerini karşılıyorsa durur. Lab'ın SubFlow mapping'i bu yüzden `x-roles`/`role`'ü iletir; onsuz
roles-kollu bir long-poll SubFlow çocuğunda hiç durmaz (authorization-chain-lab leaf'inin 575 Active /
0 token satırı bunun ölçümü).

## Testler

`LongPollOverrideTests` (6) + `ViewOverrideTests` (6):

- `ADurationOverrideReplacesTheWindowAndKeepsTheChildsRolesAndTerminate`
- `TheArmedFallbackJobFiresOnTheParentsWindow` — 5 s override, 600 s çocuk; çocuk saniyeler içinde devam etmeli
- `ARolesOverrideReplacesTheChildsGrantsOnEverySurface` — state function + `authorize?ack=true`, parent ve çocuk
- `TheArmHonoursTheRolesOverride` — yalnız çocuğun kendi rolüyle başlatılan çocuk durmamalı
- `AnOverrideOnAStateWithoutALongPollIsIgnored`
- `AnAncestorsOverrideDoesNotReachTheGrandchild` (long-poll) / `AnAncestorsViewOverrideDoesNotReachTheGrandchild`
- `AStateScopedOverrideAppliesViaTheParentAndOnTheDirectlyAddressedChild`
- `ATransitionScopedOverrideAppliesToThatTransitionsView`
- `AStateOverrideDoesNotLeakIntoATransitionView`
- `AnUnresolvableOverrideFallsBackToTheChildsView`
- `TheLegacyViewMapStillAppliesParentSideOnly`

## Nasıl koşulur

Ön koşul: infra + runtime lokal build ayakta (`etc/docker/run-docker.sh up core`), `VNEXT_BASE_URL`
`test.runsettings` içinde. MockLab **gerekmez**.

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~SubflowOverrideLab" -v minimal
```

Loglar (20300/20305/20101) ve persist edilen satırlar test dışında kontrol edilir — bkz. koşu kaydı.

## Bilinen açıklar

- **Şema:** kurulu `@burgan-tech/vnext-schema` (0.0.52) `subFlowStateOverride.interaction`/`views` ve
  `subFlowTransitionOverride.views` alanlarını tanımıyor; beş parent akışı `npm run validate`'ten
  `must NOT have additional property "interaction"` / `"views"` ile düşer. SDK JSON'u doğrudan
  `/definitions/publish`'e gönderdiği için test engeli değil. Şema değişikliği yerel bir vnext-schema
  dalında, yayınlanmadı.
- **Pencere API'den okunamıyor:** runtime `5f60ab4b` ile long-poll ack `InstanceJobs` satırı
  (`JobType 4`, `…longpoll-ack`) artık `ExecuteAt` taşıyor — Dapr job'unun kurulduğu anın aynısı, etkin
  pencereden (child'ın kendi değeri ya da parent override'ı). Ancak hiçbir okuma yüzeyi bu satırı
  göstermiyor ve suite'in veritabanı erişimi yok; test bu yüzden davranışsal ölçümü korur (5 s testi:
  job'un gerçekten ateşlendiğini kanıtlar, yalnız planlandığını değil). Önceki runtime'da (`e7cb023c`)
  `ExecuteAt` NULL'dı ve pencere işlenen satırın `ModifiedAt − CreatedAt` farkından ölçülmüştü
  (120.2 s / 5.1 s). Postgres doğrulaması (2026-09-23, `subflow_override_lab_child.InstanceJobs`,
  `JobType=4`): düzeltme öncesi 22 satırın 0'ında `ExecuteAt` dolu, düzeltme sonrası 10/10 dolu;
  `ExecuteAt − CreatedAt` = 600.0 / 120.0 / 5.0 s (child'ın kendi penceresi / parent override / kısa
  override).

## Koşu kaydı

**2026-09-23 — 12/12** (iki koşu), runtime lokal build `feature/subflow-override-interaction-views`
@ `e7cb023c`, `VNEXT_BASE_URL=http://localhost:4201`. Komşu suite'ler aynı runtime'da:
AuthorizationChainLab 44/44 (+7 morph-idm skip), SubflowOrchestration 22/22, TimeoutLab 3/3.

Test dışı kanıt (host log + postgres; OpenObserve MCP bu oturumda bağlı değildi):

- 20300 `Long-poll termination armed … fallback in 120s` ×4 (PARENT), `in 5s` ×1 (PARENT-SHORT),
  `in 600s` ×5 (PARENT-ROLES ack rolüyle ×2, LEGACY, TOP torunları ×2).
- İşlenen job satırları: PARENT çocuğu `CreatedAt 19:45:36.709 → ModifiedAt 19:47:36.874` (120.2 s);
  PARENT-SHORT çocuğu `19:49:59.512 → 19:50:04.591` (5.1 s). Çocuğun kendi değeri 600 s.
- PARENT-ROLES çocuğu yalnız `ovr.child-ack` ile başlatıldığında: job satırı yok, `Status A`, token yok.
- 20305 `Long-poll override ignored … at state plain-wait` ×1; PLAIN-CHILD'da `InstanceJobs` 0 satır.
- 20101 `SubFlow view override unresolved … child-lp-view -> subflow-override-lab-missing-view` ×2.
- MID'in `subflow.state_role_overrides` damgası TOP'un `lp-wait` override'ını taşıyor; torunun
  ExtraProperties'inde damga **yok**.
