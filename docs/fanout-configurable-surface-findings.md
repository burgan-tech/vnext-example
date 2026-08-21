# FanOutTask — configurable-surface findings

**Subject:** `FanOutTask` (TaskType 21), branch `feature/fanout-task-design` of
`burgan-tech/vnext`.
**Scenario:** `fan-out-config-matrix` — `core/Workflows/fan-out-config-matrix/`,
`core/Tasks/fan-out-config-matrix/`,
`tests/Core.IntegrationTests/Tests/FanOut/FanOutConfigMatrixTests.cs`.
**Closed out:** 2026-08-22 against runtime `ad72158b`, hosts started 00:16 on that build,
`VNEXT_BASE_URL=http://localhost:4201` (no Testcontainers/image stack involved).

---

## Status: ALL GREEN — 22 tests, 22 passed, 0 failed (twice)

```
dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~FanOut"
  FanOutConfigMatrixTests  18 tests, 18 passed, 0 failed
  FanOutDocumentsTests      4 tests,  4 passed, 0 failed
```

Two consecutive runs, identical. Every previously-red test is green, and every green test is green for
a reason that was verified rather than assumed.

| Item | Status |
|---|---|
| **F1** — cancelled items leaked a raw exception code | ✅ **RESOLVED** — `b80be176`, re-measured on all three causes |
| **F1 amplifier** — the leak could falsify `summary.timedOut` | ✅ **WITHDRAWN** — measured unreachable by construction; my claim was wrong |
| **F2** — `durable` refused with an opaque 500 | ✅ **RESOLVED** — `ad72158b`, verified 400 + field name |
| **E1** — straggler mock unreachable (MockLab prefix matching) | ✅ **RESOLVED** — seed route moved, verified 1.7s |
| **L1** — load test's BULKHEAD metric invalid under saturation | ✅ **FIXED in the load test** (validity gate) |
| **L2** — load test's straggler threshold calibrated against a broken fixture | ✅ **FIXED in the load test** (two-sided, absolute floor) |
| **F3** — per-item `ignore` has no observable effect | 🔶 **OPEN** — semantics question, needs a product decision |
| **C1** — `join.minSuccess` silently ignored for non-`quorum` policies | 🔶 **OPEN** — documented-intentional authoring hazard |

---

## The configurable surface: what is now verified end to end

| Behaviour | Evidence |
|---|---|
| `all` — every item succeeds ⇒ success | `{3,3,0}` → `case-settled` |
| `all` — one item fails ⇒ FAIL, result set still written | → `case-failed`, summary + rows present |
| `all` — empty batch ⇒ succeeds vacuously | `{0,0,0}` → `case-settled` |
| `allSettled` — empty batch ⇒ succeeds | `{0,0,0}` → `case-settled` |
| `quorum(minSuccess 2)` — 2 of 4 ⇒ success **with failures in it** | `{4,2,2}` → `case-settled` |
| `quorum(minSuccess 2)` — 1 of 3 ⇒ FAIL | `{3,1,2}` → `case-failed` |
| `quorum` — empty batch ⇒ FAIL (0 cannot clear a threshold) | `{0,0,0}` → `case-failed` |
| `firstSuccess` — one succeeds ⇒ success | `{3,1,2}` → `case-settled` |
| `firstSuccess` — none succeeds ⇒ FAIL | `{2,0,2}` → `case-failed` |
| `firstSuccess` — empty batch ⇒ FAIL, same as `quorum(1)` | `{0,0,0}` → `case-failed` |
| `itemTimeoutSeconds` fires on the right item, `timedOut` stays false | `{3,2,1}`, `FanOut:ItemTimeout` |
| `batchTimeoutSeconds` fires and raises `timedOut` | `{3,1,2}`, `timedOut: true`, `FanOut:BatchTimeout` |
| **equal deadlines** — every item blows its own first, `timedOut` stays false | `{3,0,3}`, all `FanOut:ItemTimeout` |
| `maxDegreeOfParallelism` genuinely bounds concurrency | matched pair, only the ceiling differs |
| per-item `errorBoundary` `retry` — exhaustion contained to its item | `{3,2,1}`, both siblings succeeded |
| `mode: "durable"` refused at publish with an actionable 400 | see F2 |
| a failed join still carries its data | `TaskInvocationResult.Failure` correctly avoided |

---

## F1 — RESOLVED (`b80be176`)

**Was:** an item cancelled while in flight reported the inner task's raw
`Task:Unknown:{taskKey}:TaskCanceledException` instead of its fan-out cause, so authors branching on
`FanOutErrorCodes` silently missed most cancelled items.

**Fix:** `MapEngineOutcome` re-attributes cancellations through `FanOutBatchCancellation.Classify(...)`
— an interrupted engine is re-attributed when the batch's tokens fired, and a completed engine keeps
its own code unless the structured `NormalizedError.ExceptionType` is
`OperationCanceledException`/`TaskCanceledException`. No string parsing of the composed code.

### Verification — all three causes re-measured

| Cause | Before | After | Instance |
|---|---|---|---|
| item deadline (`itemTO 1s` / `batchTO 30s`) | 0/1 correct | **1/1** | `d66089f4-5501-4e7c-8d09-4ad14251b191` |
| batch deadline (`mdop 1`, `itemTO 2s` / `batchTO 3s`) | 1/2 | **2/2** | `520c18b1-f934-4793-b8a2-b007f4392d67` |
| early stop (`firstSuccess`, 5 succeedable) | 1/4 | **4/4** | `88287189-9728-4309-85eb-6ed716e57db1` |

**Zero leaked codes across all three.** The decisive row is the batch-deadline case: `DOC-SLOW-B` — the
in-flight item that previously leaked — now reports `FanOut:BatchTimeout`, while the never-started
`DOC-SLOW-C` still reports it too, so both attribution paths agree.

```
CAUSE 2 — BATCH deadline, after the fix
  state=case-settled  summary={"total":3,"failed":2,"timedOut":true,"succeeded":1}
    DOC-SLOW-A  isSuccess=True   errorCode=None
    DOC-SLOW-B  isSuccess=False  errorCode=FanOut:BatchTimeout   <-- was Task:Unknown:...TaskCanceledException
    DOC-SLOW-C  isSuccess=False  errorCode=FanOut:BatchTimeout
```

Regression guards: `ItemTimeout_StampsItemTimeoutOnTheStraggler_AndLeavesTheBatchNotTimedOut`,
`BatchTimeout_SerialBatch_StampsBatchTimeoutAndMarksTheBatchTimedOut`,
`EarlyStop_CancelledItems_CarryTheDocumentedFanOutItemCancelledCode`.

### F1 amplifier — WITHDRAWN, my claim was wrong

I filed a concern that, because `FanOutTaskExecutor` (~line 309) derives the batch flag from the
per-item codes:

```csharp
var timedOut = ordered.Any(result => result.ErrorCode == FanOutErrorCodes.BatchTimeout);
```

a batch whose cut items were *all* in flight would have had every code leak and would report
`timedOut: false` despite timing out. I built the case explicitly to test it —
`run-batch-timeout-parallel` / `fanout-case-batch-timeout-parallel-task`, `mdop 4` over 3 items with
`itemTimeoutSeconds == batchTimeoutSeconds == 1` — and the shape **does not exist**:

```
state=case-settled  summary={"total":3,"failed":3,"timedOut":false,"succeeded":0}
  DOC-SLOW-A / B / C   isSuccess=False   errorCode=FanOut:ItemTimeout   (all three)
```

**Why it is unreachable by construction.** An item's deadline window is armed at ITEM start with
`itemTimeoutSeconds` (`OpenItemWindow()`, opened only after both slots are held); the batch deadline is
armed at BATCH start with `batchTimeoutSeconds` (`new CancellationTokenSource(...)` in the
`FanOutBatchCancellation` constructor); and `Classify` checks the item's own deadline **first**. Since
`itemTimeoutSeconds <= batchTimeoutSeconds` is enforced at parse time, an item running from batch start
always reaches its own deadline first. `FanOut:BatchTimeout` is therefore reachable only for an item
whose start was delayed by more than `batchTimeout - itemTimeout` — i.e. one that queued behind the
concurrency limit, which requires `mdop < itemCount`. That is the serial arm, and it is also why the
serial arm's never-started item kept a correct code even while F1 was open.

`timedOut: false` above is **correct**: no item was cut by the batch deadline.

The case was kept as `EqualDeadlines_EveryItemBlowsItsOwnDeadline_AndTheBatchIsNotReportedTimedOut`,
because it pins something nothing else covers — that the two deadlines are not conflated at the one
configuration where they expire together.

---

## F2 — RESOLVED (`ad72158b`)

**Was:** `mode: "durable"` was refused at publish (good) but with an opaque
`500 "An internal error occurred during your request!"`, discarding an exception message that already
named both the offending mode and the supported one.

**Fix:** `ComponentValidatorProcessor` catches `ArgumentException` (+ `ArgumentNullException`,
`ArgumentOutOfRangeException`) around the single validator invocation and converts it into a validation
error keyed `{componentType}.{paramName}`. One catch at the shared coordinator, so it covers every
component type and every task type.

### Verification

```
POST /api/v1/definitions/publish   (type 21, config.mode = "durable", fresh version)
  HTTP 400
  errorCode: validation.App:900006
  detail   : Component validation failed for type 'sys-tasks'
  errors   : {"sys-tasks.config": [
               "FanOutTask mode 'durable' is not supported yet. Only 'inline' is available (Key=). (Parameter 'config')"]}
```

Generalisation confirmed with an unrelated `Configure`-time check — a bad `itemsPath`, which was
previously also a 500:

```
  errors : {"sys-tasks.config": ["FanOutTask itemsPath must start with '$.' (Key=). (Parameter 'config')"]}
```

Regression guard: `DurableMode_IsRefusedWithAnActionableValidationError` (asserts refusal, `< 500`, and
that the body names the mode).

**Cosmetic nit, not filed as a defect:** the message renders `Key=` empty, because `Configure` throws
before the component key is populated on the task instance. Harmless — the field key
(`sys-tasks.config`) plus the component's own `key` in the request identify it — but a fixer touching
this area may want to interpolate the incoming key.

---

## E1 — RESOLVED: the straggler mock was shadowed by MockLab's PREFIX route matching

**Was a seed-authoring bug in this repo, not a runtime or container fault.** The straggler was
configured with `delayMs: 1500` but answered in 13-46ms, so `itemTimeoutSeconds`,
`batchTimeoutSeconds` and `maxDegreeOfParallelism` could not be exercised at all — and the `mdop`
control arm **passed vacuously**.

**Root cause:** MockLab matches routes by prefix. `mock[0]` was registered at
`api/fan-out/documents/process`, so every path starting with that string was answered by it, including
`api/fan-out/documents/process-slow`. The slow mock was unreachable and its delay never applied:

```
200  api/fan-out/documents/process-XYZQQ  -> {"pages":3}   <-- nonsense path, still the fast mock
200  api/fan-out/documents/process-slow   -> {"pages":3}   <-- never reaches the slow mock
404  api/fan-out/nonexistent              -> correctly absent
```

So the earlier conclusion "MockLab is not applying delayMs" was wrong — the delayed mock was never the
one answering. (Separately confirmed and still true: no seed file in that directory puts a delay on a
*rule*; rule objects carry only `conditionField`, `conditionOperator`, `conditionValue`, `statusCode`,
`responseBody`, `contentType`, `priority`, `responseHeaders`. A delayed response must be its own mock.)

**Fix:** moved to a sibling segment that cannot be swallowed —
`api/fan-out/documents/process-slow` → **`api/fan-out/slow-documents/process`** — with both mappings
updated and both workflows bumped (`fan-out-documents` 1.0.1 → 1.0.2, `fan-out-config-matrix` 1.1.0 →
1.2.0 → 1.3.0), since a published version is immutable and republishing answers 409 while the runtime
keeps serving the old embedded `.csx`.

```
api/fan-out/slow-documents/process  -> 200 in 1.735s  {"pages":120,"slow":true}
api/fan-out/documents/process       -> 200 in 0.038s  {"pages":3}
...process?documentId=DOC-FAIL-A    -> 500                     (rule still works)
```

**A plain `docker restart` does not re-seed** — MockLab persists mocks in a container-local DB and skips
collections whose name already exists. The container must be recreated:
`docker compose up -d --force-recreate mocklab`.

**Guard against recurrence:** `AssertStragglerRouteIsActuallySlowAsync` fronts all four
straggler-driven tests and checks (1) the response BODY carries the slow mock's marker — the
non-timing check, and the one that catches prefix shadowing — and (2) elapsed >= 1s, the largest
`itemTimeoutSeconds` any straggler case configures. It stays silent when MockLab is unreachable.

### Timeout values, and why

The straggler delay stays **1500ms**: `itemTimeoutSeconds` must be a whole number `>= 1`, so 1500ms is
the smallest value that can overshoot the minimum deadline. One slow mock serves every case.

| Case | mdop | itemTO | batchTO | Why |
|---|---|---|---|---|
| item timeout | 4 | 1s | 30s | 1500ms overshoots the item deadline by 50%; 28.5s of batch budget left, so `timedOut` must stay false — that is what separates the two codes |
| batch timeout (serial) | 1 | 2s | 3s | Each item alone is 1500ms < 2s, so only the batch deadline can cut. Serially ~1.5s / ~3.0s / ~4.5s — item 3 is unambiguously past it, so the assertion never depends on the borderline item 2 |
| mdop control arm | 4 | 2s | 3s | Same items, same everything but the ceiling: three concurrent 1500ms items finish ~1.5s inside a 3s deadline |
| equal deadlines | 4 | 1s | 1s | Every item blows its own deadline; the one configuration where a deadline-precedence bug would be invisible |

`batchTimeoutSeconds` moved 2 → 3 on **both** mdop arms together (task components bumped to 1.1.0). At
2s the parallel arm had only 500ms of slack for three concurrent calls plus per-item engine overhead —
the fragile arm was the *control* arm, the worst place for it. They must move together or the
comparison stops being a concurrency claim.

---

## L1 — RESOLVED in the load test: BULKHEAD metric is invalid under saturation

Found while re-running `api-tests/fan-out-documents/fanout-load.py` for the first time with a working
straggler. The default profile reported a violation:

```
12 instances x 8 items:  efektif eszamanlilik 59.82  (tavan 36)  -> FAIL BULKHEAD
```

**Not a runtime defect.** The metric is `sum(item durationMs) / wall`, and its stated premise is that
each item's duration is in-flight downstream time. That premise is false: `FanOutTaskExecutor` starts
the item stopwatch **before** the slot waits —

```
RunItemWithGatesAsync:  Stopwatch.StartNew()          (line ~330)
                        degreeGate.WaitAsync(...)      (line ~351)
                        AcquireGlobalSlotAsync(...)    (line ~353)
                        OpenItemWindow()               (line ~362)
```

— so `durationMs` **includes queue-wait time**. The runtime tags `vnext.fanout.item.queue_wait_ms` on
each item span precisely to separate the two, and the per-item *deadline* window is deliberately opened
only after the slots are held (so `itemTimeoutSeconds` is unaffected — it measures execution only).

Consequence: the metric inflates exactly when items queue, i.e. exactly when the bulkhead is doing its
job, producing a **false FAIL**. Queue-wait is not exposed on the data the load test can read
(`caseResults[].durationMs` is all it has; queue wait lives on spans in the monitoring host), so the
metric cannot be corrected — only scoped.

**Fix:** the BULKHEAD assertion now runs only on a provably queue-free profile — `items <= max-dop`
(no batch-local gate wait) **and** `instances*items <= ceiling` (no global bulkhead wait) — and
otherwise prints a SKIP naming which condition failed and how to run it meaningfully. This matches the
script's own design note that exact peak concurrency comes from the monitoring host's per-item spans.

```
--instances 4 --items 3 --max-dop 3   ->  PASS BULKHEAD  3.96 <= 13.80        (6 checks)
--instances 12 --items 8             ->  SKIP BULKHEAD  items (8) > max-dop (3) …
```

---

## L2 — RESOLVED in the load test: the straggler threshold was calibrated against a broken fixture

The STRAGGLER check was one-sided, `max/p50 <= 4.0`. With a working straggler the ratio is **~10 by
design** (1500ms straggler / ~150ms fast item), so the check failed the moment the fixture started
working:

```
first meaningful run: item p50/max = 158ms / 1621ms, ratio 10.26  ->  FAIL (esik 4.0)
```

The 4.0 ceiling was only ever satisfiable while the route was shadowed and every item was fast. Worse,
being one-sided it **could not detect the missing straggler** — which is precisely how E1 hid for so
long.

**Fix:** two-sided, and the floor is absolute rather than a ratio.

- Ceiling raised to `15.0` with the derivation recorded (≈10 expected, headroom for queueing).
- New `STRAGGLER-VAR` floor: `slowest >= 0.8 × SLOW_ROUTE_DELAY_MS` (1200ms), skipped when
  `--slow-per-instance 0`.

The floor is absolute because `max/p50` proved too noisy to serve as a presence detector: a run with
**no** `DOC-SLOW` items at all produced a ratio of 9.44 (one cold 963ms item against a 102ms median).
A ratio floor would have accepted that as "straggler present"; the absolute floor does not.
`SLOW_ROUTE_DELAY_MS` is a module constant pointing back at the seed so the two cannot drift silently.

### Load test result

Both profiles PASS:

```
--instances 4 --items 3 --max-dop 3 --slow-per-instance 1   SONUC: PASS — 6 checks
  BULKHEAD 3.96 <= 13.80 | TEK-YAZIM 0 broke | STRAGGLER-VAR 1849ms >= 1200ms | STRAGGLER 6.53 <= 15.0

--instances 12 --items 8 (default, saturated)               SONUC: PASS — 5 checks
  BULKHEAD skipped (queueing) | TEK-YAZIM 0 broke | STRAGGLER-VAR 4716ms >= 1200ms | STRAGGLER 1.39 <= 15.0
```

`SAGLIK` and `TEK-YAZIM` passed on every run — no instance faulted, and the single-write invariant held
under load, including the saturated 96-item profile. `ITEM-JOURNAL` remains SKIP without
`--monitor-url`.

---

## Still open

### F3 — per-item `errorBoundary` with `action: ignore` has no observable effect

**Severity: low. Semantics undocumented — needs a product decision, not necessarily a code fix.**

A wildcard `{"action":"ignore","errorCodes":["*"],"priority":999}` per-item boundary under
`join.policy: all`, over `[DOC-1, DOC-FAIL-A, DOC-3]`, leaves the failing item failed: it still counts
toward `failed`, still fails the `all` join, and still triggers the early stop — indistinguishable in
outcome from having no boundary at all.

Defensible as-is: the guide documents only the `retry` case, and `ErrorAction.Ignore` maps to
`BoundaryActionResult.Continue` / `ShouldContinue`, which is about not propagating an error rather than
fabricating success. But `ignore`/`log` are then inert for fan-out items while looking configurable, and
an author would reasonably expect an ignored item not to sink an `all` batch. Either the semantics or
the documentation should change.

The boundary *is* consulted per item: with a `retry` rule the failing item's code is
`Task:Http:process-document-task:500` (the inner task's own code passing through), whereas with the
`ignore` rule it is `FanOut:ItemFailed`. So this is not "the boundary is never applied".

Pinned as a characterization test —
`PerItemErrorBoundary_Ignore_DoesNotConvertAFailedItemIntoASuccess` — which passes and carries a
comment saying to invert it deliberately if the intended semantics turn out to be "an ignored item
counts as successful".

### C1 — `join.minSuccess` accepted and silently ignored for non-`quorum` policies

**Severity: low (authoring trap). Documented as intentional. Deliberately NOT pinned.**

`FanOutTask.Configure` validates `minSuccess` in one direction only (required and `>= 1` for `quorum`).
`{"policy":"firstSuccess","minSuccess":3}` parses, stores 3, and behaves as 1, because
`FanOutJoinEvaluator` reads `minSuccess` only on the `Quorum` arm. Same for `all` and `allSettled`. No
warning at definition time, which is the only place it could be visible.

Not pinned by a test on purpose: it is documented as intentional ("Ignored (with no warning today) for
other policies"), and asserting a documented no-op would cement the trap — whoever later makes it
meaningful, or rejects it, would have to delete the test. Recorded here so the product call can be made
explicitly. It is the same failure shape the repo treats as worth erroring on for dynamic role grants.

---

## Verification gaps — what the SDK / environment still cannot express

1. **Per-item retry ATTEMPT counts.** They live in the `InstanceTask` journal row keyed
   `{fanOutTaskKey}#{index}`, reachable only via the monitoring host's
   `GET /api/v1/monitor/.../instances/{id}/tasks` (port 4203), which no test stack starts and the SDK
   does not wrap. MockLab's sequential-response feature is per-mock, not per-item, so it cannot express
   "fail once then succeed" under concurrency either.
   `PerItemErrorBoundary_Retry_ContainsExhaustionToItsOwnItem` asserts containment instead of faking a
   count. Closing this needs an SDK monitoring wrapper or a MockLab per-request-key sequential mode.
2. **Exact peak concurrency.** See L1 — `durationMs` includes queue wait, so peak in-flight concurrency
   is only readable from the monitoring host's per-item spans (`vnext.fanout.item.queue_wait_ms`). The
   load test now scopes its claim rather than guessing.
3. **`npm run validate` rejects every `attributes.type: "21"` component.**
   `@burgan-tech/vnext-schema` 0.0.52 caps the task-type enum at 20; the run fails on the ten
   `fan-out-*` task components (nine matrix + one documents). Publish bypasses schema validation — all
   published 200 — so the runtime path is unaffected. Not worked around.
4. **Elasticsearch was not usable as evidence.** A `trace.id` term query on
   `logs-apm.app.vnext_app-default*` returned 0 hits for these traces. All evidence in this file is
   from the API surface and instance data.

---

## Corrected test design (recorded so the mistake is not repeated)

The first revision declared **no** error boundary and treated a **Faulted instance** as the failed-join
signal. That was wrong and made all five failed-join cases pass silently.

Control that settled it: feeding `documents` a STRING instead of an array makes `FanOutItemsResolver`
throw — a hard `Result<TaskInvocationResult>.Fail`, the strongest failure available — and the instance
still reached `case-settled` with no `caseResults` written. **With no boundary configured, a failing
onEntry task is not acted on; the state's unconditional auto transition fires regardless.**

The workflow now declares one global boundary and every case state carries the target transition:

```json
"errorBoundary": { "onError": [ { "action": "rollback", "transition": "to-case-failed", "priority": 999 } ] }
```

`action: "abort"` cannot be used with a transition — the validator rejects it with *"Transition must not
be specified when Action is Abort."*, because abort-without-transition is the fault path. `rollback` is
the transition-carrying action.

Two further authoring facts learned the same way, both documented in the builder script:

- **Every `triggerType: 1` transition must carry a `rule`.** "A lone auto transition is valid if its
  rule always returns true" means a rule returning `true`, not the absence of a rule. Omitting it fails
  publish with 400 and `"Auto transition '…' must have a rule defined."` Hence `src/CaseSettledRule.csx`.
- **The start transition must settle before a case transition is fired**, or the runtime answers 409
  (Busy). `RunCaseAsync` waits for non-Busy first.

---

## Run history

| Run | Result | What changed |
|---|---|---|
| 1 | 16 tests, 0 passed | Workflow never published (auto transitions need a `rule`; `abort` cannot carry a transition) |
| 2 | 16 tests, 7 passed | Published. Fault-based observation of a failed join was wrong |
| 3 | 17 tests, 12 passed | Global `rollback` boundary → `case-failed`. 2 defects + 3 blocked by E1 |
| 4 | 17 tests, 14 passed | E1 fixed. `BatchTimeout` and `mdop` pass for real; `ItemTimeout` exposed F1's true scope |
| 5 | **22 tests (18+4), 22 passed** | F1 `b80be176` + F2 `ad72158b`. Amplifier withdrawn; equal-deadlines case added; load test L1/L2 fixed |
