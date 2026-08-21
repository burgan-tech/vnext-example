# FanOutTask — configurable-surface findings

**Subject:** `FanOutTask` (TaskType 21), branch `feature/fanout-task-design` of
`burgan-tech/vnext`.
**Scenario under test:** `fan-out-config-matrix` (this repo) —
`core/Workflows/fan-out-config-matrix/`, `core/Tasks/fan-out-config-matrix/`,
`tests/Core.IntegrationTests/Tests/FanOut/FanOutConfigMatrixTests.cs`.
**Date:** 2026-08-21.

---

## Status: NO RUNTIME DEFECT CONFIRMED — the suite was never executed

**Read this section before anything else.** This file does not report defects observed in a running
system, because the tests could not be run. The scenario was authored and compiles; it has not
produced a single result.

### Why

The four vNext application hosts were **not running**, and the task's constraints forbid starting
them.

Evidence gathered 2026-08-21:

```
$ curl -s -m 10 -o /dev/null -w '%{http_code}' http://localhost:4201/health   ->  000
$ curl -s -m 10 -o /dev/null -w '%{http_code}' http://localhost:4202/health   ->  000
$ lsof -nP -iTCP -sTCP:LISTEN | grep -E ':(4201|4202)'                        ->  (no rows)
$ pgrep -fl 'BBT.Workflow'                                                    ->  (no rows)
```

Docker infrastructure IS up and healthy — `vnext-postgres`, `vnext-redis`, `vnext-elasticsearch`
(healthy, 9200 answering 200), `vnext-vault` (healthy), `mocklab` (healthy, published on **3001**),
the OTEL collector, and the Dapr sidecars for orchestration / execution / inbox / outbox — all
"Up 8 hours". The sidecars are up **waiting for their apps to attach**; only the four
locally-built `dotnet run` processes are missing.

Re-checked at the end of the session: still down.

### To finish this work

1. In the vNext workspace, start the four hosts, each with `--launch-profile http`:
   ```
   dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host --launch-profile http
   dotnet run --project execution/BBT.Workflow.Execution.HttpApi.Host       --launch-profile http
   dotnet run --project workers/BBT.Workflow.Workers.Inbox                  --launch-profile http
   dotnet run --project workers/BBT.Workflow.Workers.Outbox                 --launch-profile http
   ```
   Do **not** use container images — they carry pre-`feature/fanout-task-design` code and would
   silently invalidate every result.
2. Confirm `tests/Core.IntegrationTests/test.runsettings` still carries
   `<VNEXT_BASE_URL>http://localhost:4201</VNEXT_BASE_URL>` (it does as of this commit). If it is
   unset the SDK provisions its own Testcontainers stack **from images** — same invalidation.
3. Run:
   ```
   dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~FanOutConfigMatrix"
   ```
4. Triage each failure into (a) genuine runtime defect, (b) wrong expectation in the test,
   (c) environment problem — and fill in the "Confirmed findings" section below. **Do not weaken an
   assertion to get green.** Every assertion's rationale is recorded in the test's own XML doc
   comments and in `tests/Core.IntegrationTests/Tests/FanOut/README.md`.

### What each case would prove

The full matrix, per case, with item mix and expected outcome, is tabulated in
[`tests/Core.IntegrationTests/Tests/FanOut/README.md`](../tests/Core.IntegrationTests/Tests/FanOut/README.md)
§ 2. Sixteen `[Fact]`s cover: all four `join.policy` values on **both** sides of their verdict;
`join.minSuccess` met and not met; the empty-collection rule for all four policies;
`itemTimeoutSeconds` vs `batchTimeoutSeconds` producing distinct error codes and disagreeing about
`summary.timedOut`; `maxDegreeOfParallelism` bounding real concurrency (matched pair, differing only
in the ceiling); per-item `errorBoundary` with `ignore` and with `retry`; and `mode: "durable"`
being refused at publish.

### Highest-value cases to watch when it does run

Ranked by how likely a failure is to be a genuine defect rather than a wrong expectation:

1. **`PerItemErrorBoundary_Ignore_KeepsAFailingItemFromTakingTheBatchDown`** — the only case whose
   outcome *flips* on the configuration under test (`join: all` + wildcard `ignore` on a mix that
   faults without the boundary). If the per-item boundary is not actually wired through
   `FanOutTaskExecutor`'s per-item task-engine call, this faults and nothing else in the matrix
   notices.
2. **`JoinAll_OneItemFails_...` / `JoinQuorum_MinSuccessNotMet_...` / `JoinFirstSuccess_NoItemSucceeds_...`**
   — whether a failed join actually propagates as a failed task. `FanOutJoinEvaluator` returning
   `IsSuccess = false` is pinned by unit tests; that the executor then *fails the task* (rather than
   returning success with failure data attached, which the doc's "`TaskInvocationResult.Failure` is
   deliberately not used" note makes non-obvious) is what only an end-to-end run can show.
3. **The three empty-collection cases** — the `all`/`allSettled` ⇄ `quorum`/`firstSuccess`
   asymmetry depends on `FanOutItemsResolver` resolving `$.documents: []` to an empty batch rather
   than throwing.
4. **`DurableMode_IsRefusedRatherThanSilentlyAccepted`** — see candidate C2 below.

---

## Confirmed findings

**None.** Nothing in this section until the suite has been run. Add one `###` subsection per
confirmed defect, each with: expected, observed, exact reproduction (component config + test name +
trace id), evidence (log lines / the Elasticsearch query used), and severity.

Trace ids: the `X-Trace-Id` response header on the start call. Query:

```
POST http://localhost:9200/logs-apm.app.vnext_app-default*/_search
{ "query": { "term": { "trace.id": "<trace id>" } },
  "sort": [ { "@timestamp": "asc" } ], "size": 200 }
```

---

## Candidate concerns from static reading (NOT confirmed defects)

These came from reading the runtime source, not from running it. Each is recorded so the next
session can confirm or dismiss it; none is asserted by the suite as a defect.

### C1 — `join.minSuccess` is accepted and silently ignored for non-`quorum` policies

**Severity: low (authoring trap / validation gap). Documented as intentional.**

`FanOutTask.Configure`
(`src/BBT.Workflow.Domain/Definitions/Tasks/FanOutTask.cs`) validates `minSuccess` in one direction
only:

```csharp
if (JoinPolicy == FanOutJoinPolicy.Quorum && MinSuccess is null or < 1)
    throw new ArgumentException(
        $"FanOutTask join.policy 'quorum' requires join.minSuccess >= 1 (Key={Key}).", nameof(config));
```

There is no converse check. `{"policy": "firstSuccess", "minSuccess": 3}` parses, stores
`MinSuccess = 3`, and then behaves as `minSuccess = 1` — `FanOutJoinEvaluator` only reads
`minSuccess` on the `Quorum` arm. Same for `all` and `allSettled`. The author gets no warning at
definition time, and definition time is the only place it could be visible.

`docs/domain/fan-out-task.md` documents this explicitly ("Ignored (with no warning today) for other
policies"), so it is a **deliberate current state**, not an oversight. Raised here only because it is
the same failure shape the repo treats as worth erroring on elsewhere (cf. the dynamic role-grant
rule: a malformed grant becomes silently inert, and "definition time is the only place it is
visible"). Whether to reject it, warn, or leave it is a product call.

**Not covered by the suite**, deliberately — asserting a documented no-op would pin the trap rather
than the behaviour.

### C2 — it is unverified whether `mode: "durable"` is rejected at PUBLISH or only at execution

**Severity: unknown until run. Potentially medium (late failure surface).**

`FanOutTask.Configure` throws `ArgumentException` on any `mode` other than `inline`. What is not
established from source alone is **when** that constructor runs relative to
`POST /api/v1/definitions/publish`: if publish stores the component JSON without materialising the
task, a `durable` component publishes cleanly and the throw is deferred to the first execution of
whatever flow references it — i.e. a definition-time error surfacing as a runtime fault, possibly in
production.

`DurableMode_IsRefusedRatherThanSilentlyAccepted` is written to fail loudly in exactly that case,
with a message saying so. If it fails with a 2xx from publish, **that is a genuine finding** and
belongs in "Confirmed findings" above — not a wrong expectation.

The second assertion in that test (the response body must mention `durable` / `not supported` /
`inline`) exists so that a refusal for some *unrelated* validation reason cannot masquerade as the
rejection under test.

---

## Verification gaps — things the SDK / environment cannot express

Recorded so nobody re-derives them, and so no one mistakes their absence for oversight.

1. **Per-item retry ATTEMPT counts are not observable.** A per-item retry's attempts land in the
   `InstanceTask` journal row keyed `{fanOutTaskKey}#{index}` (`FanOutTaskExecutor` sets
   `JournalTaskKey = $"{task.Key}#{item.Index}"`). Those rows are reachable only through the
   monitoring host's `GET /api/v1/monitor/{domain}/workflows/{workflow}/instances/{id}/tasks`
   (port 4203), which neither `VNext.Testing.Sdk`'s container stack nor the local dev stack starts,
   and the SDK exposes no wrapper for it. MockLab's sequential-response feature (`isSequential` +
   `sequenceItems`) is **per-mock, not per-item**, so it cannot express "fail once then succeed"
   under concurrent items either. `PerItemErrorBoundary_Retry_...` therefore asserts containment
   (exhaustion stays in its own item; siblings and the batch are unaffected) rather than faking an
   attempt count. Closing this gap needs either an SDK monitoring-host wrapper or a MockLab
   per-request-key sequential mode.
2. **`WorkflowTestBase` has no "expect a fault" waiter.** `WaitForInstanceStateAsync` deliberately
   fails fast when the instance faults — correct for every other suite, exactly wrong for the six
   cases here where a fault is the expected outcome. `FanOutConfigMatrixTests.WaitForFaultAsync`
   fills it locally from `WaitUntilAsync` + `GetInstanceStateAsync`. If a third suite ever needs it,
   promote it to `WorkflowTestBase`.
3. **`npm run validate` rejects every `attributes.type: "21"` component.**
   `@burgan-tech/vnext-schema` 0.0.52 caps the task-type enum at 20. Confirmed this session: the run
   fails with exactly ten files — the nine new `core/Tasks/fan-out-config-matrix/*.json` **and** the
   pre-existing `core/Tasks/fan-out-documents/fan-out-documents-task.json`. Nothing else fails, and
   the workflow component passes. Expected, tracked separately, and *not* worked around here:
   publish bypasses schema validation, so the runtime path is unaffected. Do not contort a component
   to satisfy the stale schema.
4. **Instance-data version history is orchestration-invisible.** Noted for completeness — this is
   why the sibling `fan-out-documents` scenario measures the single-write invariant with in-flow
   version stamps. See `tests/Core.IntegrationTests/Tests/FanOut/README.md` § 1. The config matrix
   does not re-measure that invariant; it is already covered.

---

## Dismissed during investigation

Kept so the next session does not spend the same time on it.

- **`FanOut:Cancelled` vs `FanOut:ItemCancelled` divergence — NOT REAL.** A grep appeared to show
  private constants in `FanOutTaskExecutor.cs` (`EarlyStopErrorCode = "FanOut:Cancelled"`) diverging
  from the public `FanOutErrorCodes.ItemCancelled = "FanOut:ItemCancelled"`, which would have been a
  public-contract bug. Verified against the current file: **those constants do not exist.** The hit
  came from a stale cross-session search index (and from
  `docs/superpowers/plans/2026-08-21-fanout-task.md`, a design document quoting an earlier draft).
  Current code stamps codes only via `FanOutErrorCodes.*` — `FanOutBatchCancellation.Classify` uses
  `ItemTimeout` / `BatchTimeout` / `ItemCancelled` / `ItemNotStarted`, and `FanOutTaskExecutor` uses
  `ItemFailed` / `BatchTimeout`. No divergence.

---

## Environment reference

| Thing | Where | Note |
| --- | --- | --- |
| Orchestration | `http://localhost:4201` | **down this session** |
| Execution | `http://localhost:4202` | **down this session** |
| MockLab | `http://localhost:3001` | up (container port 5000) |
| Elasticsearch | `http://localhost:9200` | up, healthy |
| Monitoring host | `http://localhost:4203` | not started by any stack; see gap 1 |
| MockLab seed | `etc/docker/config/seed/fan-out-documents-collection.json` | shared with the sibling scenario; `DOC-*` → 200, `DOC-FAIL*` → 500, `DOC-SLOW*` → 200 after 1500ms |

The matrix deliberately **reuses** that seed and the existing `process-document-task` inner task
rather than adding new mocks — the item mix per case is chosen from the three existing id prefixes.
