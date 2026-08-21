# FanOutTask — configurable-surface findings

**Subject:** `FanOutTask` (TaskType 21), branch `feature/fanout-task-design` of
`burgan-tech/vnext`, at `ccfcec01` (includes the `CreateParallelBranch` header-typing fix
`9497c7b6` and the `InstanceWriteGate` SubProcess fix `ccfcec01`).
**Scenario:** `fan-out-config-matrix` — `core/Workflows/fan-out-config-matrix/`,
`core/Tasks/fan-out-config-matrix/`,
`tests/Core.IntegrationTests/Tests/FanOut/FanOutConfigMatrixTests.cs`.
**Run:** 2026-08-21, against the locally built runtime on `localhost:4201` (`VNEXT_BASE_URL` set, so
no Testcontainers/image stack was involved).

## Result: 17 tests, 12 passed, 5 failed

| # | Failing test | Category |
|---|---|---|
| F1 | `EarlyStop_CancelledItems_CarryTheDocumentedFanOutItemCancelledCode` | **genuine defect** |
| F2 | `DurableMode_IsRefusedWithAnActionableValidationError` | **genuine defect** |
| E1 | `ItemTimeout_StampsItemTimeoutOnTheStraggler_AndLeavesTheBatchNotTimedOut` | environment |
| E1 | `BatchTimeout_SerialBatch_StampsBatchTimeoutAndMarksTheBatchTimedOut` | environment |
| E1 | `MaxDegreeOfParallelism_RaisedCeiling_LetsEveryItemFinishInsideTheSameBudget` | environment |

Both defects have a dedicated failing test. **Going green on F1/F2 means the defect was fixed** — do
not adjust the assertions to silence them. The three E1 tests are blocked by a broken mock, not by
the runtime, and their guard says so in the failure message.

### What PASSED — the configurable surface that works

All four join policies on both sides of their verdict, `minSuccess`, and the empty-batch rule are
**correct**:

| Verified | Evidence |
|---|---|
| `all` — every item succeeds ⇒ success | `{total 3, succeeded 3, failed 0}` → `case-settled` |
| `all` — one item fails ⇒ FAIL, and the result set still lands | → `case-failed`, summary + rows present |
| `all` — empty batch ⇒ succeeds vacuously | `{0,0,0}` → `case-settled` |
| `allSettled` — empty batch ⇒ succeeds | `{0,0,0}` → `case-settled` |
| `quorum(minSuccess 2)` — 2 of 4 succeed ⇒ success **with failures in it** | `{4,2,2}` → `case-settled` |
| `quorum(minSuccess 2)` — 1 of 3 succeeds ⇒ FAIL | `{3,1,2}` → `case-failed` |
| `quorum` — empty batch ⇒ FAIL (0 cannot clear a threshold) | `{0,0,0}` → `case-failed` |
| `firstSuccess` — one succeeds ⇒ success | `{3,1,2}` → `case-settled` |
| `firstSuccess` — none succeeds ⇒ FAIL | `{2,0,2}` → `case-failed` |
| `firstSuccess` — empty batch ⇒ FAIL, same as `quorum(1)` | `{0,0,0}` → `case-failed` |
| per-item `errorBoundary` `retry` — exhaustion contained to its own item | `{3,2,1}`, both siblings succeeded, → `case-settled` |
| a failed join still carries its data | `TaskInvocationResult.Failure` is correctly avoided; rows survive |

**The join verdict propagates correctly.** An earlier reading of this run suggested it did not; that
was a defect in the SCENARIO, not the runtime — see § Corrected test design below. `join.policy` is
fully load-bearing on flow control.

---

## F1 — early-stop cancelled items leak a raw exception code instead of `FanOut:ItemCancelled`

**Severity: medium.** Public-contract violation; silently breaks author branching.

### Expected

`FanOutErrorCodes.ItemCancelled` (`"FanOut:ItemCancelled"`) is documented as the code for
"the item was cancelled by the join policy's early stop — `firstSuccess` already succeeded, or `all`
already failed, and this item was still running", and `docs/domain/fan-out-task.md` § "Error codes
and branching on partial failure" tells authors to branch on these strings. Every item cancelled by
early stop should carry it.

### Actual

Codes are **inconsistent within a single batch**. Items already in flight when the early stop fires
get the inner task's raw exception name; only an item cancelled before it started gets the
documented code:

```
firstSuccess over 5 succeedable items -> case-settled, summary {total 5, succeeded 1, failed 4}
  DOC-1  isSuccess=True   errorCode=None
  DOC-2  isSuccess=False  errorCode=Task:Unknown:process-document-task:TaskCanceledException
  DOC-3  isSuccess=False  errorCode=Task:Unknown:process-document-task:TaskCanceledException
  DOC-4  isSuccess=False  errorCode=Task:Unknown:process-document-task:TaskCanceledException
  DOC-5  isSuccess=False  errorCode=FanOut:ItemCancelled
```

Note the leaked code also **embeds the task key**, so it is not even a stable string to match on.

### Reproduction

Component `core/Tasks/fan-out-config-matrix/fanout-case-join-first-success-task.json`
(`join.policy: firstSuccess`, `maxDegreeOfParallelism: 4`, inner task `process-document-task`).

```
POST /api/v1/core/workflows/fan-out-config-matrix/instances/start?sync=false
  {"testId":"probe","documents":[{"id":"DOC-1"},{"id":"DOC-2"},{"id":"DOC-3"},{"id":"DOC-4"},{"id":"DOC-5"}]}
PATCH .../instances/{id}/transitions/run-join-first-success?sync=false   {}
GET   .../instances/{id}                 -> read attributes.caseResults[].errorCode
```

Test: `FanOutConfigMatrixTests.EarlyStop_CancelledItems_CarryTheDocumentedFanOutItemCancelledCode`.
Observed instance `bccc762f-44bc-4e06-b645-822ca82223b8`; a later identical run reported
`2 of 3 … did not carry 'FanOut:ItemCancelled'` (the exact split varies with which items are
in flight, the leak does not).

### Assessment

`FanOutBatchCancellation.Classify` produces the right code, but it only gets consulted for an item
whose cancellation is observed by the fan-out layer. An item that had already entered the inner
`HttpTask` surfaces a `TaskCanceledException`, which the generic task pipeline normalizes into
`Task:Unknown:{taskKey}:{exceptionName}` **before** the fan-out classifier can attribute it, so the
fan-out attribution is lost for exactly the items the doc describes ("and this item was still
running"). The fix is to let the item's settle path re-attribute a cancellation to
`Classify(...)` when the batch's early-stop / deadline token is the cause, rather than accepting the
inner task's normalized error verbatim. Relevant code: `FanOutTaskExecutor` lines ~460 and ~632-652
(`engineResult.Error.Code ?? FanOutErrorCodes.ItemFailed`, and
`execution.TaskError?.NormalizedError.Code ?? …`) plus `FanOutBatchCancellation.Classify`.

---

## F2 — `mode: "durable"` is refused at publish, but with an opaque HTTP 500

**Severity: medium.** Answers the previously open question C2.

### Expected

`durable` is reserved and `FanOutTask.Configure` throws
`"FanOutTask mode 'durable' is not supported yet. Only 'inline' is available (Key=…)"`. Publish
should refuse it the way it refuses any other invalid component — a 400 validation problem naming
the offending field, as a bad workflow gets:

```
400 {"detail":"Component validation failed for type 'sys-flows'","errorCode":"validation.App:900006",
     "errors":{"workflow.ErrorBoundary.OnError[0].Transition":["Transition must not be specified when Action is Abort."]}}
```

### Actual

Refused — which is the important half, the reserved mode never becomes a live definition — but as an
unhandled exception with nothing actionable:

```
500 {"type":"https://httpstatuses.com/500/failure/internal_error","title":"Internal Server Error",
     "status":500,"detail":"An internal error occurred during your request!",
     "instance":"/api/v1/definitions/publish","errorCode":"failure.internal_error",
     "traceId":"00-e8e71d69afa7e1b9c2b19aaf849acffe-6e9038dcee9e483f-01"}
```

The author is told only that something broke on the server. The exception message — which already
names the offending mode AND the supported one — is discarded.

### Reproduction

`POST /api/v1/definitions/publish` with a type-21 task whose `config.mode` is `"durable"` (full body
in the test). Use a fresh `version` so a 409 cannot be confused with the rejection.

Test: `FanOutConfigMatrixTests.DurableMode_IsRefusedWithAnActionableValidationError` — part 1
(refused at all) passes; part 2 (refused as 4xx) is the defect.

### Assessment

Task-component `Configure` throws `ArgumentException`, and the publish path evidently wraps
`WorkflowTask` materialisation in a generic exception handler rather than the component-validation
path that produces `App:900006`. This affects **every** task type whose `Configure` validates —
FanOut's `itemsPath` must start with `$.`, `maxDegreeOfParallelism >= 1`,
`itemTimeoutSeconds <= batchTimeoutSeconds`, `quorum` requires `minSuccess >= 1` — so all of those
authoring errors are 500s today. Mapping `ArgumentException` from task construction into the
component-validation result would fix the whole class at once. **Good news for the risk that
motivated C2:** the rejection does happen at publish, so a `durable` component cannot reach
production and fail at first execution.

---

## F3 — per-item `errorBoundary` with `action: ignore` has no observable effect (OPEN QUESTION)

**Severity: low. Semantics undocumented — needs a product decision, not necessarily a code fix.**

### Measured

A wildcard `{"action":"ignore","errorCodes":["*"],"priority":999}` per-item boundary under
`join.policy: all`, over `[DOC-1, DOC-FAIL-A, DOC-3]`:

```
state=case-failed  summary={"total":3,"failed":2,"succeeded":1}
  DOC-1       isSuccess=True   errorCode=None
  DOC-FAIL-A  isSuccess=False  errorCode=FanOut:ItemFailed
  DOC-3       isSuccess=False  errorCode=Task:Unknown:process-document-task:TaskCanceledException
```

The ignored item is still `isSuccess: false`, still counts toward `failed`, still fails the `all`
join, and still triggers the early stop that cancels `DOC-3` — indistinguishable in outcome from
having no boundary at all.

### Why this is filed as a question, not a defect

`docs/domain/fan-out-task.md` documents the per-item boundary only through the retry case ("a
retry-exhausted item becomes one `Failed` entry"). `ErrorAction.Ignore` maps to
`BoundaryActionResult.Continue` / `ShouldContinue`, which is about not propagating an error, not
about fabricating success — so the current behaviour is defensible. But `ignore`/`log` are then
inert for fan-out items, and an author who configures them reasonably expects the item not to sink
an `all` batch. Either the semantics or the documentation should change.

Note the boundary IS consulted per item: with a `retry` rule the failing item's code is
`Task:Http:process-document-task:500` (the inner task's own code passing through), whereas with the
`ignore` rule it is `FanOut:ItemFailed`. So this is not "the boundary is never applied".

Pinned as a **characterization** test —
`PerItemErrorBoundary_Ignore_DoesNotConvertAFailedItemIntoASuccess` — which currently passes and
carries a comment saying to invert it deliberately if the intended semantics turn out to be
"an ignored item counts as successful".

---

## C1 — resolved: `join.minSuccess` ignored for non-`quorum` policies, deliberately NOT pinned

`FanOutTask.Configure` validates `minSuccess` in one direction only (required and `>= 1` for
`quorum`); `{"policy":"firstSuccess","minSuccess":3}` parses, stores 3, and behaves as 1 because
`FanOutJoinEvaluator` reads `minSuccess` only on the `Quorum` arm.

**Decision: do not pin it, and do not add a component for it.** It is documented as intentional
("Ignored (with no warning today) for other policies"), and a test asserting a documented no-op
would cement the trap rather than surface it — the next person to make `minSuccess` meaningful (or
to reject it) would have to delete the test. Recorded here as an authoring hazard so the product
call can be made explicitly; it is the same failure shape the repo treats as worth erroring on for
dynamic role grants, where definition time is the only place it is visible.

---

## E1 — ENVIRONMENT: MockLab is not applying `delayMs`

**Not a FanOutTask defect. Blocks three tests and silently degrades an existing load test.**

`etc/docker/config/seed/fan-out-documents-collection.json` configures the `process-slow` route with
`"delayMs": 1500`. Measured directly against MockLab:

```
POST http://localhost:3001/api/fan-out/documents/process-slow?documentId=DOC-SLOW-A
  attempt 1: 0.046862s  http=200
  attempt 2: 0.022121s  http=200
POST http://localhost:3001/api/fan-out/documents/process?documentId=DOC-1   (fast route)
             0.008272s  http=200
```

~13-46ms instead of 1500ms. Rules DO work on the same collection (`DOC-FAIL*` correctly returns
500), so the collection is imported — the delay specifically is not applied. MockLab exposes no
admin surface to inspect the stored mock (`/__admin/mappings`, `/api/mocks`, `/admin/mocks`,
`/api/collections` all 404), so whether the field was dropped at import or is unimplemented could
not be determined from outside.

### Consequences

1. **`itemTimeoutSeconds` and `batchTimeoutSeconds` cannot be exercised at all.** Both are validated
   as whole seconds `>= 1`, and every mock route answers in ~10ms, so no item can exceed any
   deadline. `FanOut:ItemTimeout` / `FanOut:BatchTimeout` and `summary.timedOut` remain **unverified
   end to end**.
2. **`maxDegreeOfParallelism` cannot be verified either** — with instant items, every ceiling
   produces the same outcome. This one is the trap: that test PASSED vacuously in the first run.
   `AssertStragglerRouteIsActuallySlowAsync` now guards all three so a broken mock cannot be
   mistaken for a passing concurrency proof.
3. **The sibling scenario's load test is affected too.** `api-tests/fan-out-documents/fanout-load.py`
   reports a "straggler ratio" built on `DOC-SLOW` ids; with no delay that metric measures jitter.

### To unblock

Either fix `delayMs` in MockLab (or re-import the collection if it was dropped at import time —
per `CLAUDE.md`, MockLab skips collections whose name already exists, so this needs
`docker compose down -v && docker compose up -d mocklab`), or give the seed a route that is slow by
another mechanism. Once the straggler is genuinely ~1500ms the three tests should run unchanged.

---

## Corrected test design (recorded so the mistake is not repeated)

The first revision of this scenario declared **no** error boundary and treated a **Faulted instance**
as the failed-join signal. That was wrong, and it made all five failed-join cases silently pass.

Control experiment: feeding `documents` a STRING instead of an array makes `FanOutItemsResolver`
throw — a hard `Result<TaskInvocationResult>.Fail`, the strongest failure available — and the
instance still reached `case-settled` with no `caseResults` written at all.

**With no error boundary configured, a failing onEntry task is not acted on; the state's
unconditional auto transition fires regardless.** Absence of a boundary means "the failure is not
acted on", not "the failure faults the instance".

The workflow now declares one global boundary:

```json
"errorBoundary": { "onError": [ { "action": "rollback", "transition": "to-case-failed", "priority": 999 } ] }
```

and every case state carries a `to-case-failed` transition. `action: "abort"` cannot be used with a
transition — the validator rejects it with *"Transition must not be specified when Action is
Abort."* — because abort-without-transition IS the fault path. `rollback` is the transition-carrying
action.

Two further authoring facts learned the same way, both now documented in the builder script:

- **Every `triggerType: 1` transition must carry a `rule`.** "A lone auto transition is valid if its
  rule always returns true" means a rule that returns `true`, not the absence of a rule. Omitting it
  fails publish with 400 and `"Auto transition '…' must have a rule defined."` for every such
  transition. Hence `src/CaseSettledRule.csx`.
- **The start transition must settle before a case transition is fired**, or the runtime answers 409
  (Busy). `RunCaseAsync` waits for non-Busy first.

---

## Verification gaps — what the SDK / environment cannot express

1. **Per-item retry ATTEMPT counts are not observable.** They live in the `InstanceTask` journal row
   keyed `{fanOutTaskKey}#{index}`, reachable only via the monitoring host's
   `GET /api/v1/monitor/.../instances/{id}/tasks` (port 4203), which no test stack starts and the
   SDK does not wrap. MockLab's sequential-response feature is per-mock, not per-item, so it cannot
   express "fail once then succeed" under concurrency either.
   `PerItemErrorBoundary_Retry_ContainsExhaustionToItsOwnItem` therefore asserts containment rather
   than faking an attempt count. Closing this needs an SDK monitoring wrapper or a MockLab
   per-request-key sequential mode.
2. **`WorkflowTestBase` has no "expect a non-success terminal state" waiter.**
   `WaitForInstanceStateAsync` fails fast on a fault — correct elsewhere, wrong here.
   `WaitForTerminalAsync` fills it locally and additionally fails fast when the OPPOSITE terminal
   state is reached, which a plain state waiter would report as a timeout. Worth promoting to
   `WorkflowTestBase` if a third suite needs it.
3. **`npm run validate` rejects every `attributes.type: "21"` component.**
   `@burgan-tech/vnext-schema` 0.0.52 caps the task-type enum at 20; the run fails on exactly ten
   files — the nine new ones plus the pre-existing `fan-out-documents-task.json`. Publish bypasses
   schema validation (all nine published fine, confirmed by 409 "already exists" on re-publish), so
   the runtime path is unaffected. Not worked around.

---

## Environment reference for this run

| Thing | Value |
|---|---|
| Orchestration / Execution | `localhost:4201` / `4202`, both `/health` → 200 |
| Runtime commit | `ccfcec01`, branch `feature/fanout-task-design` |
| Hosts | 4 × `--launch-profile http` (12 processes incl. sidecar children) |
| MockLab | `localhost:3001`; `DOC-*` → 200, `DOC-FAIL*` → 500, `DOC-SLOW*` → 200 **without** its delay |
| Elasticsearch | `localhost:9200`, healthy. Note: a `trace.id` term query on `logs-apm.app.vnext_app-default*` returned 0 hits for these traces, so app logs were not usable as evidence here; all evidence above is from the API surface |
| Test command | `dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~FanOutConfigMatrix"` |
