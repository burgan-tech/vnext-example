# vNext Runtime Integration Test Documentation

Bu dokuman, `core` projesindeki tum integration test workflow'larini, icerdikleri bilesenleri ve her birinin hangi vNext ozelliklerini test ettigini detayli olarak aciklar.

---

## Genel Bakis

| # | Grup | Workflow | Test Edilen Ana Ozellikler |
|---|------|----------|---------------------------|
| 1 | Lifecycle & Transitions | `lifecycle-transitions-test-workflow` | State tipleri, transition tipleri, on-entries/exits, timer, cancel, **exit** (`attributes.exit`), **schedule iptal / timer reschedule**, **queryRoles** ve **transition `roles` (yalnizca state fonksiyonu listeleme filtresi)** |
| 2 | SubFlow & SubProcess | `subflow-orchestration-parent` + child + grandchild | Parent-child-grandchild zinciri, parent shared transitions, **child shared transition**, **cancel cascade** (child/grandchild **cancelled** final state'leri), **effective state**, **updateData (SubFlow)**, **subFlow.overrides** (timeout, states.queryRoles, transitions.roles) |
| 3 | Task Execution | `task-execution-test-workflow` + `task-target-workflow` + `extended-tasks-test-workflow` | HTTP/Script/StartFlow/GetInstanceData/Notification/TriggerTransition/SubProcess/GetInstances; Dapr HTTP/Service/PubSub (`extended-tasks-test-workflow`); Mocklab + Dapr YAML |
| 4 | Error Boundary | `error-boundary-test-workflow` | Retry, Ignore, **Rollback**, **Log**, **Notify** (action:4) aksiyonlari, retry policy, priority, **timeoutPolicy** (onTimeout), **errorHandlerRule** (errorTypes/errorCodes) |
| 5 | View, Function & Extension | `view-function-extension-test-workflow` | View tipleri, display modlari, function, extension, wizard state, **features** referansi |
| 6 | Schema & Data | `schema-data-test-workflow` | Master schema, transition schema, updateData, field roles |
| 7 | Instance Management | `instance-management-test-workflow` | Filtering, pagination, sorting, timeout, idempotent start, **subType 4/5/6** (suspended/busy/human) |
| 8 | Flow Types | `core-flow-test` + `subprocess-flow-test` + `flow-flow-test` + `subflow-flow-test` | **Core (C)**, **SubProcess (P)**, **Flow (F)** ve **SubFlow (S)** workflow tipleri |
| 9 | Version Consistency | `version-consistency-test-workflow` (v1.0.0 + v2.0.0) | Workflow versiyon degisiminde mevcut instance'larin kendi versiyonlariyla devam etmesi |
| 10 | Dynamic Collection & Object | `collection-object-test-workflow` | ScriptBase dinamik koleksiyon/nesne API'leri (CreateObject, CreateList, SetProperty, GetList, AsList, ListFilter, ListCount, ListAny, ListFirst, ListLast, ListSelect, ListAdd, ListRemove, RemoveProperty, ToDictionary, HasProperty) |

**Grup numaralari:** Eski **Grup 8 (Extended Tasks)** icerigi **Grup 3 (Task Execution)** altinda birlestirilmistir (`extended-tasks-test-workflow` artik Grup 3 kapsaminda). Matris ve tablolarda **G8 / Grup 8** = Flow Types, **G9 / Grup 9** = Version Consistency.

---

## Bilesen Envanteri

```
core/
├── Workflows/          (15 workflow - 13 mevcut + 2 yeni version-consistency)
├── Tasks/              (22 task - 21 mevcut + 1 yeni version-consistency; Dapr Binding task kaldirildi)
├── Views/              (3 view)
├── Schemas/            (2 schema)
├── Functions/          (2 function)
├── Extensions/         (2 extension)
├── etc/dapr/components/  (Dapr binding ve pubsub YAML)
└── doc/
    └── integration-test-documentation.md
```

---

## Grup 1: Lifecycle & Transitions

**Workflow:** `lifecycle-transitions-test-workflow` (type: F)

**Amac:** Workflow yasam dongusu ve tum transition tiplerini test eder; ayrica **exit transition**, **zamanlayici schedule yonetimi** (manuel iptal ve **self-loop reschedule** — hedef olarak mevcut state anahtari `auto-passed-state`; `$self` ile ayni davranis), **workflow/state queryRoles** ve **`GET .../functions/state` icinde transition listesinin role gore filtrelenmesi** senaryolarini kapsar.

**Important (transition `roles`):** In the runtime model, transition `roles` (allow/deny) are **not a hard PATCH gate that blocks invoking the transition**; they primarily **filter the `transitions[]` list in the state-function response** for the caller role header. A client not on the allow list may **omit** that transition from UI/API lists; documented behaviour **does not** mean the transition is **always rejected** via the API. (**Separate mechanism:** **`authorize?queryRoles=...`** for querying workflow/instance data.)

**C# testleri:** `StateFunction_*` testleri, ilgili **role header** ile state sorgulandiginda `transitions[]` icinde beklenen anahtarin **listelenip listelenmedigini** dogrular.

### Agac Yapisi

```
lifecycle-transitions-test-workflow (F)
│
├── [workflow-level queryRoles]
│   └── test-viewer → allow
│
├── [startTransition] start-lifecycle-test
│   └── onExecutionTasks:
│       └── script-task + InitializeDataMapping.csx
│
├── [cancel] cancel-workflow → terminated-state
│
├── [attributes.exit] exit-workflow → terminated-state
│   ├── triggerType: 0 (Manual)
│   ├── availableIn: initialize-state, processing-state, pre-complete-state
│   └── onExecutionTasks:
│       └── script-task + ExitMapping.csx (IMapping)
│
├── initialize-state (Initial, stateType:1)
│   └── Transitions:
│       └── move-to-processing (Manual, triggerType:0) → processing-state
│           ├── mapping: ProcessTransitionMapping.csx (ITransitionMapping)
│           └── roles: test-operator (allow) — state `transitions[]` filtresi; PATCH zorunlu red degil
│
├── processing-state (Intermediate, stateType:2)
│   ├── onEntries:
│   │   └── script-task + ProcessEntryMapping.csx
│   ├── onExits:
│   │   └── script-task + ProcessExitMapping.csx
│   └── Transitions:
│       ├── auto-pass-transition (Auto, triggerType:1) → auto-passed-state
│       │   └── rule: TestPathPassRule.csx (testPath=="pass")
│       ├── auto-fail-transition (Auto, triggerType:1) → auto-failed-state
│       │   └── rule: TestPathFailRule.csx (testPath=="fail")
│       └── default-auto-transition (Auto, triggerType:1, triggerKind:10) → default-fallback-state
│           └── rule: AlwaysTrueRule.csx (varsayilan fallback)
│
├── auto-passed-state (Intermediate, stateType:2)
│   └── Transitions:
│       ├── scheduled-timer-transition (Scheduled, triggerType:2) → timer-triggered-state
│       │   └── timer: ShortTimerMapping.csx (ITimerMapping, 6 saniye)
│       ├── cancel-schedule-manually (Manual, triggerType:0) → pre-complete-state
│       │   └── (zamanlayici schedule'ini manuel transition ile iptal senaryosu)
│       └── reschedule-timer (Manual, triggerType:0, target: auto-passed-state)
│           └── (same-state self-loop; reschedule timer — use explicit state key rather than `$self` in definition validation)
│
├── timer-triggered-state (Intermediate, stateType:2)
│   ├── onEntries:
│   │   └── script-task + TimerEntryMapping.csx
│   └── Transitions:
│       └── auto-to-pre-complete (Auto, triggerType:1) → pre-complete-state
│           └── rule: AlwaysTrueRule.csx
│
├── pre-complete-state (Intermediate, stateType:2)
│   ├── queryRoles: test-processor (allow), test-viewer (deny)
│   └── Transitions:
│       └── complete-workflow (Manual, triggerType:0) → completed-state
│           └── roles: test-approver (allow), test-operator (deny) — listeleme filtresi; deny PATCH'i garanti etmez
│
├── default-fallback-state (Intermediate, stateType:2)
│   └── Transitions:
│       └── fallback-to-complete (Auto, triggerType:1) → completed-state
│           └── rule: AlwaysTrueRule.csx
│
├── auto-failed-state (Final/Error, stateType:3, subType:2)
├── completed-state (Final/Success, stateType:3, subType:1)
└── terminated-state (Final/Terminated, stateType:3, subType:3)
```

### Test Edilen Ozellikler

| Ozellik | Nasil Test Edilir | Ilgili Eleman |
|---------|-------------------|---------------|
| State tipleri (Initial/Intermediate/Final) | 3 farkli stateType kullanilir | Tum state'ler |
| State alt tipleri (Success/Error/Terminated) | subType 1, 2, 3 kullanilir | auto-failed, completed, terminated |
| Manuel transition (triggerType:0) | Kullanici tetikli gecisler | move-to-processing, complete-workflow, cancel-schedule-manually, reschedule-timer |
| Otomatik transition (triggerType:1) | Rule bazli otomatik gecisler | auto-pass, auto-fail |
| Tamamlayici kurallar (complementary rules) | Ayni state'te zit kosullu auto transitions | TestPathPassRule + TestPathFailRule |
| Varsayilan otomatik gecis (triggerKind:10) | Hicbir rule eslesmezse fallback | default-auto-transition |
| Zamanlanmis transition (triggerType:2) | Timer ile gecis | scheduled-timer-transition |
| ITimerMapping | Timer suresi belirleme | ShortTimerMapping.csx |
| ITransitionMapping | Transition sirasinda data donusumu | ProcessTransitionMapping.csx |
| IConditionMapping | Kosul degerlendirme | TestPathPassRule, TestPathFailRule, AlwaysTrueRule |
| onEntries | State girisinde task calistirma | processing-state, timer-triggered-state |
| onExits | State cikisinda task calistirma | processing-state |
| startTransition onExecutionTasks | Workflow baslatilirken task calistirma | InitializeDataMapping.csx |
| Cancel transition | Workflow iptal mekanizmasi | cancel-workflow → terminated-state |
| **Exit transition (`attributes.exit`)** | Belirli state'lerde cikis; hedef terminated; IMapping ile exitExecuted | exit-workflow, ExitMapping.csx |
| **Schedule iptal (manuel)** | auto-passed-state'ten manuel gecisle timer beklemeden pre-complete | cancel-schedule-manually |
| **Timer reschedule (self-loop)** | Ayni state'e manuel gecis ile zamanlayici reschedule | reschedule-timer → **auto-passed-state**; sonra yeniden kurulan `scheduled-timer-transition` (~6s) → timer-triggered → auto → pre-complete |
| **queryRoles (workflow)** | authorize endpoint ile workflow datasi sorgu yetkisi | attributes.queryRoles, test-viewer |
| **queryRoles (state)** | State bazli allow/deny override | **pre-complete-state** queryRoles (`processing-state` auto gecisli; testte cancel-schedule ile pre-complete) |
| **Transition `roles`** | **Filter `transitions[]` in `GET .../functions/state`** by role (v0.0.39+); **no** guaranteed PATCH denial solely from deny on that list | move-to-processing, complete-workflow; xUnit: `StateFunction_WithTestOperatorRole_*` / `StateFunction_WithTestViewerRole_*` |
| Version strategy (Major/Minor/Patch) | Farkli versiyonlama stratejileri | Cesitli transitions |
| Idempotent start | Ayni key ile tekrar baslatma | HTTP test dosyasinda |

### HTTP Test Dosyasi (`lifecycle-transitions-test-workflow.http`)

| Senaryo | Kisa Aciklama |
|---------|---------------|
| Happy / Fail / Default path | Mevcut lifecycle akislari |
| Idempotent start | Ayni key ile tekrar baslatma |
| Cancel | cancel-workflow → terminated-state |
| **Exit** | initialize-state'ten exit-workflow; terminated-state ve `exitExecuted` |
| **Schedule cancel** | auto-passed-state'te cancel-schedule-manually → pre-complete-state |
| **Reschedule** | reschedule-timer ile kisa sure `auto-passed-state`; yeniden kurulan ~6s timer sonrasi `timer-triggered-state` → auto → **pre-complete-state** (xUnit: `RescheduleTimer_SelfTransition_ThenWaitsForRescheduledTimer_ReachesPreCompleteState`) |
| **QueryRoles / transition listesi** | **authorize:** `/functions/authorize?queryRoles=true&role=...` (datasi sorgu). **State listesi:** `GET .../functions/state` + role header ile **move-to-processing** gorunurlugu (allow/omit). **pre-complete-state:** cancel-schedule ile; queryRoles'ta test-processor allow / test-viewer deny |

### Kullanilan Bilesenler

| Tip | Anahtar | Dosya |
|-----|---------|-------|
| Task | `script-task` | Tasks/lifecycle-transitions/script-task.json |
| CSX | InitializeDataMapping | Workflows/lifecycle-transitions/src/InitializeDataMapping.csx |
| CSX | ExitMapping | Workflows/lifecycle-transitions/src/ExitMapping.csx |
| CSX | ProcessTransitionMapping | Workflows/lifecycle-transitions/src/ProcessTransitionMapping.csx |
| CSX | ProcessEntryMapping | Workflows/lifecycle-transitions/src/ProcessEntryMapping.csx |
| CSX | ProcessExitMapping | Workflows/lifecycle-transitions/src/ProcessExitMapping.csx |
| CSX | TestPathPassRule | Workflows/lifecycle-transitions/src/TestPathPassRule.csx |
| CSX | TestPathFailRule | Workflows/lifecycle-transitions/src/TestPathFailRule.csx |
| CSX | AlwaysTrueRule | Workflows/lifecycle-transitions/src/AlwaysTrueRule.csx |
| CSX | ShortTimerMapping | Workflows/lifecycle-transitions/src/ShortTimerMapping.csx |
| CSX | TimerEntryMapping | Workflows/lifecycle-transitions/src/TimerEntryMapping.csx |

**Workflow tag'leri (ozet):** `exit-transition`, `schedule-cancel`, `schedule-reschedule`, `query-roles`, `transition-roles` dahil olmak uzere lifecycle ile ilgili tum tag'ler `lifecycle-transitions-test-workflow.json` icinde tanimlidir.

---

## Grup 2: SubFlow & SubProcess Orchestration

**Workflow'lar:**
- `subflow-orchestration-parent` (type: F) - Ana flow
- `subflow-orchestration-child` (type: S) - Alt flow
- `subflow-orchestration-grandchild` (type: S) - Torun flow

**Amac:** Parent-child-grandchild SubFlow zincirini, parent seviyesindeki shared transition'lari, **child workflow uzerindeki shared transition'u**, **cancel cascade** (parent iptalinde alt akislarda **child-cancelled** / **grandchild-cancelled** final state'leri), **`/functions/state` uzerinden effective state** gorunurlugunu, **updateData transition** (SubFlow'dan parent data guncelleme) ozelligini ve **`subFlow.overrides`** mekanizmasini (parent'tan child workflow'a timeout, state `queryRoles` ve transition `roles` override etme) test eder.

### Agac Yapisi

```
subflow-orchestration-parent (F)
│
├── [startTransition] start-subflow-orchestration-parent
│   └── onExecutionTasks:
│       └── subflow-script-task + ParentStartMapping.csx
│
├── [cancel] cancel-parent → parent-cancelled
│
├── [sharedTransitions]
│   └── shared-update-data (Manual, target:$self)
│       ├── availableIn: ["parent-subflow-state"]
│       └── onExecutionTasks:
│           └── subflow-script-task + ParentSharedUpdateMapping.csx
│
├── parent-initial (Initial, stateType:1)
│   └── Transitions:
│       └── auto-parent-to-subflow (Auto) → parent-subflow-state
│           └── rule: AlwaysTrueRule.csx
│
├── parent-subflow-state (SubFlow, stateType:4)
│   ├── subFlow: type "S"
│   │   ├── process: subflow-orchestration-child
│   │   ├── mapping: ParentToChildSubFlowMapping.csx (ISubFlowMapping)
│   │   └── overrides:
│   │       ├── timeout: child-push-timeout → child-cancelled (PT15M, OnEntry)
│   │       ├── states.child-initial.queryRoles: [morph-idm.maker=allow, morph-idm.viewer=deny]
│   │       └── transitions.auto-child-to-subflow.roles: [morph-idm.maker=allow, morph-idm.viewer=deny]
│   └── Transitions:
│       └── auto-parent-to-completed (Auto) → parent-completed
│           └── rule: AlwaysTrueRule.csx
│
├── parent-completed (Final/Success, stateType:3, subType:1)
└── parent-cancelled (Final/Terminated, stateType:3, subType:3)

    subflow-orchestration-child (S)
    │
    ├── [cancel] cancel-child → child-cancelled
    │
    ├── [updateData] update-parent-data (Manual, target:$self)
    │   └── onExecutionTasks:
    │       └── subflow-script-task + UpdateParentDataMapping.csx (IMapping)
    │
    ├── [sharedTransitions]
    │   └── shared-child-update (Manual, target:$self)
    │       ├── availableIn: ["child-subflow-state"]
    │       └── onExecutionTasks:
    │           └── subflow-script-task + ChildSharedUpdateMapping.csx (IMapping)
    │
    ├── [startTransition] start-subflow-orchestration-child → child-initial
    │
    ├── child-initial (Initial, stateType:1)
    │   ├── onEntries:
    │   │   └── subflow-script-task + ChildStartMapping.csx
    │   └── Transitions:
    │       └── auto-child-to-subflow (Auto) → child-subflow-state
    │           └── rule: AlwaysTrueRule.csx
    │
    ├── child-subflow-state (SubFlow, stateType:4)
    │   ├── subFlow: type "S"
    │   │   ├── process: subflow-orchestration-grandchild
    │   │   └── mapping: ChildToGrandchildSubFlowMapping.csx (ISubFlowMapping)
    │   └── Transitions:
    │       └── auto-child-to-completed (Auto) → child-completed
    │           └── rule: AlwaysTrueRule.csx
    │
    ├── child-completed (Final/Success, stateType:3, subType:1)
    └── child-cancelled (Final/Terminated, stateType:3, subType:3)

        subflow-orchestration-grandchild (S)
        │
        ├── [cancel] cancel-grandchild → grandchild-cancelled
        │
        ├── [startTransition] start-subflow-orchestration-grandchild → grandchild-initial
        │
        ├── grandchild-initial (Initial, stateType:1)
        │   ├── onEntries:
        │   │   └── subflow-script-task + GrandchildStartMapping.csx
        │   └── Transitions:
        │       └── complete-grandchild (Manual) → grandchild-completed
        │
        ├── grandchild-completed (Final/Success, stateType:3, subType:1)
        │   └── onEntries:
        │       └── subflow-script-task + GrandchildCompleteMapping.csx
        │
        └── grandchild-cancelled (Final/Terminated, stateType:3, subType:3)
```

### Test Edilen Ozellikler

| Ozellik | Nasil Test Edilir | Ilgili Eleman |
|---------|-------------------|---------------|
| SubFlow state (stateType:4) | Parent ve child'da SubFlow state | parent-subflow-state, child-subflow-state |
| SubFlow tipi (type: S) | Child ve grandchild workflow tipleri | subflow-orchestration-child/grandchild |
| 3 seviye zincirleme (parent→child→grandchild) | Ic ice SubFlow cagrilari | Tum 3 workflow |
| ISubFlowMapping | SubFlow'a data gonderme/alma | ParentToChildSubFlowMapping, ChildToGrandchildSubFlowMapping |
| Shared transitions ($self) — parent | State degistirmeden data guncelleme | shared-update-data |
| **Shared transitions ($self) — child** | Child tanimindaki paylasimli gecis; **availableIn: child-subflow-state** | shared-child-update, ChildSharedUpdateMapping.csx |
| availableIn kisitlamasi | Shared transition sadece belirli state'lerde | parent: parent-subflow-state; child: child-subflow-state |
| Cancel transition — parent | Parent iptal | cancel-parent → parent-cancelled |
| **Cancel hedefleri — child / grandchild** | Alt akislarda iptal final state | cancel-child → **child-cancelled**; cancel-grandchild → **grandchild-cancelled** |
| **Cancel cascade** | Parent iptalinde ic ice sonlandirma | HTTP: cancel-parent senaryosu |
| **Effective state** | `/functions/state` ile en derin aktif alt akis durumu (or. grandchild-initial) | HTTP Test 4 |
| **UpdateData transition (SubFlow)** | Child'dan parent instance datasini guncelleme ($self) | update-parent-data, UpdateParentDataMapping.csx |
| Manuel transition (SubFlow icinde) | Grandchild'da kullanici tetikli tamamlama | complete-grandchild |
| **subFlow.overrides.timeout** | Parent'tan child'a timeout override (PT15M, OnEntry, → child-cancelled) | parent-subflow-state.subFlow.overrides.timeout |
| **subFlow.overrides.states.queryRoles** | Parent'tan child-initial state'inin queryRoles'unu ezme | parent-subflow-state.subFlow.overrides.states.child-initial |
| **subFlow.overrides.transitions.roles** | Parent'tan auto-child-to-subflow transition'inin roles'unu ezme | parent-subflow-state.subFlow.overrides.transitions.auto-child-to-subflow |

### HTTP Test Dosyasi (`subflow-orchestration.http`)

| Test | Kisa Aciklama |
|------|---------------|
| TEST 1 Happy path | Grandchild tamamlama, parent-completed ve data bayraklari |
| **TEST 2 Cancel cascade** | Parent cancel; alt akislarin sonlanmasi ve parent-cancelled |
| **TEST 3 Child shared transition** | Parent instance URL uzerinden **shared-child-update**; `childSharedUpdateExecuted` |
| **TEST 4 Effective state** | nested calisma sirasinda effectiveState / metadata |
| **TEST 5 UpdateData SubFlow** | Child updateData tetikleme; parent data'da `childUpdatedParent` kontrolu |

### Kullanilan Bilesenler

| Tip | Anahtar | Dosya |
|-----|---------|-------|
| Task | `subflow-script-task` | Tasks/subflow-orchestration/subflow-script-task.json |
| CSX | ParentStartMapping | Workflows/subflow-orchestration/src/ParentStartMapping.csx |
| CSX | ParentToChildSubFlowMapping | Workflows/subflow-orchestration/src/ParentToChildSubFlowMapping.csx |
| CSX | ParentSharedUpdateMapping | Workflows/subflow-orchestration/src/ParentSharedUpdateMapping.csx |
| CSX | ChildStartMapping | Workflows/subflow-orchestration/src/ChildStartMapping.csx |
| CSX | ChildToGrandchildSubFlowMapping | Workflows/subflow-orchestration/src/ChildToGrandchildSubFlowMapping.csx |
| CSX | **ChildSharedUpdateMapping** | Workflows/subflow-orchestration/src/ChildSharedUpdateMapping.csx |
| CSX | **UpdateParentDataMapping** | Workflows/subflow-orchestration/src/UpdateParentDataMapping.csx |
| CSX | GrandchildStartMapping | Workflows/subflow-orchestration/src/GrandchildStartMapping.csx |
| CSX | GrandchildCompleteMapping | Workflows/subflow-orchestration/src/GrandchildCompleteMapping.csx |
| CSX | AlwaysTrueRule | Workflows/subflow-orchestration/src/AlwaysTrueRule.csx |

---

## Grup 3: Task Execution (runtime gorevleri + Dapr dis servis)

**Workflow'lar:**
- `task-execution-test-workflow` (type: F) — Ana test workflow'u (HTTP, Script, Timer Wait, StartFlow, GetInstanceData, Notification, TriggerTransition, SubProcess, GetInstances → completed). Human Task (tip 5) runtime tarafindan kaldirilacak gecici bir ozellik oldugu icin bu zincirden cikartildi.
- `task-target-workflow` (type: F) — StartFlow / GetInstanceData / TriggerTransition / SubProcess / GetInstances icin hedef
- `extended-tasks-test-workflow` (type: F) — **Dapr-only** outbound calls: HTTP invoke, Service, PubSub (single chain; HTTP test: `api-tests/task-execution/task-execution.http` **Section 3**). **Note:** Dapr Binding (type 2) was removed from test scope (see **Untested Features** at end of file).

**Amac:** Tum **runtime-ici** gorev tipleri ile **Dapr tabanli** gorev tiplerini ayri workflow'larda dogrular; task siralama (`onEntries` order), `context.Body.data`, scheduled transition (Timer Task tip 9), auto transition (Condition Task tip 8) ve Dapr bilesen YAML (`etc/dapr/components/`) ile uyumu kapsar.

**CSX klasorleri:** `Workflows/task-execution/src/task-execution-test-workflow/` (`task-execution-test-workflow.json` mapping'leri), `.../src/extended-tasks-test-workflow/` (`extended-tasks-test-workflow.json`), `.../src/shared/AlwaysTrueRule.csx` (otomatik gecis kurallari; `task-target-workflow` dahil).

**Task JSON klasorleri:** `Tasks/task-execution/task-execution-test-workflow/` (ana workflow + hedef workflow'un kullandigi task'lar), `Tasks/task-execution/extended-tasks-test-workflow/` (Dapr task'lari).

### Agac Yapisi — `task-execution-test-workflow` (F)

```
task-execution-test-workflow (F)
│
├── [startTransition] start-task-execution
│   └── onExecutionTasks:
│       └── task-exec-script-task + InitTaskTestMapping.csx
│
├── http-task-state (Initial, stateType:1)
│   ├── onEntries: http-process-task (type:6) + HttpProcessMapping.csx
│   └── Transitions: auto-to-script-processing → script-processing-state
│
├── script-processing-state
│   ├── onEntries: task-exec-script-task (type:7) + ScriptProcessMapping.csx
│   └── Transitions: auto-to-timer-wait → timer-wait-state
│
├── timer-wait-state  (Timer Task tip 9 testi)
│   ├── onEntries: task-exec-script-task + TimerStartMapping.csx
│   │              (timerStartedAt, timerExpectedSeconds=3 yazar)
│   └── Transitions: scheduled-to-start-flow → start-flow-state
│       (triggerType: 2 + ITimerMapping (TimerScheduleMapping.csx, 3 sn) →
│        runtime arka planda Timer Task uretir; testler timerStartedAt'tan
│        gecen sureye bakarak gercek beklemeyi dogrular)
│
├── start-flow-state
│   ├── onEntries: start-flow-task (type:11) + StartFlowMapping.csx
│   └── Transitions: auto-to-get-instance-data → get-instance-data-state
│
├── get-instance-data-state
│   ├── onEntries: get-instance-data-task (type:13) + GetInstanceDataMapping.csx
│   └── Transitions: auto-to-notification → notification-state
│
├── notification-state
│   ├── onEntries: notification-task (type:10) + mapping.type G
│   └── Transitions: auto-to-trigger-transition → trigger-transition-state
│
├── trigger-transition-state
│   ├── onEntries: trigger-transition-task (type:12) + TriggerTransitionMapping.csx
│   └── Transitions: auto-to-subprocess → subprocess-state
│
├── subprocess-state
│   ├── onEntries: subprocess-task (type:14) + SubProcessMapping.csx
│   └── Transitions: auto-to-get-instances → get-instances-state
│
├── get-instances-state
│   ├── onEntries: get-instances-task (type:15) + GetInstancesMapping.csx
│   └── Transitions: auto-to-completed → completed-state
│
└── completed-state (Final/Success, stateType:3, subType:1)

task-target-workflow (F)
│
├── [startTransition] start-target → target-initial
├── target-initial — manual-complete-target → target-completed
└── target-completed
```

### Agac Yapisi — `extended-tasks-test-workflow` (F, sadece Dapr)

```
extended-tasks-test-workflow (F)
│
├── [startTransition] start-extended-tasks → init-state
│
├── init-state (Initial)
│   ├── onEntries: script-task (Tasks/lifecycle-transitions/script-task.json) + InitExtendedTaskMapping.csx
│   └── Transitions: auto-to-dapr-http → dapr-http-state
│
├── dapr-http-state → dapr-service-state → dapr-pubsub-state
│   (sirasiyla type 1, 3, 4 Dapr task'leri + Dapr*Mapping.csx)
│   (Dapr Binding (tip 2) state'i ve task'i kaldirildi — bkz. Test Edilmeyen Ozellikler)
│
└── completed-state (Final; pubsub sonrasi otomatik tamamlanma)
```

### Test Edilen Ozellikler

| Ozellik | Nasil Test Edilir | Ilgili Eleman |
|---------|-------------------|---------------|
| HTTP Task (type:6) | Mocklab HTTP | http-process-task + HttpProcessMapping |
| Script Task (type:7) | C# script data | task-exec-script-task + ScriptProcessMapping |
| **Condition Task (type:8)** | Sistem gorevi — `triggerType: 1` (auto) + `IConditionMapping` rule calistirilirken runtime tarafindan otomatik uretilir; manuel JSON ile tanimlanmaz. Tum `auto-to-*` gecislerinde `AlwaysTrueRule.csx` calisir → her auto transition icin Condition Task ortaya cikar. | Tum auto transition'lar (`script-processing-state`, `start-flow-state`, `get-instance-data-state`, `notification-state`, `trigger-transition-state`, `subprocess-state`, `get-instances-state`) + `shared/AlwaysTrueRule.csx` |
| **Timer Task (type:9)** | Sistem gorevi — `triggerType: 2` (scheduled) + `ITimerMapping` calistirilirken runtime tarafindan otomatik uretilir; manuel JSON ile tanimlanmaz. `timer-wait-state` cikis transition'i `scheduled-to-start-flow` 3 sn bekler; testler parent attributes.timerStartedAt ile (DateTime.UtcNow - timerStartedAt) >= ~2.5 sn olarak gercek beklemeyi dogrular. | timer-wait-state + TimerStartMapping (anlik mark) + TimerScheduleMapping (scheduled timer 3 sn) |
| StartFlow Task (type:11) | Hedef workflow baslatma + hedef instance teyidi (`startedInstanceId` + GET state) | start-flow-task + StartFlowMapping (SetBody parentInstanceId/source/note) |
| GetInstanceData Task (type:13) | Baska instance datasi | get-instance-data-task + GetInstanceDataMapping |
| Notification Task (type:10) | Platform mapping G (script yok) | notification-task + mapping `{ "type": "G" }` |
| TriggerTransition Task (type:12) | Hedef instance + manuel gecis | trigger-transition-task + TriggerTransitionMapping (SetInstance + `manual-complete-target`) |
| SubProcess Task (type:14) | Alt surec baslatma + hedef instance teyidi (`subprocessInstanceId` + `subprocessData` yanit ozeti + GET state/attributes) | subprocess-task + SubProcessMapping (SetBody parentInstanceId/source/note; OutputHandler `subprocessData` = `context.Body.data` alanlari) |
| GetInstances Task (type:15) | Instance listesi | get-instances-task + GetInstancesMapping |
| Dapr HTTP (1) / Service (3) / PubSub (4) | Sidecar + YAML; **task gercekten calisti** kaniti (skill §6.4): HTTP/Service icin mocklab yanitindan donen `processId` parent attributes'a yazilir ve testte non-empty string assert edilir; PubSub icin runtime `context.Body.isSuccess` `published` olarak yazilir ve `true` assert edilir. **PubSub bileseni** task JSON'da `pubSubName: "{DaprPubSubName}"` placeholder; `DaprPubSubMapping.InputHandler` `GetConfigValue("DaprPubSubName")` ile Vault'tan okuyup `SetPubSubName(...)` ile override eder (varsayilan: `vnext-pubsub` Redis sistem bileseni). | `extended-tasks-test-workflow` + dapr-*-task; assertion'lar `ExtendedTasksWorkflowInstanceDataAssertions.AssertDaprChainCompleted` |
| HttpTask / StartTask / GetInstanceDataTask cast | InputHandler | Ilgili mapping.csx |
| context.Body.data | Task yaniti | OutputHandler'lar |
| GetConfigValue | MocklabBaseUrl | HttpProcessMapping |
| Task siralama | onEntries order | Tum state'ler |
| Hedef workflow yasam dongusu | StartFlow + GetInstanceData + TriggerTransition + SubProcess hedefleri | task-target-workflow (ortak hedef) |

### Kullanilan Bilesenler (ozet)

| Tip | Anahtar | Tip Kodu | Dosya |
|-----|---------|----------|-------|
| Task | `http-process-task` | 6 | Tasks/task-execution/task-execution-test-workflow/http-process-task.json |
| Task | `task-exec-script-task` | 7 | Tasks/task-execution/task-execution-test-workflow/task-exec-script-task.json |
| Task | `start-flow-task` | 11 | Tasks/task-execution/task-execution-test-workflow/start-flow-task.json |
| Task | `get-instance-data-task` | 13 | Tasks/task-execution/task-execution-test-workflow/get-instance-data-task.json |
| Task | `notification-task` | 10 | Tasks/task-execution/task-execution-test-workflow/notification-task.json |
| Task | `trigger-transition-task` | 12 | Tasks/task-execution/task-execution-test-workflow/trigger-transition-task.json |
| Task | `subprocess-task` | 14 | Tasks/task-execution/task-execution-test-workflow/subprocess-task.json |
| Task | `get-instances-task` | 15 | Tasks/task-execution/task-execution-test-workflow/get-instances-task.json |
| Task | `dapr-http-task` | 1 | Tasks/task-execution/extended-tasks-test-workflow/dapr-http-task.json |
| Task | `dapr-service-task` | 3 | Tasks/task-execution/extended-tasks-test-workflow/dapr-service-task.json |
| Task | `dapr-pubsub-task` | 4 | Tasks/task-execution/extended-tasks-test-workflow/dapr-pubsub-task.json |
| Task | `script-task` (paylasimli) | 7 | Tasks/lifecycle-transitions/script-task.json |
| CSX | `task-execution-test-workflow` mapping'leri | `Workflows/task-execution/src/task-execution-test-workflow/*.csx` |
| CSX | `extended-tasks-test-workflow` mapping'leri | `Workflows/task-execution/src/extended-tasks-test-workflow/*.csx` |
| CSX | Ortak auto-transition rule | `Workflows/task-execution/src/shared/AlwaysTrueRule.csx` |

**HTTP / Postman:** `api-tests/task-execution/task-execution.http` (Sections 1–3, happy path). Postman: `postman-task-execution-collection.json` (same three sections).

**C# integration tests:** `tests/Core/Tests/task-execution-test-workflow/` (`TaskExecutionTestWorkflowTests`, `TaskExecutionInstanceDataAssertions`) — B1/B2/B3 happy paths, instance data contract on the main workflow, and extended Dapr `taskResults` flags.

**Workflow:** `error-boundary-test-workflow` (type: F)

**Amac:** Hata yonetimi mekanizmasini (error boundary) task, state ve workflow seviyelerinde test eder; **Rollback (action:2)**, **Log (action:5)** ve **Notify (action:4)** aksiyonlari ile zincirlenmis senaryoyu kapsar. Ayrica **timeoutPolicy** (onTimeout) ve **gelismis errorHandlerRule** (errorTypes/errorCodes filtreleme) ozelliklerini icerir.

### Agac Yapisi

```
error-boundary-test-workflow (F)
│
├── [startTransition] start-error-boundary-test
│   └── onExecutionTasks:
│       └── error-script-task + InitErrorTestMapping.csx
│
├── [workflow-level errorBoundary]
│   ├── onError: action:0 (Abort), priority:100
│   └── onTimeout: action:3 (Ignore)
│
├── retry-test-state (Initial, stateType:1)
│   ├── onEntries:
│   │   └── error-http-task (type:6) + ErrorHttpMapping.csx
│   │       └── [task-level errorBoundary]
│   │           └── onError: action:1 (Retry), priority:10
│   │               └── retryPolicy: maxRetries:2, initialDelay:PT2S, backoffType:0
│   └── Transitions:
│       └── auto-to-ignore-test (Auto) → ignore-test-state
│           └── rule: AlwaysTrueRule.csx
│
├── ignore-test-state (Intermediate, stateType:2)
│   ├── [state-level errorBoundary]
│   │   └── onError: action:3 (Ignore), priority:10, errorTypes:["System.InvalidOperationException"], errorCodes:["*"]
│   ├── onEntries:
│   │   ├── order:1 error-script-task + ThrowErrorMapping.csx (kasitli hata firlatir)
│   │   └── order:2 error-script-task + IgnoreErrorMapping.csx (hata yoksayildiktan sonra calisir)
│   └── Transitions:
│       └── auto-to-rollback-from-ignore (Auto) → rollback-test-state
│           └── rule: AlwaysTrueRule.csx
│
├── rollback-test-state (Intermediate, stateType:2)
│   ├── [state-level errorBoundary]
│   │   └── onError: action:2 (Rollback), priority:10
│   ├── onEntries:
│   │   ├── order:1 error-script-task + ThrowErrorMapping.csx
│   │   └── order:2 error-script-task + RollbackMapping.csx (IMapping)
│   └── Transitions:
│       └── auto-to-log-from-rollback (Auto) → log-test-state
│           └── rule: AlwaysTrueRule.csx
│
├── log-test-state (Intermediate, stateType:2)
│   ├── [state-level errorBoundary]
│   │   └── onError: action:5 (Log), priority:10
│   ├── onEntries:
│   │   ├── order:1 error-script-task + ThrowErrorMapping.csx
│   │   └── order:2 error-script-task + LogOnlyMapping.csx
│   └── Transitions:
│       └── auto-to-completed-from-log (Auto) → notify-test-state
│           └── rule: AlwaysTrueRule.csx
│
├── notify-test-state (Intermediate, stateType:2)
│   ├── [state-level errorBoundary]
│   │   └── onError: action:4 (Notify), priority:10, transition:"auto-to-completed-from-notify"
│   ├── onEntries:
│   │   ├── order:1 error-script-task + ThrowErrorMapping.csx
│   │   └── order:2 error-script-task + NotifyMapping.csx (IMapping)
│   └── Transitions:
│       └── auto-to-completed-from-notify (Auto) → completed-state
│           └── rule: AlwaysTrueRule.csx
│
├── completed-state (Final/Success, stateType:3, subType:1)
└── error-final (Final/Error, stateType:3, subType:2)
```

### Test Edilen Ozellikler

| Ozellik | Nasil Test Edilir | Ilgili Eleman |
|---------|-------------------|---------------|
| Task-level error boundary | Task onEntries'de errorBoundary | retry-test-state onEntries |
| State-level error boundary | State uzerinde errorBoundary | ignore-test-state, rollback-test-state, log-test-state |
| Workflow-level error boundary | Workflow attributes'da errorBoundary | attributes.errorBoundary |
| Retry action (action:1) | HTTP task hata alinca yeniden dene | retry-test-state task errorBoundary |
| Retry policy | maxRetries, initialDelay, backoffType | retryPolicy yapisi |
| Ignore action (action:3) | Script hata firlatinca yoksay | ignore-test-state errorBoundary |
| **Rollback action (action:2)** | State errorBoundary rollback | rollback-test-state |
| **Log action (action:5)** | Hata loglama; siradaki task ile devam | log-test-state |
| **Notify action (action:4)** | Hata bildirim ve transition tetikleme | notify-test-state errorBoundary |
| Abort action (action:0) | En son calisacak global yakalama | workflow errorBoundary |
| **timeoutPolicy (onTimeout)** | Timeout durumunda action:3 (Ignore) | workflow errorBoundary.onTimeout |
| **errorHandlerRule (errorTypes)** | Hata tipine gore filtreleme | ignore-test-state: System.InvalidOperationException |
| **errorHandlerRule (errorCodes)** | Hata koduna gore filtreleme | ignore-test-state: errorCodes:["*"] |
| Priority siralama | Dusuk priority once calisir | task:10, state:10, workflow:100 |
| Hata sonrasi devam | Ignore sonrasi siradaki task calisir | IgnoreErrorMapping (order:2) |
| Rollback sonrasi zincir | rollback-test-state tamamlaninca log-test-state | auto-to-log-from-rollback |
| Log sonrasi basari finali | log-test-state tamamlaninca completed-state | auto-to-completed-from-log |

### Kullanilan Bilesenler

| Tip | Anahtar | Dosya |
|-----|---------|-------|
| Task | `error-http-task` (type:6) | Tasks/error-boundary/error-http-task.json |
| Task | `error-script-task` (type:7) | Tasks/error-boundary/error-script-task.json |
| CSX | InitErrorTestMapping | Workflows/error-boundary/src/InitErrorTestMapping.csx |
| CSX | ErrorHttpMapping | Workflows/error-boundary/src/ErrorHttpMapping.csx |
| CSX | ThrowErrorMapping | Workflows/error-boundary/src/ThrowErrorMapping.csx |
| CSX | IgnoreErrorMapping | Workflows/error-boundary/src/IgnoreErrorMapping.csx |
| CSX | **RollbackMapping** | Workflows/error-boundary/src/RollbackMapping.csx |
| CSX | LogOnlyMapping | Workflows/error-boundary/src/LogOnlyMapping.csx |
| CSX | **NotifyMapping** | Workflows/error-boundary/src/NotifyMapping.csx |
| CSX | AlwaysTrueRule | Workflows/error-boundary/src/AlwaysTrueRule.csx |

**Workflow tag'leri (ozet):** `rollback`, `log-action`, `notify`, `notify-action`, `timeout-policy`, `error-types`, `error-codes` dahil; tam liste `error-boundary-test-workflow.json` icindedir.

---

## Grup 5: View, Function & Extension

**Workflow:** `view-function-extension-test-workflow` (type: F)

**Amac:** View tipleri, display modlari, function cagrilari, extension mekanizmasi, wizard state'i ve **features** referansini test eder.

### Agac Yapisi

```
view-function-extension-test-workflow (F)
│
├── [startTransition] start-view-function-extension-test
│   └── onExecutionTasks:
│       └── vfe-script-task + InitVfeMapping.csx
│
├── [functions]
│   ├── single-task-function (scope: Instance)
│   │   └── task: vfe-script-task + FunctionSingleTaskMapping.csx
│   └── multi-task-function (scope: Instance)
│       ├── onExecutionTasks:
│       │   ├── order:1 vfe-script-task + FunctionMultiTask1Mapping.csx
│       │   └── order:2 vfe-http-task + FunctionMultiTask2Mapping.csx
│       └── output: FunctionOutputMapping.csx (IOutputHandler)
│
├── [features]
│   └── global-extension (sys-extensions referansi — features array'inde)
│
├── [extensions]
│   ├── global-extension (type:1 Global, scope:3 Everywhere)
│   │   └── task: vfe-script-task + GlobalExtensionMapping.csx
│   └── requested-extension (type:4 DefinedFlowAndRequested, scope:1 GetInstance)
│       └── task: vfe-script-task + RequestedExtensionMapping.csx
│
├── view-test-state (Initial, stateType:1)
│   ├── view: json-view (type:1 JSON, display: full-page)
│   └── Transitions:
│       ├── auto-to-multi-view (Auto, triggerType:1) → multi-view-state
│       │   └── rule: WebPlatformRule.csx (IConditionMapping, platform kontrolu)
│       └── default-auto-fallback (Auto, triggerType:1, triggerKind:10) → completed-state
│           └── rule: AlwaysTrueRule.csx (fallback)
│
├── multi-view-state (Intermediate, stateType:2)
│   ├── view: html-view (type:2 HTML, display: popup)
│   └── Transitions:
│       └── manual-to-wizard (Manual) → wizard-state
│
├── wizard-state (Wizard, stateType:5)
│   └── Transitions:
│       └── complete-with-markdown-view (Manual) → completed-state
│           └── view: markdown-view (type:3 Markdown, display: bottom-sheet)
│
└── completed-state (Final/Success, stateType:3, subType:1)
```

### Test Edilen Ozellikler

| Ozellik | Nasil Test Edilir | Ilgili Eleman |
|---------|-------------------|---------------|
| JSON View (type:1) | State'e JSON view baglama | json-view → view-test-state |
| HTML View (type:2) | State'e HTML view baglama | html-view → multi-view-state |
| Markdown View (type:3) | Transition'a view baglama | markdown-view → complete-with-markdown-view |
| Display modlari | full-page, popup, bottom-sheet | 3 farkli view |
| Wizard State (stateType:5) | En fazla 1 transition kisitlamasi | wizard-state |
| Transition view | Transition uzerinde view gosterimi | complete-with-markdown-view |
| IConditionMapping (transition rule) | Auto transition'da rule kullanimi | WebPlatformRule.csx |
| triggerKind:10 (varsayilan fallback) | Complementary auto transitions | default-auto-fallback |
| Single-task function | Tek task'li function tanimlama | single-task-function |
| Multi-task function | Birden fazla task'li function | multi-task-function |
| IOutputHandler | Function ciktisini birlestirme | FunctionOutputMapping.csx |
| Global extension (type:1) | Tum isteklerde calisan extension | global-extension |
| Requested extension (type:4) | Talep edildiginde calisan extension | requested-extension |
| Extension scope | Everywhere vs GetInstance | scope:3 vs scope:1 |
| **Features referansi** | features array'inde extension referansi | features: global-extension |

### Kullanilan Bilesenler

| Tip | Anahtar | Dosya |
|-----|---------|-------|
| Task | `vfe-script-task` (type:7) | Tasks/view-function-extension/vfe-script-task.json |
| Task | `vfe-http-task` (type:6) | Tasks/view-function-extension/vfe-http-task.json |
| View | `json-view` | Views/view-function-extension/json-view.json |
| View | `html-view` | Views/view-function-extension/html-view.json |
| View | `markdown-view` | Views/view-function-extension/markdown-view.json |
| Function | `single-task-function` | Functions/view-function-extension/single-task-function.json |
| Function | `multi-task-function` | Functions/view-function-extension/multi-task-function.json |
| Extension | `global-extension` | Extensions/view-function-extension/global-extension.json |
| Extension | `requested-extension` | Extensions/view-function-extension/requested-extension.json |
| CSX | InitVfeMapping | Workflows/view-function-extension/src/InitVfeMapping.csx |
| CSX | WebPlatformRule | Workflows/view-function-extension/src/WebPlatformRule.csx |
| CSX | AlwaysTrueRule | Workflows/view-function-extension/src/AlwaysTrueRule.csx |
| CSX | FunctionSingleTaskMapping | Functions/view-function-extension/src/FunctionSingleTaskMapping.csx |
| CSX | FunctionMultiTask1Mapping | Functions/view-function-extension/src/FunctionMultiTask1Mapping.csx |
| CSX | FunctionMultiTask2Mapping | Functions/view-function-extension/src/FunctionMultiTask2Mapping.csx |
| CSX | FunctionOutputMapping | Functions/view-function-extension/src/FunctionOutputMapping.csx |
| CSX | GlobalExtensionMapping | Extensions/view-function-extension/src/GlobalExtensionMapping.csx |
| CSX | RequestedExtensionMapping | Extensions/view-function-extension/src/RequestedExtensionMapping.csx |

---

## Grup 6: Schema & Data Management

**Workflow:** `schema-data-test-workflow` (type: F)

**Amac:** Master schema, transition schema validasyonu, updateData ($self) mekanizmasi ve field roles ozelliklerini test eder.

**Note (field roles vs. transition roles):** Here **field roles** are **field visibility** on the master schema (e.g. role-based field filtering in `GET .../functions/data`). The transition **`roles`** field on **`lifecycle-transitions`** in **Group 1** is the **`transitions[]` listing filter for `GET .../functions/state`**; the two mechanisms differ.

### Agac Yapisi

```
schema-data-test-workflow (F)
│
├── [startTransition] start-schema-test
│   └── onExecutionTasks:
│       └── schema-data-script-task + SchemaInitMapping.csx
│
├── [workflow-level schema]
│   └── schema-data-master (master schema)
│       ├── required: orderId, customerName, amount, currency
│       ├── enum: currency (TRY, USD, EUR)
│       └── roles: internalNote (admin:allow, customer:deny), auditLog (auditor:allow)
│
├── [updateData]
│   └── update-instance-data (Manual, target: $self)
│
├── data-initialized (Initial, stateType:1)
│   └── Transitions:
│       ├── confirm-with-schema (Manual) → schema-validated-state
│       │   ├── schema: schema-data-confirm-transition
│       │   │   └── required: confirmed (boolean), confirmedBy (string)
│       │   └── onExecutionTasks:
│       │       └── schema-data-script-task + ConfirmMapping.csx
│       └── skip-to-no-schema (Manual) → no-schema-state
│
├── schema-validated-state (Intermediate, stateType:2)
│   └── Transitions:
│       └── to-no-schema (Manual) → no-schema-state
│
├── no-schema-state (Intermediate, stateType:2)
│   └── Transitions:
│       └── complete-schema-test (Manual) → completed-state
│
└── completed-state (Final/Success, stateType:3, subType:1)
```

### Test Edilen Ozellikler

| Ozellik | Nasil Test Edilir | Ilgili Eleman |
|---------|-------------------|---------------|
| Master schema (workflow-level) | Workflow attributes.schema ile tanimlama | schema-data-master |
| Transition schema | Transition'a schema baglama | schema-data-confirm-transition |
| Schema validasyonu | Gecerli data ile transition | confirm-with-schema |
| Schema sessiz reddi | Gecersiz data gonderildiginde state degismez | HTTP test: eksik required alan gonderme |
| updateData ($self) | State degistirmeden data guncelleme | update-instance-data |
| Field roles (master schema alan gorunurlugu) | Alan bazli data gorunurlugu | internalNote (admin/customer), auditLog (auditor) |
| JSON Schema Draft 2020-12 | Schema standardi uyumu | Her iki schema dosyasi |
| Required alan kontrolu | Zorunlu alanlarin validasyonu | orderId, customerName, amount, currency |
| Enum validation | Allowed value-set check | currency: TRY, USD, EUR |
| ETag | Instance data versiyonlama (HTTP test) | HTTP test dosyasinda |

### Kullanilan Bilesenler

| Tip | Anahtar | Dosya |
|-----|---------|-------|
| Task | `schema-data-script-task` (type:7) | Tasks/schema-data/schema-data-script-task.json |
| Schema | `schema-data-master` | Schemas/schema-data/schema-data-master.json |
| Schema | `schema-data-confirm-transition` | Schemas/schema-data/schema-data-confirm-transition.json |
| CSX | SchemaInitMapping | Workflows/schema-data/src/SchemaInitMapping.csx |
| CSX | ConfirmMapping | Workflows/schema-data/src/ConfirmMapping.csx |

---

## Grup 7: Instance Management

**Workflow:** `instance-management-test-workflow` (type: F)

**Amac:** Instance filtreleme, sayfalama, siralama, workflow timeout, idempotent start mekanizmalarini ve **state subType 4 (Temporarily suspended), 5 (Busy), 6 (Human)** ozelliklerini test eder.

### Agac Yapisi

```
instance-management-test-workflow (F)
│
├── [startTransition] start-instance-management-test
│   └── onExecutionTasks:
│       └── instance-mgmt-script-task + InitInstanceMgmtMapping.csx
│
├── [timeout]
│   └── workflow-timeout → timeout-state
│       └── timer: duration PT120S (2 dakika), reset: false
│
├── active-state (Initial, stateType:1)
│   └── Transitions:
│       ├── process (Manual) → processing-state
│       └── fast-complete (Manual) → completed-state
│
├── processing-state (Intermediate, stateType:2)
│   └── Transitions:
│       ├── finish (Manual) → completed-state
│       ├── reject (Manual) → rejected-state
│       ├── suspend (Manual) → suspended-state
│       ├── set-busy (Manual) → busy-state
│       └── assign-human (Manual) → human-state
│
├── completed-state (Final/Success, stateType:3, subType:1)
├── rejected-state (Final/Error, stateType:3, subType:2)
├── timeout-state (Final/Terminated, stateType:3, subType:3)
├── suspended-state (Final/Suspended, stateType:3, subType:4)
├── busy-state (Final/Busy, stateType:3, subType:5)
└── human-state (Final/Human, stateType:3, subType:6)
```

### Test Edilen Ozellikler

| Ozellik | Nasil Test Edilir | Ilgili Eleman |
|---------|-------------------|---------------|
| Workflow timeout | PT120S sonunda otomatik timeout-state | attributes.timeout |
| Timeout timer | Suresiz timeout ve reset:false | timer yapisi |
| Idempotent start | Ayni key ile tekrar baslatma | HTTP test: ayni key ile 2 kez POST |
| Instance filtreleme | API uzerinden filtre sorgusu | HTTP test: filter=attributes.category eq 'finance' |
| Instance sayfalama | page ve pageSize parametreleri | HTTP test: page=1&pageSize=2 |
| Instance siralama | sort parametresi | HTTP test: sort=-createdAt |
| Effective state | State bazli filtreleme | HTTP test: filter=state eq 'active-state' |
| Status filtreleme | Instance durumuna gore filtreleme | HTTP test: filter=status eq 'A' |
| Coklu instance senaryosu | Farkli category/priority ile birden fazla instance | HTTP test: 3 farkli instance |
| **subType 4 (Temporarily suspended)** | Final state ile suspended alt tipi | suspended-state |
| **subType 5 (Busy)** | Final state ile busy alt tipi | busy-state |
| **subType 6 (Human)** | Final state ile human alt tipi | human-state |
| Manuel transition cesitleri | Ayni state'ten farkli hedeflere gecis | process, fast-complete, finish, reject, suspend, set-busy, assign-human |

### Kullanilan Bilesenler

| Tip | Anahtar | Dosya |
|-----|---------|-------|
| Task | `instance-mgmt-script-task` (type:7) | Tasks/instance-management/instance-mgmt-script-task.json |
| CSX | InitInstanceMgmtMapping | Workflows/instance-management/src/InitInstanceMgmtMapping.csx |

---

## Grup 8: Flow Types (Core ve SubProcess)

**Workflow'lar:**
- `core-flow-test` (type: C) - Core flow tipi
- `subprocess-flow-test` (type: P) - SubProcess flow tipi
- `flow-flow-test` (type: F) - Flow tipi
- `subflow-flow-test` (type: S) - SubFlow tipi

**Amac:** vNext'in destekledigi 4 farkli workflow tipinden (C, F, S, P) her birinin runtime tarafindan kabul edilip calistirilabildigini dogrular. F ve S tipleri diger gruplarda kapsamli test edilir; burada yalnizca minimal smoke dogrulamasi yapilir.

### Agac Yapisi

```
core-flow-test (C)
│
├── [startTransition] start-core-flow → core-init-state
│   └── onExecutionTasks:
│       └── flow-types-script-task + CoreInitMapping.csx
│
├── core-init-state (Initial, stateType:1)
│   ├── onEntries:
│   │   └── flow-types-script-task + CoreInitMapping.csx
│   └── Transitions:
│       └── auto-to-core-completed (Auto) → core-completed
│           └── rule: AlwaysTrueRule.csx
│
└── core-completed (Final/Success, stateType:3, subType:1)

subprocess-flow-test (P)
│
├── [startTransition] start-subprocess-flow → subprocess-init-state
│   └── onExecutionTasks:
│       └── flow-types-script-task + SubProcessInitMapping.csx
│
├── subprocess-init-state (Initial, stateType:1)
│   ├── onEntries:
│   │   └── flow-types-script-task + SubProcessInitMapping.csx
│   └── Transitions:
│       └── auto-to-subprocess-completed (Auto) → subprocess-completed
│           └── rule: AlwaysTrueRule.csx
│
└── subprocess-completed (Final/Success, stateType:3, subType:1)

flow-flow-test (F)
│
├── [startTransition] start-flow-flow-test → flow-init-state
│   └── onExecutionTasks:
│       └── flow-types-script-task + FlowInitMapping.csx
│
├── flow-init-state (Initial, stateType:1)
│   ├── onEntries:
│   │   └── flow-types-script-task + FlowInitMapping.csx
│   └── Transitions:
│       └── auto-flow-to-complete (Auto) → flow-completed
│           └── rule: AlwaysTrueRule.csx
│
└── flow-completed (Final/Success, stateType:3, subType:1)

subflow-flow-test (S)
│
├── [startTransition] start-subflow-flow-test → subflow-init-state
│   └── onExecutionTasks:
│       └── flow-types-script-task + SubFlowInitMapping.csx
│
├── subflow-init-state (Initial, stateType:1)
│   ├── onEntries:
│   │   └── flow-types-script-task + SubFlowInitMapping.csx
│   └── Transitions:
│       └── auto-subflow-to-complete (Auto) → subflow-completed
│           └── rule: AlwaysTrueRule.csx
│
└── subflow-completed (Final/Success, stateType:3, subType:1)
```

### Test Edilen Ozellikler

| Ozellik | Nasil Test Edilir | Ilgili Eleman |
|---------|-------------------|---------------|
| Core flow tipi (type: C) | Minimal workflow publish ve calistirma | core-flow-test |
| SubProcess flow tipi (type: P) | Minimal workflow publish ve calistirma | subprocess-flow-test |
| Flow tipi (type: F) | Minimal workflow publish ve calistirma | flow-flow-test |
| SubFlow tipi (type: S) | Minimal workflow publish ve calistirma | subflow-flow-test |
| Farkli flow tiplerinin runtime uyumlulugu | Tum 4 tip: C, F, S, P (hepsi G8'de minimal smoke; F/S ayrica G1-G7, G2, G9'da kapsamli) | Tum gruplarda |

### Kullanilan Bilesenler

| Tip | Anahtar | Dosya |
|-----|---------|-------|
| Task | `flow-types-script-task` (type:7) | Tasks/flow-types/flow-types-script-task.json |
| CSX | CoreInitMapping | Workflows/flow-types/src/CoreInitMapping.csx |
| CSX | SubProcessInitMapping | Workflows/flow-types/src/SubProcessInitMapping.csx |
| CSX | FlowInitMapping | Workflows/flow-types/src/FlowInitMapping.csx |
| CSX | SubFlowInitMapping | Workflows/flow-types/src/SubFlowInitMapping.csx |
| CSX | AlwaysTrueRule | Workflows/flow-types/src/AlwaysTrueRule.csx |

### HTTP Test Dosyasi (`flow-types-test.http`)

| Test | Kisa Aciklama |
|------|---------------|
| Core flow | Core (C) tipi workflow baslatma ve state kontrolu |
| SubProcess flow | SubProcess (P) tipi workflow baslatma ve state kontrolu |
| Flow | Flow (F) tipi workflow baslatma ve state kontrolu |
| SubFlow | SubFlow (S) tipi workflow baslatma ve state kontrolu |

---

## Grup 9: Version Consistency (Versiyon Tutarliligi)

**Workflow:** `version-consistency-test-workflow` (type: F) — ayni key, iki farkli versiyon (v1.0.0 ve v2.0.0)

**Amac:** Workflow versiyonu degistiginde mevcut instance'larin kendi baslangic versiyonlarinin akisindan devam ettigini dogrular. v2.0.0 publish edildikten sonra bile v1.0.0 uzerinde baslatilmis instance'in v1 yolunu takip etmesini test eder.

**NOT:** Bu test publish surecine bagimli oldugu icin tamamen otomatik calistirilamaz. HTTP test dosyasi kullanicinin sirasiyla v1 ve v2'yi publish etmesini gerektiren adim adim bir rehber niteligindedir.

### Agac Yapisi

```
version-consistency-test-workflow (F, v1.0.0) — 3 state
│
├── [startTransition] start-version-test → init-state
│   └── onExecutionTasks:
│       └── version-consistency-script-task + InitVersionMapping.csx
│
├── init-state (Initial, stateType:1)
│   └── Transitions:
│       └── auto-to-processing (Auto) → processing-state
│           └── rule: AlwaysTrueRule.csx
│
├── processing-state (Intermediate, stateType:2)
│   └── Transitions:
│       └── complete-processing (Manual) → completed-state  ← v1: dogrudan completed
│
└── completed-state (Final/Success, stateType:3, subType:1)
    └── onEntries:
        └── version-consistency-script-task + V1CompletedMapping.csx (v1Completed=true)


version-consistency-test-workflow (F, v2.0.0) — 4 state (ekstra review-state)
│
├── [startTransition] start-version-test → init-state
│   └── onExecutionTasks:
│       └── version-consistency-script-task + InitVersionMapping.csx
│
├── init-state (Initial, stateType:1)
│   └── Transitions:
│       └── auto-to-processing (Auto) → processing-state
│           └── rule: AlwaysTrueRule.csx
│
├── processing-state (Intermediate, stateType:2)
│   └── Transitions:
│       └── complete-processing (Manual) → review-state    ← v2: review'a gider
│
├── review-state (Intermediate, stateType:2)              ← SADECE v2'de VAR
│   ├── onEntries:
│   │   └── version-consistency-script-task + ReviewMapping.csx (reviewExecuted=true)
│   └── Transitions:
│       └── approve-review (Manual) → completed-state
│
└── completed-state (Final/Success, stateType:3, subType:1)
    └── onEntries:
        └── version-consistency-script-task + V2CompletedMapping.csx (v2Completed=true)
```

### Test Senaryosu

| Adim | Islem | Beklenen Sonuc |
|------|-------|----------------|
| 1 | v1.0.0 publish et | Runtime v1 tanimini kaydeder |
| 2 | Instance A baslat (v1 uzerinde) | A: processing-state'te bekler |
| 3 | v2.0.0 publish et (ayni key) | Runtime v2 tanimini kaydeder, A etkilenmez |
| 4 | Instance B baslat (v2 uzerinde) | B: processing-state'te bekler |
| 5 | Instance A: complete-processing | A → completed-state (v1 yolu, review YOK) |
| 6 | Instance B: complete-processing | B → review-state (v2 yolu) |
| 7 | Instance B: approve-review | B → completed-state |

### Dogrulama Kriterleri

| Instance | Son State | Data Bayraklari |
|----------|-----------|-----------------|
| A (v1.0.0) | completed-state | `v1Completed=true`, `completedByVersion="1.0.0"`, `reviewExecuted` **YOK** |
| B (v2.0.0) | completed-state | `reviewExecuted=true`, `v2Completed=true`, `completedByVersion="2.0.0"`, `v1Completed` **YOK** |

### Test Edilen Ozellikler

| Ozellik | Nasil Test Edilir | Ilgili Eleman |
|---------|-------------------|---------------|
| Version isolation | v2 publish sonrasi v1 instance v1 akisinda kalir | Instance A vs Instance B |
| Ayni key farkli versiyon | Ayni workflow key ile 2 farkli versiyon publish | v1.0.0 + v2.0.0 JSON dosyalari |
| Yeni instance en son versiyonu kullanir | v2 sonrasi baslatilan instance v2 akisinda gider | Instance B → review-state |
| Eski instance eski versiyonda kalir | v1 instance v2 state'lerine ugramaz | Instance A → completed (review YOK) |
| Versiyon bazli data kaniti | Her versiyonun mapping'leri farkli bayraklar set eder | v1Completed vs v2Completed, reviewExecuted |

### Kullanilan Bilesenler

| Tip | Anahtar | Dosya |
|-----|---------|-------|
| Task | `version-consistency-script-task` (type:7) | Tasks/version-consistency/version-consistency-script-task.json |
| Workflow | `version-consistency-test-workflow` (v1.0.0) | Workflows/version-consistency/version-consistency-test-workflow.json |
| Workflow | `version-consistency-test-workflow` (v2.0.0) | Workflows/version-consistency/version-consistency-test-workflow-v2.json |
| CSX | InitVersionMapping | Workflows/version-consistency/src/InitVersionMapping.csx |
| CSX | AlwaysTrueRule | Workflows/version-consistency/src/AlwaysTrueRule.csx |
| CSX | V1CompletedMapping | Workflows/version-consistency/src/V1CompletedMapping.csx |
| CSX | ReviewMapping | Workflows/version-consistency/src/ReviewMapping.csx |
| CSX | V2CompletedMapping | Workflows/version-consistency/src/V2CompletedMapping.csx |

### HTTP Test Dosyasi (`version-consistency-test.http`)

| Adim | Kisa Aciklama |
|------|---------------|
| ADIM 1 | v1.0.0 publish (manuel) |
| ADIM 2-3 | Instance A baslat, state kontrol (processing) |
| ADIM 4 | v2.0.0 publish (manuel) |
| ADIM 5-6 | Instance B baslat, state kontrol (processing) |
| ADIM 7 | Instance A complete → completed (v1 yolu) |
| ADIM 8 | Instance B complete → review (v2 yolu) |
| ADIM 9 | Instance B approve → completed |
| ADIM 10 | Data dogrulama |

---

## Grup 10: Dynamic Collection & Object

**Workflow:** `collection-object-test-workflow` (type: F)

**Amac:** `ScriptBase` sinifinin sagladigi dinamik koleksiyon ve nesne yardimci metodlarini gercek bir workflow context'inde uctan uca dogrular. Her state bir API grubunu test eder ve `AlwaysTrueRule` ile bir sonraki state'e otomatik gecer.

### Agac Yapisi

```
collection-object-test-workflow (F)
│
├── [startTransition] start-collection-test
│   └── onExecutionTasks:
│       └── script-task + InitCollectionTestMapping.csx
│
├── test-create-and-set-state (Initial, stateType:1)
│   ├── onEntries:
│   │   └── script-task + CreateAndSetMapping.csx
│   │       (CreateObject, CreateList, SetProperty, ListAdd)
│   └── Transitions:
│       └── create-and-set-completed (Auto) → test-get-list-state
│           └── rule: AlwaysTrueRule.csx
│
├── test-get-list-state (Intermediate, stateType:2)
│   ├── onEntries:
│   │   └── script-task + GetListAndAsListMapping.csx
│   │       (GetList, AsList — null/non-list edge case)
│   └── Transitions:
│       └── get-list-completed (Auto) → test-filter-count-any-state
│           └── rule: AlwaysTrueRule.csx
│
├── test-filter-count-any-state (Intermediate, stateType:2)
│   ├── onEntries:
│   │   └── script-task + ListFilterCountAnyMapping.csx
│   │       (ListFilter, ListCount, ListAny — predicate'li/predicate'siz, empty list)
│   └── Transitions:
│       └── filter-count-any-completed (Auto) → test-first-last-state
│           └── rule: AlwaysTrueRule.csx
│
├── test-first-last-state (Intermediate, stateType:2)
│   ├── onEntries:
│   │   └── script-task + ListFirstLastMapping.csx
│   │       (ListFirst, ListLast — predicate, no-match → null, empty list)
│   └── Transitions:
│       └── first-last-completed (Auto) → test-list-select-state
│           └── rule: AlwaysTrueRule.csx
│
├── test-list-select-state (Intermediate, stateType:2)
│   ├── onEntries:
│   │   └── script-task + ListSelectMapping.csx
│   │       (ListSelect<string>, ListSelect<int>, transformation, empty list)
│   └── Transitions:
│       └── list-select-completed (Auto) → test-add-remove-state
│           └── rule: AlwaysTrueRule.csx
│
├── test-add-remove-state (Intermediate, stateType:2)
│   ├── onEntries:
│   │   └── script-task + ListAddRemoveMapping.csx
│   │       (ListAdd, ListRemove — predicate ile sayim, count dogrulama)
│   └── Transitions:
│       └── add-remove-completed (Auto) → test-remove-prop-to-dict-state
│           └── rule: AlwaysTrueRule.csx
│
├── test-remove-prop-to-dict-state (Intermediate, stateType:2)
│   ├── onEntries:
│   │   └── script-task + RemovePropertyToDictionaryMapping.csx
│   │       (RemoveProperty, ToDictionary, HasProperty — null edge case)
│   └── Transitions:
│       └── remove-prop-to-dict-completed (Auto) → test-completed-state
│           └── rule: AlwaysTrueRule.csx
│
└── test-completed-state (Final/Success, stateType:3, subType:1)
```

### Test Edilen Ozellikler

| Ozellik | Nasil Test Edilir | Ilgili Eleman |
|---------|-------------------|---------------|
| CreateObject() | Yeni dinamik nesne olusturma | CreateAndSetMapping.csx |
| CreateList() | Bos dinamik liste olusturma | CreateAndSetMapping.csx |
| SetProperty(obj, key, val) | Dinamik nesneye property atama | CreateAndSetMapping.csx |
| ListAdd(list, item) | Listeye eleman ekleme | CreateAndSetMapping.csx, ListAddRemoveMapping.csx |
| GetList(obj, propName) | Instance data'dan liste alma | GetListAndAsListMapping.csx |
| AsList(value) | Degeri listeye donusturme (null/non-list edge case) | GetListAndAsListMapping.csx |
| ListFilter(list, predicate) | Kosul ile filtreleme | ListFilterCountAnyMapping.csx |
| ListCount(list) / ListCount(list, predicate) | Predicate'li/predicate'siz sayim | ListFilterCountAnyMapping.csx |
| ListAny(list) / ListAny(list, predicate) | Eleman varlik kontrolu | ListFilterCountAnyMapping.csx |
| ListFirst(list) / ListFirst(list, predicate) | Ilk eleman (no-match → null) | ListFirstLastMapping.csx |
| ListLast(list) / ListLast(list, predicate) | Son eleman (no-match → null) | ListFirstLastMapping.csx |
| ListSelect<T>(list, selector) | Projeksiyon: string, int, transformation | ListSelectMapping.csx |
| ListRemove(list, predicate) | Kosul ile eleman silme | ListAddRemoveMapping.csx |
| RemoveProperty(obj, key) | Property silme (non-existent → false) | RemovePropertyToDictionaryMapping.csx |
| ToDictionary(obj) | ExpandoObject → Dictionary (null → empty) | RemovePropertyToDictionaryMapping.csx |
| HasProperty(obj, key) | Property varlik kontrolu (ScriptBase) | RemovePropertyToDictionaryMapping.csx |
| Empty list / null edge cases | Bos liste ve null ile graceful degradation | GetListAndAsListMapping, ListFilterCountAnyMapping, ListFirstLastMapping, ListSelectMapping |

### Kullanilan Bilesenler

| Tip | Anahtar | Dosya |
|-----|---------|-------|
| Task | `script-task` (paylasimli, type:7) | Tasks/lifecycle-transitions/script-task.json |
| CSX | InitCollectionTestMapping | Workflows/collection-object-test/src/InitCollectionTestMapping.csx |
| CSX | CreateAndSetMapping | Workflows/collection-object-test/src/CreateAndSetMapping.csx |
| CSX | GetListAndAsListMapping | Workflows/collection-object-test/src/GetListAndAsListMapping.csx |
| CSX | ListFilterCountAnyMapping | Workflows/collection-object-test/src/ListFilterCountAnyMapping.csx |
| CSX | ListFirstLastMapping | Workflows/collection-object-test/src/ListFirstLastMapping.csx |
| CSX | ListSelectMapping | Workflows/collection-object-test/src/ListSelectMapping.csx |
| CSX | ListAddRemoveMapping | Workflows/collection-object-test/src/ListAddRemoveMapping.csx |
| CSX | RemovePropertyToDictionaryMapping | Workflows/collection-object-test/src/RemovePropertyToDictionaryMapping.csx |
| CSX | AlwaysTrueRule | Workflows/collection-object-test/src/AlwaysTrueRule.csx |

**HTTP / C# integration test:** Henuz eklenmedi.

---

## Altyapi Bilesenleri

### Docker Compose

`docker-compose.yml` dosyasi asagidaki servisleri tanimlar:

| Servis | Amac | Port / Not |
|--------|------|--------------|
| `core-mocklab` | Mock API sunucusu (HTTP task testleri icin) | 3002:5000 |
| `core-mocklab-dapr` | Mocklab Dapr sidecar | `daprd` komutunda **`--resources-path /etc/dapr/components`** ile bilesen YAML'lari yuklenir; volume: `./etc/dapr:/etc/dapr` |

**Dapr bilesen dosyalari:**

| Dosya | Amac |
|-------|------|
| `etc/dapr/components/test-binding.yaml` | (Su an **kullanilmiyor**; Dapr Binding test kapsamindan cikarildi — bkz. **Test Edilmeyen Ozellikler**) |
| `etc/dapr/components/test-pubsub.yaml` | (Su an **kullanilmiyor**; mockoon-dapr sidecar'inda mount edilse de execution-app sidecar'i yalnizca runtime'in yukledigi sistem pubsub bilesenlerini gorur. `extended-tasks-test-workflow` Dapr PubSub task'i Vault'taki `DaprPubSubName` (varsayilan: `vnext-pubsub`) bileseni uzerinden publish eder; `DaprPubSubMapping.csx` `InputHandler`'da `SetPubSubName` ile override edilir.) |
| `etc/dapr/config.yaml` | Dapr genel konfigurasyon |

### Mockoon Mock Endpoint'leri

`etc/docker/config/seed/integration-test-collection.json` dosyasinda tanimlanan endpoint'ler:

| Method | Endpoint | Durum | Amac |
|--------|----------|-------|------|
| POST | `/api/test/process` | 200 | HTTP task testi (Grup 3) |
| POST | `/api/test/validate` | 200/400 | Kural bazli validasyon testi |
| GET | `/api/test/user-info` | 200 | Function/extension HTTP task testi (Grup 5) |
| POST | `/api/test/error-endpoint` | 500 | Error boundary testi (Grup 4) |
| POST | `/api/test/slow-endpoint` | 200 (5s delay) | Timeout testi |
| POST | `/api/test/notification` | 200 | Bildirim testi |

---

## CSX Interface Rehberi

Integration testlerde kullanilan C# script interface'leri:

| Interface | Kullanim Alani | Metodlar |
|-----------|---------------|----------|
| `ScriptBase, IMapping` | Task mapping (onEntries, onExits, onExecutionTasks), **exit** `onExecutionTasks`, shared transition gorevleri | `InputHandler(WorkflowTask, ScriptContext)`, `OutputHandler(ScriptContext)` |
| `ScriptBase, IConditionMapping` | Auto transition rule, view selection rule | `Handler(ScriptContext)` → `Task<bool>` |
| `ScriptBase, ITimerMapping` | Scheduled transition zamanlama | `Handler(ScriptContext)` → zamanlama bilgisi |
| `ScriptBase, ISubFlowMapping` | SubFlow data aktarimi | `InputHandler(ScriptContext)`, `OutputHandler(ScriptContext)` |
| `ScriptBase, IOutputHandler` | Function cikti birlestirme | `OutputHandler(ScriptContext)` |

### Ortak ScriptBase Yardimci Metodlari

| Metod | Aciklama |
|-------|----------|
| `HasProperty(obj, "propName")` | Dynamic nesnede property var mi kontrol eder |
| `LogInformation($"mesaj")` | Loglama yapar |
| `GetConfigValue("Key")` | Reads configuration value from Vault |

---

## Ozellik Kapsam Matrisi

Her grubun hangi vNext ozelliklerini test ettigini gosteren matris. **Gn** sutunu Genel Bakis tablosundaki **Grup n** ile eslesir (1-10; birlestirilmis Dapr senaryosu **G3**).

| Ozellik | G1 | G2 | G3 | G4 | G5 | G6 | G7 | G8 | G9 | G10 |
|---------|----|----|----|----|----|----|----|----|----|----|
| State tipleri (1/2/3) | X | X | X | X | X | X | X | X | X | X |
| State alt tipleri (1/2/3) | X | X |  | X |  |  | X |  |  | X |
| **State alt tipleri (4/5/6)** |  |  |  |  |  |  | X |  |  |  |
| SubFlow state (4) |  | X |  |  |  |  |  |  |  |  |
| Wizard state (5) |  |  |  |  | X |  |  |  |  |  |
| Manuel transition (0) | X | X |  |  | X | X | X |  | X |  |
| Otomatik transition (1) | X | X | X | X | X |  |  | X | X | X |
| Zamanlanmis transition (2) | X |  |  |  |  |  |  |  |  |  |
| triggerKind:10 (default) | X |  |  |  | X |  |  |  |  |  |
| onEntries | X | X | X | X |  |  |  | X | X | X |
| onExits | X |  |  |  |  |  |  |  |  |  |
| startTransition tasks | X | X | X | X | X | X | X | X | X | X |
| IMapping | X | X | X | X | X | X | X | X | X | X |
| IConditionMapping | X | X | X | X | X |  |  | X | X | X |
| ITimerMapping | X |  |  |  |  |  |  |  |  |  |
| ITransitionMapping | X |  |  |  |  |  |  |  |  |  |
| ISubFlowMapping |  | X |  |  |  |  |  |  |  |  |
| IOutputHandler |  |  |  |  | X |  |  |  |  |  |
| HTTP Task (type:6) |  |  | X | X |  |  |  |  |  |  |
| Script Task (type:7) | X | X | X | X | X | X | X | X | X | X |
| StartFlow Task (11) |  |  | X |  |  |  |  |  |  |  |
| GetInstanceData Task (13) |  |  | X |  |  |  |  |  |  |  |
| **Dapr HTTP Task (1)** |  |  | X |  |  |  |  |  |  |  |
| **Dapr Service Task (3)** |  |  | X |  |  |  |  |  |  |  |
| **Dapr PubSub Task (4)** |  |  | X |  |  |  |  |  |  |  |
| **Notification Task (10)** |  |  | X |  |  |  |  |  |  |  |
| **Trigger Transition Task (12)** |  |  | X |  |  |  |  |  |  |  |
| **SubProcess Task (14)** |  |  | X |  |  |  |  |  |  |  |
| **Get Instances Task (15)** |  |  | X |  |  |  |  |  |  |  |
| Cancel transition | X | X |  |  |  |  |  |  |  |  |
| **Exit transition (`attributes.exit`)** | X |  |  |  |  |  |  |  |  |  |
| **Schedule cancel (manuel)** | X |  |  |  |  |  |  |  |  |  |
| **Timer reschedule (self-loop)** | X |  |  |  |  |  |  |  |  |  |
| Shared transitions ($self) |  | X |  |  |  |  |  |  |  |  |
| **Child-level shared transition** |  | X |  |  |  |  |  |  |  |  |
| **SubFlow cancel final states (child/grandchild)** |  | X |  |  |  |  |  |  |  |  |
| **Effective state (/functions/state, nested)** |  | X |  |  |  |  |  |  |  |  |
| updateData ($self) |  | X |  |  |  | X |  |  |  |  |
| **UpdateData (SubFlow context)** |  | X |  |  |  |  |  |  |  |  |
| Master schema |  |  |  |  |  | X |  |  |  |  |
| Transition schema |  |  |  |  |  | X |  |  |  |  |
| Field roles (master schema) |  |  |  |  |  | X |  |  |  |  |
| **queryRoles (workflow/state)** | X |  |  |  |  |  |  |  |  |  |
| **Transition roles (state fn. list filter)** | X |  |  |  |  |  |  |  |  |  |
| Error boundary (task) |  |  |  | X |  |  |  |  |  |  |
| Error boundary (state) |  |  |  | X |  |  |  |  |  |  |
| Error boundary (workflow) |  |  |  | X |  |  |  |  |  |  |
| Retry policy |  |  |  | X |  |  |  |  |  |  |
| **Rollback (action:2)** |  |  |  | X |  |  |  |  |  |  |
| **Log action (action:5, state)** |  |  |  | X |  |  |  |  |  |  |
| **Notify action (action:4)** |  |  |  | X |  |  |  |  |  |  |
| **timeoutPolicy (onTimeout)** |  |  |  | X |  |  |  |  |  |  |
| **errorHandlerRule (errorTypes/errorCodes)** |  |  |  | X |  |  |  |  |  |  |
| View (JSON/HTML/MD) |  |  |  |  | X |  |  |  |  |  |
| Display modlari |  |  |  |  | X |  |  |  |  |  |
| Function (single/multi) |  |  |  |  | X |  |  |  |  |  |
| Extension (global/req) |  |  |  |  | X |  |  |  |  |  |
| **Features referansi** |  |  |  |  | X |  |  |  |  |  |
| Workflow timeout |  |  |  |  |  |  | X |  |  |  |
| Idempotent start | X |  |  |  |  |  | X |  |  |  |
| Instance filtreleme |  |  | X |  |  |  | X |  |  |  |
| Sayfalama/Siralama |  |  | X |  |  |  | X |  |  |  |
| Hedef workflow yasam dongusu (StartFlow/SubProcess hedefi) |  |  | X |  |  |  |  |  |  |  |
| **Condition Task (tip 8) — auto transition rule** |  |  | X |  |  |  |  |  |  |  |
| **Timer Task (tip 9) — scheduled transition** |  |  | X |  |  |  |  |  |  |  |
| Complementary rules | X |  |  |  | X |  |  |  |  |  |
| **Dapr sidecar + bilesen YAML** |  |  | X |  |  |  |  |  |  |  |
| **Flow tipi: Core (C)** |  |  |  |  |  |  |  | X |  |  |
| **Flow tipi: SubProcess (P)** |  |  |  |  |  |  |  | X |  |  |
| Flow tipi: Flow (F) | X |  | X | X | X | X | X |  | X | X |
| Flow tipi: SubFlow (S) |  | X |  |  |  |  |  |  |  |  |
| **Version isolation (ayni key, farkli versiyon)** |  |  |  |  |  |  |  |  | X |  |
| **Eski instance eski versiyonda kalir** |  |  |  |  |  |  |  |  | X |  |
| **subFlow override timeout** |  | X |  |  |  |  |  |  |  |  |
| **subFlow override queryRoles (state)** |  | X |  |  |  |  |  |  |  |  |
| **subFlow override transition roles** |  | X |  |  |  |  |  |  |  |  |
| **ScriptBase dinamik koleksiyon API'leri** (CreateList, ListAdd, ListRemove, ListFilter, ListCount, ListAny, ListFirst, ListLast, ListSelect) |  |  |  |  |  |  |  |  |  | X |
| **ScriptBase dinamik nesne API'leri** (CreateObject, SetProperty, RemoveProperty, HasProperty, ToDictionary) |  |  |  |  |  |  |  |  |  | X |
| **AsList / GetList edge case** (null, non-list) |  |  |  |  |  |  |  |  |  | X |

---

## NOT: Schema Kisitlamalari

1. **Rule-based view selection**: Mevcut `vnext-schema` versiyonu (0.0.23) state uzerinde sadece tekil `view` (viewDefinition) desteklemektedir. Runtime dokumantasyonunda anlatilan coklu `views` array'i (rule-based view selection) bu schema versiyonunda desteklenmemektedir. Bu nedenle `WebPlatformRule.csx` rule-based view selection yerine transition rule olarak kullanilmistir. Schema guncellendikten sonra bu test, `views` array'i ile yeniden yapilandirilabilir.

2. **platformOverrides**: `view-definition.schema.json` icinde `platformOverrides` alani tanimli degildir. Bu nedenle platform bazli view override testleri mevcut schema ile yapilamaz.

3. **Event transition (triggerType:3)**: Runtime dokumantasyonunda tanimlanan event-driven transition'lar icin test eklenmemistir. Bu, ileri asamada ayri bir senaryoda test edilebilir.

---

## Test Edilmeyen Ozellikler

Bu bolum, runtime tarafindan desteklenen ancak `vnext-example` integration test workflow'lari icinde **bilincli olarak test edilmeyen** ozellikleri listeler. Her madde icin neden test edilmediginin gerekcesi ve ileride etkin test eklenmek istenirse hangi yolun izlenecegine dair kisa bir not bulunur.

### 1. Dapr Binding Task (tip 2)

- **Onceki konum:** Grup 3 → `extended-tasks-test-workflow` icindeki `dapr-binding-state` (state, task ve mapping artik kaldirildi).
- **Kaldirilan dosyalar:**
  - State: `core/Workflows/task-execution/extended-tasks-test-workflow.json` icindeki `dapr-binding-state` (ilgili `auto-to-dapr-binding` transition'i ile birlikte). Akis su an `dapr-service-state` → `dapr-pubsub-state` seklinde dogrudan ilerler.
  - Task tanimi: `core/Tasks/task-execution/extended-tasks-test-workflow/dapr-binding-task.json` (silindi).
  - Mapping: `core/Workflows/task-execution/src/extended-tasks-test-workflow/DaprBindingMapping.csx` (silindi).
- **Korunan altyapi:** `etc/dapr/components/test-binding.yaml` Dapr bilesen tanimi dosyada **birakilmistir** ancak workflow tarafindan **artik referans edilmemektedir**; ileride yeniden test eklenmek istendiginde dogrudan kullanilabilir.
- **Neden test edilmiyor:** Dapr output binding davranisi, runtime'in stabil kapsami disinda kalan bir entegrasyondur ve mevcut integration test ortaminda guvenilir bir mocklab/Dapr kombinasyonu ile uctan uca dogrulanamamaktadir. Diger Dapr task tipleri (HTTP-1, Service-3, PubSub-4) `extended-tasks-test-workflow` icinde test edilmeye devam etmektedir.
- **Eklenmek istenirse:** Yeni bir `dapr-binding-state` + `dapr-binding-task` (tip 2) cifti `extended-tasks-test-workflow` icinde tekrar olusturulmalidir; Dapr tarafinda `test-binding.yaml` zaten hazirdir. Bu degisiklik mevcut `extended-tasks-test-workflow` zincirini ve C# integration test sozlesmesini (`taskResults.daprBinding`) yeniden gunceller.

### 2. Human Task (tip 5)

- **Onceki konum:** Daha onceki revizyonlarda Grup 3 (`task-execution-test-workflow`) Human Task ozelligini de kapsiyordu; bu zincir icindeki `human-task-state` ve `human-task` (tip 5) artik kaldirilmistir.
- **Neden test edilmiyor:** Human Task (tip 5) **runtime tarafindan kaldirilacak gecici bir ozellik** olarak isaretlenmistir. Bu nedenle:
  - Bu ozellik **yeni test workflow'larina eklenmez**.
  - Mevcut testlerde gecmiste kullanildiysa temizlenir (mevcut durumda `task-execution-test-workflow` ve `instance-management-test-workflow` icinde Human Task tipi referansi yoktur; yalnizca `instance-management` workflow'unda **state subType 6 (Human)** — Human Task task-tipi degil — final state alt tipi olarak korunmaktadir).
- **Iliskili ama farkli ozellik:** `instance-management-test-workflow` icindeki `human-state` (subType 6) Human **Task** tipi degil, **state alt tipi**dir. Bu state, runtime'in `subType` enum'unu test eder ve Human Task tip 5'in kaldirilma kararindan **etkilenmez**.
- **Eklenmek istenirse:** Runtime'da Human Task ozelligi kalici olarak destekleneceginin teyidi alindiginda; ayri bir `human-task-test-workflow` ve `human-task` (tip 5) JSON tanimi olusturulup Mocklab veya gercek bir human-task hub uctan uca testle entegre edilmelidir. Su an icin **eklenmemelidir**.

### Ozet Tablo

| Ozellik | Tip / Konum | Durum | Gerekce |
|---------|-------------|-------|---------|
| Dapr Binding Task | Task tipi 2, `extended-tasks-test-workflow` | **Kaldirildi (state + task + mapping silindi)** | Runtime kapsamindaki kararsizlik; mocklab/Dapr binding entegrasyonu uctan uca dogrulanamiyor |
| Human Task | Task tipi 5, daha once `task-execution-test-workflow` zincirinde | **Kaldirildi / yeni testlere eklenmez** | Runtime tarafindan kaldirilacak gecici ozellik; sadece state subType 6 (Human) korunur |
