# SubflowCarriedHeadersLab — integration test

Bir SubFlow child'ı settle olduğunda (finish/exit, cancel, istek hop'unda fault) onu settle eden
**isteğin header'ları** terminal olaya (`CarriedHeaders`) yazılır ve parent'a taşınır. Domain `core`,
yalnız script task'lar. Runtime: vnext `feature/subflow-terminal-carried-headers`; sözleşme
`docs/runtime/event-publish-modes.md` § "Carried headers on sub-item terminal events" ve
`docs/architecture/subflow-execution.md` § "Carried Headers from the Child".

| Test | Neyi denetliyor |
|---|---|
| `FinisherHeaders_ReachParentOutputMapping` | Child A `finish` PATCH'i `sub: <guid>`, `x-carried-custom: v1`, `authorization: Bearer canary-<guid>`, `x-subflow-depth: 9` ile gelir. Parent'ın A output mapping'i `context.Headers`'tan `carried.sub == <guid>`, `carried.custom == v1` görür; `authorization` (deny floor), `x-token-id` (deny floor, `token`) ve `x-subflow-depth` (framework hariç tutması) **yoktur**; canary parent verisinin hiçbir yerinde geçmez |
| `ApplyToResume_Off_NextChildSeesNoHeaders` | Varsayılan runtime (`ApplyToResume=false`): parent'ın resume hop'unda B'nin input mapping'i `sub` görmez → `bSeenSub == null`; resume kuralı `resume-no-sub`'a gider (`resumeRoute == "no-sub"`). `VNEXT_IT_CARRIED_HEADERS_APPLY_TO_RESUME=1` ise skip |
| `ApplyToResume_On_NextChildSeesFinisherSub` | Yalnız `VNEXT_IT_CARRIED_HEADERS_APPLY_TO_RESUME=1`: `bSeenSub == <guid>`, `resumeRoute == "saw-sub"`. Aksi hâlde skip |
| `TimerFinishedChild_CarriesNothing` | Child A zamanlanmış transition'la (2 sn) biter; timer hop'unda istek yok → `carried.count == 0`, `carried.sub == null` (output mapping yine koşar, set boş) |
| `CanceledChild_CarriesHeaders` | Child A `cancel-sfch-child` ile iptal edilir. `instance.sub.canceled` parent tarafında (`SubflowCancellationService`) **output mapping koşturmaz** — bu yüzden `carried` hiç yazılmaz; parent resume olur, B'yi koşar ve tamamlanır. Set yalnız resume'a uygulanır: kapalı modda `resumeRoute == "no-sub"`, açık modda `saw-sub` + `bSeenSub == <guid>`. (İsim brief'ten; cancel'da taşınan set yalnız açık modda gözlenebilir) |
| `FaultedChild_CarriesHeadersToOutputMapping` | Child A `explode` PATCH'i: transition task'ı fırlatır, task seviyesinde Abort boundary child'ı **istek hop'unda** fault'latır → `instance.sub.faulted`. `SubflowFaultService` parent'ı (boundary yok) fault'latır ama output mapping'i taşınan setle yine koşar: parent `F`, `carried.sub == <guid>`, `custom == v1`, `authorization`/`x-subflow-depth` yok |

## Akış şekli

`core/Workflows/subflow-carried-headers-lab/`, `build-subflow-carried-headers-lab.py` ile üretilir
(sürüm 1.0.2; publish sürüm-değişmezdir, değişiklikte `VERSION`'ı patch artır). Task:
`core/Tasks/subflow-carried-headers-lab/sfch-script.json` (script, type 7).

```
sfch-parent (F): start → sfch-begin ─auto→ sfch-a (S: sfch-child, mode=aMode)
                   sfch-a ─auto[rule: resume ctx has sub]→ sfch-resume-saw-sub ┐ (onEntry: resumeRoute)
                   sfch-a ─auto[default]────────────────→ sfch-resume-no-sub  ┘ ─auto→ sfch-b (S: sfch-child, mode=auto,
                                                                                         seenSub = resume ctx `sub`) ─auto→ sfch-done
sfch-child (S):  start → sfch-child-route ─[mode=timer]→ sfch-child-timer ─scheduled 2s→ sfch-child-done
                                           ─[mode=auto]─→ sfch-child-done
                                           ─[default]──→ sfch-child-wait ─finish→ sfch-child-done
                                                                          ─explode (task throws, Abort)→ fault
                 cancel-sfch-child → sfch-child-cancelled
```

A'nın output mapping'i (`SfchChildAMapping.csx`) parent verisine `carried.{sub, custom, hasAuthorization,
hasDepth, hasToken, count, keys}` yazar. B'nin input mapping'i (`SfchChildBMapping.csx`) parent'ın
resume hop'unda koşar ve `context.Headers["sub"]`'ı B'nin başlangıç verisine `seenSub` olarak koyar;
output mapping'i parent'a `bSeenSub` olarak geri yazar.

## Nasıl koşulur

Varsayılan mod (runtime `ApplyToResume` kapalı — varsayılan):

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~SubflowCarriedHeadersLab" -v minimal
# beklenen: 5 geçti, 1 skip (ApplyToResume_On_*)
```

Açık mod: runtime'ı `Workflow__SubFlow__CarriedHeaders__ApplyToResume=true` ile başlat, sonra

```bash
VNEXT_IT_CARRIED_HEADERS_APPLY_TO_RESUME=1 dotnet test tests/Core.IntegrationTests \
  --settings tests/Core.IntegrationTests/test.runsettings --filter "FullyQualifiedName~SubflowCarriedHeadersLab" -v minimal
# beklenen: 5 geçti, 1 skip (ApplyToResume_Off_*)
```

Env değişkeni yalnız testin beklentisini seçer; runtime'ın ayarını okumaz. İkisi uyuşmazsa mod
testleri kırmızı düşer.

## Dikkat noktaları

- **`sub` GUID olmalı.** `sub` çağıran kimlik header'ıdır; runtime onu varchar(36) bir kolona yazar
  (`modifiedByBehalfOf`). Daha uzun bir değer (`finisher-<32 hex>`) kaydı `22001 value too long` ile
  düşürür → PATCH 500. Bu özellikten bağımsız, mevcut davranış.
- Bir child task'ının fırlatması **boundary yoksa** hiçbir şey yapmaz (ErrorBoundaryLab U1), child normal
  biter. `explode` bu yüzden task seviyesinde Abort boundary taşır.
- Child A'nın correlation'ı (önceden atanmış id ile) child satırı yazılmadan görünür; test 404'ü bekler.
- `WorkflowTestBase.Headers()` `user_reference`, `x-request-id`, `x-device-id`, `x-token-id` gönderir:
  ilk üçü taşınır, `x-token-id` deny floor'a takılır — bu yüzden `carried.count == 5` (sub, custom + 3).

## Geçme kriteri + kanıt (MCP / disk)

Her test `parent=…`, `child=…`, `sub=…` ve `authorization=Bearer canary-<guid>` satırlarını test
çıktısına yazar (`--logger "console;verbosity=detailed"`). Kontroller:

1. **Canary sızmıyor.** Her canary (`canary-<guid>` deseni) runtime loglarında ve outbox/inbox gövdesinde
   **sıfır** kez geçmeli:
   ```bash
   grep -c "canary-<guid>" <vnext>/.vnext-local/domains/core/logs/*.log      # hepsi 0
   ```
   OpenObserve (org `default`, stream `vnext`): `body` içinde `canary-` araması 0 satır. Request-log
   scope'unda `authorization` `***REDACTED***` görünür.
2. **Span sayacı.** `instance.sub.completed` işleyen span'de `vnext.subflow.carried_header_count` (finish
   testi 5, timer testi 0); log 40140 (`SubFlowCarriedHeadersFiltered`, Debug) yalnız kept/denied/overflow
   sayılarını taşır, değer taşımaz.
3. **Postgres.** Parent satırı (`sfch_parent."Instances"`/`InstancesData`) `carried.sub == <guid>` ve
   `authorization`/canary içermez; outbox satırının `CarriedHeaders`'ında `authorization` yok.
4. Fault testi: parent `F` + aktif incident, child `F`; cancel testi: child `sfch-child-cancelled`, parent
   `C`, `carried` yok.

## Son koşu (2026-10-30, vnext `feature/subflow-terminal-carried-headers` @ 3d94c247, lokal core)

| Mod | Sonuç |
|---|---|
| Varsayılan (`ApplyToResume` kapalı) | 5 geçti, 1 skip (`ApplyToResume_On_*`), art arda 2 koşu |
| Açık (`Workflow__SubFlow__CarriedHeaders__ApplyToResume=true` + `VNEXT_IT_CARRIED_HEADERS_APPLY_TO_RESUME=1`) | 5 geçti, 1 skip (`ApplyToResume_Off_*`) |

Kanıt (`psql` / `redis-cli` / OpenObserve REST — MCP sunucuları o oturumda bağlı değildi):

- `sys_queues."OutboxMessages"` ve `"InboxMessages"`: `canary-` geçen satır **0 / 0** (her iki modda);
  finisher child'ının `instance.sub.completed` olayında `data.carriedHeaders` outbox ve inbox'ta **aynı**
  (`sub`, `x-carried-custom`, `x-request-id`, `x-device-id`, `user_reference`), `authorization` ve
  `x-subflow-depth` yok.
- Redis stream `vnext.core.development.instance.sub.completed.v1`: canary 0, `authorization` 0.
- OpenObserve stream `vnext` (6 saat, 9.301 log): `canary-` 0; `vnext.subflow.carried_header_count`
  etiketli span 36. Log 40140 Debug olduğundan bu ortamda gönderilmez.
- Açık modda parent'ın resume satırları (`sfch_parent."InstanceTransitions"`, `resume-saw-sub` / `auto-b`)
  yalnız filtrelenmiş seti taşır — test SDK'sının `x-token-id`'si (`token` içerir) düşmüş; `start` satırı
  ham header'ları tutmaya devam eder (runtime council koşulu C-1, bu özelliğin dışında).
- Açık modda cancel testinde de resume `saw-sub` dalına gider ve B iptal edenin `sub`'ını görür.

## Kapsanmayanlar

- Re-arm rebuild'i ve yalnız-Inbox (relay düşmüş, backup) yolu: header'lar aynen kopyalanmalı, ama bunu
  tetiklemek relay'i bozmayı gerektirir — testten üretilemez.
- Upward parent fault'ta (output mapping/parent yükleme hatası) set'in grandparent'a yeniden filtrelenmiş
  iletimi: üç seviye + bilinçli bozuk mapping gerektirir, kapsam dışı.
- `exit` ile biten child ve `role` header'ının her zaman taşınması ayrıca assert edilmiyor.
