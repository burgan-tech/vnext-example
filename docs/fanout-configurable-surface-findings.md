# FanOutTask — configurable-surface findings

**Subject:** `FanOutTask` (TaskType 21), branch `feature/fanout-task-design` of
`burgan-tech/vnext`, at `ccfcec01` (includes the `CreateParallelBranch` header-typing fix
`9497c7b6` and the `InstanceWriteGate` SubProcess fix `ccfcec01`).
**Scenario:** `fan-out-config-matrix` — `core/Workflows/fan-out-config-matrix/`,
`core/Tasks/fan-out-config-matrix/`,
`tests/Core.IntegrationTests/Tests/FanOut/FanOutConfigMatrixTests.cs`.
**Run:** 2026-08-21, against the locally built runtime on `localhost:4201` (`VNEXT_BASE_URL` set, so
no Testcontainers/image stack was involved).

## Result: 17 tests, 14 passed, 3 failed

`dotnet test --filter "FullyQualifiedName~FanOut"` overall: **21 tests, 18 passed, 3 failed** —
`FanOutConfigMatrixTests` 17 (14/3) plus the sibling `FanOutDocumentsTests` 4 (4/0, no regression
from the mapping change and workflow bump).

| # | Failing test | Category |
|---|---|---|
| F1 | `ItemTimeout_StampsItemTimeoutOnTheStraggler_AndLeavesTheBatchNotTimedOut` | **genuine defect** |
| F1 | `EarlyStop_CancelledItems_CarryTheDocumentedFanOutItemCancelledCode` | **genuine defect** |
| F2 | `DurableMode_IsRefusedWithAnActionableValidationError` | **genuine defect** |

Every failure is now a filed defect with a dedicated test; there are **no remaining environment
blocks**. **Going green means the defect was fixed** — do not adjust the assertions to silence them.

At the time of writing, a separate agent is fixing F1 and F2 in the runtime, and the four hosts are
still serving the PRE-fix build, so these three are expected red until the hosts are rebuilt.

### Run history

| Run | Result | What changed |
|---|---|---|
| 1 | 16 tests, 0 passed | Workflow was never published (auto transitions need a `rule`; `abort` cannot carry a transition) |
| 2 | 16 tests, 7 passed, 9 failed | Published. Fault-based observation of a failed join was wrong — see § Corrected test design |
| 3 | 17 tests, 12 passed, 5 failed | Global `rollback` boundary → `case-failed`. 2 defects + 3 blocked by E1 |
| 4 | **17 tests, 14 passed, 3 failed** | E1 fixed (prefix-shadowed mock route). `BatchTimeout` and `mdop` now pass **for real**; `ItemTimeout` got past the guard and exposed that F1 is broader than filed |

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
| **`batchTimeoutSeconds` fires and raises `summary.timedOut`** | serial `mdop 1` × three 1500ms items vs a 3s deadline → `{3,1,2}`, `timedOut: true`, ≥1 item `FanOut:BatchTimeout` |
| **`maxDegreeOfParallelism` genuinely bounds concurrency** | same items, same config, ceiling 4 instead of 1 → `{3,3,0}`, `timedOut: false`. The matched pair is the whole proof: only the ceiling differs |
| **`itemTimeoutSeconds` fires on the right item and does NOT raise `summary.timedOut`** | 1s item deadline / 30s batch → `{3,2,1}`, `timedOut: false`, siblings unaffected. Only the item's error CODE is wrong (F1) |

**The join verdict propagates correctly.** An earlier reading of this run suggested it did not; that
was a defect in the SCENARIO, not the runtime — see § Corrected test design below. `join.policy` is
fully load-bearing on flow control.

---

## F1 — an item cancelled WHILE IN FLIGHT leaks a raw exception code instead of its fan-out cause

**Severity: medium-high.** Public-contract violation affecting **all three** cancellation causes, and
it can additionally falsify the batch-level `summary.timedOut` flag (see § Amplifier).

> **Scope widened after the MockLab fix (E1).** This was first filed as early-stop-only, because the
> item- and batch-deadline paths could not be exercised at all while the straggler mock was
> unreachable. With a working delay both were measured and the same leak is present. All three
> `FanOut` cancellation codes are affected.

### Expected

`FanOutErrorCodes` and `docs/domain/fan-out-task.md` § "Error codes and branching on partial failure"
define these as the task's public contract and tell authors to branch on these strings:

| Cause | Documented code |
|---|---|
| item exceeded `itemTimeoutSeconds` | `FanOut:ItemTimeout` |
| item cut short by `batchTimeoutSeconds` | `FanOut:BatchTimeout` |
| item cancelled by the join policy's early stop | `FanOut:ItemCancelled` |

### Actual — measured on all three paths

The discriminator is **whether the item had already entered the inner task**. An item still queueing
for a concurrency slot gets the correct code; an item in flight leaks
`Task:Unknown:{taskKey}:TaskCanceledException`. The leaked string also **embeds the task key**, so it
is not even stable to match on.

**Item deadline** — `itemTimeoutSeconds 1`, `batchTimeoutSeconds 30`, one 1500ms straggler.
`fanout-case-item-timeout-task@1.0.0`, instance `275f98d8-dd6a-44b3-b5f6-812f4ab43a2e`.
**0 of 1 correct:**

```
state=case-settled  summary={"total":3,"failed":1,"timedOut":false,"succeeded":2}
  DOC-1       isSuccess=True   errorCode=None
  DOC-SLOW-A  isSuccess=False  errorCode=Task:Unknown:process-document-task:TaskCanceledException
  DOC-3       isSuccess=True   errorCode=None       <-- DOC-SLOW-A expected FanOut:ItemTimeout
```

**Batch deadline** — `mdop 1`, `itemTimeoutSeconds 2`, `batchTimeoutSeconds 3`, three 1500ms items.
`fanout-case-batch-timeout-serial-task@1.1.0`, instance `fea4cebb-1542-461b-bf71-1a1fc2fbb5e8`.
**1 of 2 correct:**

```
state=case-settled  summary={"total":3,"failed":2,"timedOut":true,"succeeded":1}
  DOC-SLOW-A  isSuccess=True   errorCode=None                  (finished at ~1.5s)
  DOC-SLOW-B  isSuccess=False  errorCode=Task:Unknown:process-document-task:TaskCanceledException
  DOC-SLOW-C  isSuccess=False  errorCode=FanOut:BatchTimeout    (never started — correct)
```

**Early stop** — `firstSuccess` over five succeedable items.
`fanout-case-join-first-success-task@1.0.0`, instance `bccc762f-44bc-4e06-b645-822ca82223b8`.
**1 of 4 correct:**

```
state=case-settled  summary={"total":5,"failed":4,"timedOut":false,"succeeded":1}
  DOC-1  isSuccess=True   errorCode=None
  DOC-2  isSuccess=False  errorCode=Task:Unknown:process-document-task:TaskCanceledException
  DOC-3  isSuccess=False  errorCode=Task:Unknown:process-document-task:TaskCanceledException
  DOC-4  isSuccess=False  errorCode=Task:Unknown:process-document-task:TaskCanceledException
  DOC-5  isSuccess=False  errorCode=FanOut:ItemCancelled        (never started — correct)
```

### Amplifier — the leak can silently falsify `summary.timedOut`

`FanOutTaskExecutor` (~line 309) derives the batch flag from the per-item codes:

```csharp
var timedOut = ordered.Any(result => result.ErrorCode == FanOutErrorCodes.BatchTimeout);
```

`timedOut` is therefore true only because at least one cut item kept its correct code — above, the
never-started `DOC-SLOW-C`. **If every batch-cut item is in flight when the deadline fires — the
common shape once `maxDegreeOfParallelism >= item count` — all of them leak and `summary.timedOut`
reads `false` for a batch that demonstrably timed out.** A flow branching on `timedOut` takes the
wrong path with no error surfaced anywhere.

`BatchTimeout_SerialBatch_…` passes today only because `mdop 1` guarantees a never-started item.
That is not a lucky pass, but it is a narrow one: it would not survive raising `mdop` alone.

### Reproduction

```
POST  /api/v1/core/workflows/fan-out-config-matrix/instances/start?sync=false
      {"testId":"probe","documents":[{"id":"DOC-1"},{"id":"DOC-SLOW-A"},{"id":"DOC-3"}]}
      (poll GET .../instances/{id} until metadata.status != "B")
PATCH .../instances/{id}/transitions/run-item-timeout?sync=false   {}
      (poll until status != "B")
GET   .../instances/{id}   -> attributes.caseResults[].errorCode
```

Swap the transition for `run-batch-timeout-serial` (three `DOC-SLOW-*`) or `run-join-first-success`
(five `DOC-*`) to hit the other two paths. Requires MockLab's straggler route to be genuinely slow —
see § E1, now resolved.

Tests: `ItemTimeout_StampsItemTimeoutOnTheStraggler_AndLeavesTheBatchNotTimedOut` and
`EarlyStop_CancelledItems_CarryTheDocumentedFanOutItemCancelledCode`. Kept as two tests on purpose:
they pin different causes, and a fix could plausibly address one without the other.

### Assessment

`FanOutBatchCancellation.Classify` computes the right code — it is simply not consulted for an item
whose cancellation surfaced from inside the inner task. An in-flight item's `TaskCanceledException`
is normalized by the generic task pipeline into `Task:Unknown:{taskKey}:{exceptionName}` **before**
the fan-out layer can attribute it, and the executor then accepts that verbatim:

- `FanOutTaskExecutor` ~line 636: `engineResult.Error.Code ?? FanOutErrorCodes.ItemFailed`
- `FanOutTaskExecutor` ~line 652: `execution.TaskError?.NormalizedError.Code ?? FanOutErrorCodes.ItemFailed`

Both prefer the inner code whenever it is present. The fix is to consult the batch's cancellation
state FIRST when the item's failure is a cancellation and let `Classify(item, window)` name the cause,
falling back to the inner code only for genuine non-cancellation failures. `Classify`'s existing
precedence (own deadline → batch deadline → early stop → not started) is already correct and needs no
change. Fixing this also removes the `summary.timedOut` amplifier above for free.

**What is NOT broken:** the timeouts and the early stop themselves all work. The right item fails at
the right moment, siblings are unaffected, and `summary.timedOut` was correct on both runs above.
This is purely error attribution — which is why the affected tests assert their sound claims before
the code, so those stay verified while the defect stands.

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

## E1 — RESOLVED: the straggler mock was shadowed by MockLab's PREFIX route matching

**Not a FanOutTask defect. Was a seed-authoring bug in this repo; fixed here.**

### Symptom

`etc/docker/config/seed/fan-out-documents-collection.json` configured the straggler with
`"delayMs": 1500`, but it answered in 13-46ms — so `itemTimeoutSeconds`, `batchTimeoutSeconds` and
`maxDegreeOfParallelism` could not be exercised at all, and the `mdop` control arm **passed
vacuously** on the first run.

### Root cause

**MockLab matches routes by PREFIX, and the slow route was a suffix-extension of the fast one.**
`mock[0]` was registered at `api/fan-out/documents/process`, so every path starting with that string
was answered by it — including `api/fan-out/documents/process-slow`. The slow mock was
**unreachable** and its `delayMs` was never in play. Proof against the live container:

```
200  api/fan-out/documents/process-XYZQQ  -> {"pages":3}   <-- nonsense path, still the fast mock
200  api/fan-out/documents/process-slow   -> {"pages":3}   <-- never reaches the slow mock
404  api/fan-out/nonexistent              -> correctly absent
```

So the earlier conclusion "MockLab is not applying delayMs" was wrong: the delayed mock was simply
never the one answering. (Separately confirmed: no seed file in that directory puts a delay on a
rule — rule objects carry only `conditionField`, `conditionOperator`, `conditionValue`, `statusCode`,
`responseBody`, `contentType`, `priority`, `responseHeaders` — so a delayed response does have to be
its own mock. That part of the earlier note stands.)

### Fix applied

The straggler moved to a **sibling segment** that cannot be swallowed:
`api/fan-out/documents/process-slow` → **`api/fan-out/slow-documents/process`**. Both mappings that
select it for `DOC-SLOW*` ids were updated, and both workflows bumped (`fan-out-documents` 1.0.1 →
1.0.2, `fan-out-config-matrix` 1.1.0 → 1.2.0) because a published version is immutable — republishing
the same version answers 409 and the runtime keeps serving the old embedded `.csx`.

Verified after recreating the container:

```
api/fan-out/slow-documents/process  -> 200 in 1.735s  {"pages":120,"slow":true}   <-- own body, real delay
api/fan-out/documents/process       -> 200 in 0.038s  {"pages":3}
api/fan-out/documents/process?documentId=DOC-FAIL-A -> 500                        <-- rule still works
```

**A plain `docker restart` is not enough** — MockLab persists mocks in a container-local DB and skips
collections whose name already exists, so the container must be recreated to re-seed:
`docker compose up -d --force-recreate mocklab`.

### Guard against recurrence

`AssertStragglerRouteIsActuallySlowAsync` fronts all three straggler-driven tests and now makes two
checks:

1. **the response BODY carries the slow mock's marker** (`"slow"`) — a non-timing check, and the one
   that actually catches prefix shadowing;
2. **elapsed >= 1s** — the floor is the largest `itemTimeoutSeconds` any straggler case configures,
   not merely "some delay", because a delay below that floor cannot make an item miss its deadline
   and the case would pass while proving nothing.

The guard stays silent when MockLab is unreachable (the containerised path resolves it on a different
host). It is deliberately kept even though the bug is fixed: it is what turned a vacuous green into a
named failure.

### Timeout values, and why

The straggler delay stays **1500ms** — one slow mock reused by all three cases; since
`itemTimeoutSeconds` must be a whole number `>= 1`, 1500ms is the smallest value that can overshoot
the minimum deadline.

| Case | mdop | itemTimeout | batchTimeout | Why |
|---|---|---|---|---|
| item timeout | 4 | **1s** | 30s | 1500ms overshoots the item deadline by 50%; 28.5s of batch budget left, so `timedOut` must stay false — that is what separates the two codes |
| batch timeout (serial) | **1** | 2s | **3s** | Each item alone is 1500ms < 2s, so no item deadline can fire and only the batch deadline can cut. Serially: ~1.5s, ~3.0s, ~4.5s — item 3 is unambiguously past the deadline, so the assertion never depends on the borderline item 2 |
| mdop control arm | **4** | 2s | **3s** | Same items, same everything but the ceiling: three concurrent 1500ms items finish ~1.5s inside a 3s deadline |

`batchTimeoutSeconds` moved 2 → 3 on **both** mdop arms together (hence the task-component bump to
1.1.0). At 2s the parallel arm had only 500ms of slack for three concurrent HTTP calls plus per-item
engine overhead — the fragile arm was the *control* arm, the worst place for it. At 3s its slack
triples while the serial arm's discriminating gap widens (3s deadline vs 4.5s of serial work). They
must move together or the comparison stops being a concurrency claim.

### Still worth doing

`api-tests/fan-out-documents/fanout-load.py` reports a straggler ratio built on `DOC-SLOW` ids. It
was measuring jitter for as long as the route was shadowed; with the route fixed and the workflow
republished it should now measure something real, but the load test has **not been re-run** in this
pass.

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
