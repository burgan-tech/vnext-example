# FaultedRecoveryLab — integration test

One test class, **38 test methods / 40 cases**, over the `faulted-recovery-lab` flows (domain `core`): can
an instance that faulted because a transition's mapping threw, because the data it produced failed the
closed master schema, or because its instance-key check failed, be recovered through the API — `retry`
re-running the failed hop, the reserved `cancel` closing the instance? And does everything that already
worked keep working?

Master = vnext `fb7407c5` with the fix stashed (same code as v0.0.106 on these paths). Fix = the
`1252-…` branch. ✗ = red on master, ✓ = green on master; every case is green on the fix.

### Every transition kind that runs a mapping

| Test | Master | What it checks |
|---|---|---|
| `ManualMappingThrows_FaultsWithAnIncidentAndAnOpenRecord_AndRetryReRunsTheHop` | ✗ | Manual transition, mapping throws: sync answer `200` + `status: F`, instance `F` in `fr-draft`, active `Execution:200002` incident naming `fr-advance`, an **unfinished** record. `retry` with a corrected body: `fr-active` / `A`, no active incident, mapped data written, one record in all. |
| `ManualMappingThrows_TheReservedCancelClosesTheFaultedInstance` | ✗ | Same fault; `cancel` closes it: `fr-cancelled` / `C`, no active incident, no open record. |
| `SharedTransitionMappingThrows_RetryReRunsIt` | ✗ | A shared transition (`availableIn: fr-draft`) faults with an open record; `retry` reaches `fr-active`. |
| `UpdateDataMappingThrows_AnswersFaulted_AndRetryReappliesTheUpdate` | ✗ | `updateData` faults: the sync answer says `F` (master: `A`), open `fr-update` record; `retry` re-applies the update with the retry's body, rests `A` in `fr-draft`. |
| `UpdateDataMappingThrows_TheReservedCancelClosesTheFaultedInstance` | ✗ | Same fault, closed with `cancel`. |
| `CancelMappingThrows_AnswersFaulted_AndRetryReRunsTheCancel` | ✗ | The reserved `cancel`'s own mapping throws on an Active instance: answer `F` (master: `A`), incident on `fr-cancel`; `retry` re-runs the cancel to `C`. |
| `CancelMappingThrows_CancellingAgainWithAValidBodyClosesTheInstance` | ✗ | Same fault, then `cancel` again with a valid body closes it. |
| `UpdateDataWithAKeyAlreadyTaken_FaultsWithAnOpenRecord_AndRetryWithAFreeKeyAssignsIt` | ✗ | morph-touch's key-generate shape: `update-data` on a keyless instance with a key another instance holds: `200` + `F`, incident `Instance:100019`, open record; `retry` with a free key → `A` with that key. |
| `FlowWithoutCancel_KeyTaken_RefusesCancel_AndRetryWithAFreeKeyIsTheWayOut` | ✗ | morph-touch's `rezervation` exactly: key-only `updateData`, **no cancel declared**. Key collision → `F`; `cancel` is refused (no such transition) and the instance stays `F`; `retry` with a free key → `A` with that key. |

### Every kind of target state and failure

| Test | Master | What it checks |
|---|---|---|
| `MappedDataFailsTheMasterSchema_FaultsWithAnOpenRecord_AndRetryReRunsTheHop` | ✗ | The mapping returns a field the closed master schema does not declare: Faulted with an open record; `retry` with a valid body reaches `fr-active`. |
| `MappingThatDoesNotCompile_FaultsEveryAttempt_AndTheReservedCancelClosesIt` | ✗ | A mapping that does not compile: every `retry` answers `F` on the same record (one record), `cancel` closes it. |
| `HopIntoAFinishStateThrows_RetryCompletesTheInstance` | ✗ | The failed hop targets a Finish state; `retry` completes the instance (`C`), no open record, no active incident. |
| `HopIntoASubFlowStateThrows_RetryStartsTheChild_AndTheChildCompletesTheParent` | ✗ | The failed hop targets a SubFlow state: no child before; `retry` starts the child, the child finishes, the parent completes with the child's output. |
| `HopIntoATimedStateThrows_RetryArmsTheStatesTimer_AndItFires` | ✗ | The failed hop targets a state with a 2 s scheduled transition; `retry` arms the timer and it fires — the fault's late cleanup does not cancel it. |

### Sync and async

| Test | Master | What it checks |
|---|---|---|
| `AsyncManualMappingThrows_FaultsWithAnOpenRecord_AndRetryReRunsTheHop` | ✗ | The manual case with `sync=false`: the job faults the instance with an open record; `retry` re-runs the hop. |
| `HopTheDefinitionMakesAsyncThrows_RetryReRunsIt` | ✗ | `executionType: "A"` on the transition (the caller's `sync=true` is overridden): open record, `retry` re-runs it. |
| `AsyncRetry_RightAfterTheFault_ReRunsTheHop` | ✗ | `retry` with `sync=false` right after the fault: the queued re-run is not cancelled by the fault's late cleanup. |
| `AsyncCancel_OnAFaultedInstance_RunsInlineAndClosesIt` | ✗ | `cancel` with `sync=false` on a Faulted instance runs inline: `200` + `status: C`, no open record. |

### What retry does with the body, the data and the incident

| Test | Master | What it checks |
|---|---|---|
| `RetryThatFailsAgain_StaysFaultedAndRetryable_OnTheSameRecord` | ✗ | A retry that throws again answers `F`, new active incident, **one** record (reused); a third, valid retry succeeds. |
| `SchemaFailureRetriedWithTheSameBody_FaultsAgainOnTheSameRecord_AndAValidBodyPasses` | ✗ | The same bad body faults again on the same record; a valid body passes. |
| `UpdateDataRetriedWithoutABody_RunsTheMappingOnNoBody` | ✗ | `retry` with no body re-runs the mapping on no body (the failed request's body is not replayed). |
| `AFailedHopWritesNoData_AndItsRetryWritesItOnce` | ✗ | The failed hop writes no data version; the retry writes it once. |
| `AfterRecovery_TheIncidentIsResolved_AndKeptInTheHistory` | ✗ | After recovery the incident is resolved and still listed (one, `Execution:200002`, `isResolved: true`). |
| `TheStateFunction_ShowsTheFaultAndItsIncident_ThenTheRecoveredState` | ✗ | The state function says `F` with `hasActiveIncident: true`, then `fr-active` / `A` with none. |

### Cancel on a Faulted instance

| Test | Master | What it checks |
|---|---|---|
| `CancellingAFaultedInstance_WhoseCancelMappingThrows_StaysFaultedAndRetryable` | ✗ | `cancel` on Faulted takes it over, then its own mapping throws: Faulted again, incident on `fr-cancel`, the failed advance's record closed and the cancel's open; `retry` re-runs the cancel to `C`. |
| `CancelNotAvailableInTheCurrentState_IsRefused_AndTheInstanceStaysFaultedAndRetryable` | ✗ | A cancel whose `availableIn` excludes the current state is refused (`Transition:100024`); the instance stays `F` with its incident; `retry` still works. |
| `TaskFailureAfterTheRecordWasSaved_TheReservedCancelClosesTheInstanceAndItsOpenRecord` | ✗ | A task failure (record already saved): `cancel` closes the instance and its open record. |

### Concurrent recovery requests

| Test | Master | What it checks |
|---|---|---|
| `RetryAndCancelAtTheSameTime_EndInOneConsistentState` | ✗ | Three rounds of retry + cancel at once: no `5xx`, at least one `200`, the instance ends `fr-active`/`A` **or** `fr-cancelled`/`C`, no active incident. |
| `TwoRetriesAtTheSameTime_RunTheHopOnce` | ✗ | Two retries at once: exactly one `200`, one record, nothing open. |
| `TwoCancelsAtTheSameTime_CloseTheInstanceOnce` | ✗ | Two cancels at once: at least one `200`, `C`, no active incident. |

### SubFlow parent and child

| Test | Master | What it checks |
|---|---|---|
| `ChildMappingThrows_RetryingTheChildReRunsItsHop` | ✗ | A child's mapping throws: child `F` with an open record (the parent faults too); `retry` on the child re-runs the hop and the child completes. Tripwire for the known issue: the parent stays `F`. |
| `ParentFaultedByItsChild_TheReservedCancelClosesEachOfThem` | ✗ | The child's fault faults the parent. Tripwire: the parent's retry answers `100028`. `cancel` closes the parent; the child is closed by its own `cancel`. |
| `ParentUpdateDataThrowsWhileItsChildRuns_CancellingTheParentAlsoCancelsTheChild` | ✗ | The parent's `updateData` fails while its child runs: the fault cascades to the child. Tripwire: the parent's retry is refused (`100028`). The parent's `cancel` closes the parent **and** the child. |
| `ParentFaultedByItsOutputMapping_TheReservedCancelClosesIt` | ✗ | The child completes with a note that makes the parent's output mapping throw: parent `F`, child `C`; `cancel` closes the parent. |

### Must not change (regression guards — green on master and on the fix)

| Test | Master | What it checks |
|---|---|---|
| `AnyKeyButTheReservedCancel_IsStillRefusedOnAFaultedInstance` (`fr-exit`, `fr-advance`, `fr-update`) | ✓ ✓ ✓ | Every other key, `exit` and `updateData` included, still answers `400 Instance:100018` on a Faulted instance; the instance and its incident are unchanged. |
| `CancelOnACompletedInstance_IsStillRefused` | ✓ | A Completed instance still answers `400 Instance:100018` to `cancel`. |
| `TaskFailureAfterTheRecordWasSaved_RetryStillReRunsTheHop` | ✓ | A task failure (the case that already worked): `retry` still re-runs the hop, one record. |
| `ASuccessfulPath_LeavesOneFinishedRecordPerHop` | ✓ | A normal path: one finished record per hop, nothing open, no incident. |

## Why this exists

burgan-tech/vnext#1252 (P0, opened 2026-11-12; from burgan-tech/vnext-client-sdk-core#136 — measured by the
consumer on 0.0.99 on 2026-10-16, worse on 0.0.104 on 2026-11-09 — and #119). A transition's mapping,
its instance-key check and its data write (with the master-schema check) run in
`CreateTransitionRecordStep` **before** the transition record is saved. A failure there faulted the
instance with an incident but no unfinished record: `retry` answered `400 Instance:100028` ("No
incomplete transition found") and the reserved `cancel` answered `400 Instance:100018`, because Faulted was
terminal for every key. Only a database edit could recover the instance. The sync answer of a failed
`updateData` or `cancel` also said `status: A` for a Faulted instance.

Measured on vnext master `fb7407c5`, whose code on these paths is identical to the v0.0.106 release
(`git diff v0.0.106 fb7407c5` changes nothing in the record step's order, the terminal check, the instance
load, the fault path or retry's record lookup): a keyless instance's `update-data` with a key another
instance holds answered `200` with `status: A`; the instance was `F / F` with incident `Instance:100019`
and no record of the hop; `retry` answered `100028` and `cancel` `100018`. (Builds before 0.0.104 happened
to leave a failed manual transition Busy, which the reserved cancel could close; 0.0.104 removed that
accident — the consumer's "it got worse".)

Fixed in vnext by: the failed hop's record saved unfinished before the fault (caller-requested hops
only); the reserved `cancel` admitted on a Faulted instance (takeover: Faulted → Busy, incidents
resolved, open records closed, one transaction); the persisted fault mirrored onto the run's own
aggregate so the sync answer says `F`.

Measured on a master build (`fb7407c5`, the fix stashed), 2026-11-24: **34 of 40 red**, the 6 regression
guards green. Earlier the same day the first ten tests were 9 of 10 red; psql: every failed instance
`F / F`, one transition record (the start), one unresolved incident (`Execution:200002`, or
`SchemaValidationException` for the schema case).

## Known limits (vnext, documented as a known issue)

- A parent faulted by its child's own fault cannot be retried (`100028`: its correlation is closed, it has
  no open record), and a successful retry of the child does not resume it. `cancel` both.
- A parent that faulted on its own while its child ran keeps the correlation open; its retry is handed to
  the child, which has nothing to re-run (`100028`), after the parent was already unfaulted — the parent
  is left `A` with no incident (burgan-tech/vnext#1338's refused-retry problem). `cancel` it instead.
- Each of these is pinned as a tripwire in the tests named above: fixing one flips its assertions.
- An automatic transition cannot declare a mapping (publish refuses it), so the lab has none. A scheduled
  or event hop that fails in its mapping keeps no record for retry (vnext#1338); `cancel` closes it.

## How to run

Prerequisites: the runtime under test running locally as `core`
(`etc/docker/run-docker.sh up core` in vnext) and `VNEXT_BASE_URL` in `test.runsettings` pointing at it.
No MockLab, no partner domain. The SDK publishes the flows itself.

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~FaultedRecoveryLab" -v minimal
```

**Pass criterion:** 40/40. On a runtime without the fix only the six regression guards pass.

Flows: `core/Workflows/faulted-recovery-lab/` (regenerate with `build-faulted-recovery-lab.py`; never edit
the base64 blobs), master schema `core/Schemas/faulted-recovery-lab/`. One class on purpose: the SubFlow
tests share the parent/child pair and nothing else starts those flows.

## Run record

| Date | Runtime | Result |
|---|---|---|
| 2026-11-24 | vnext master `fb7407c5` (fix stashed; same code as v0.0.106 on these paths) | 40-case lab: 34 red / 6 green (the regression guards). 17-test lab earlier: 16/17 red |
| 2026-11-24 | vnext `570e08e2` (branch `1252-bug-make-instances-faulted-by-a-mapping-or-schema-failure-recoverable`) | 40-case lab: **40/40, three runs in a row** (on the same code before the commit). Full suite on the commit: 765 passed / 1 failed / 132 skipped, the lab 40/40 — the one red is `InstanceIdFilterLab` `NotAGuid`, a local-DB artefact (`idf-reader` 1.0.0 published 2026-11-17, before fixture commit `601b8f1`) |

Checked with tests and psql rows; not confirmed against traces (the OpenObserve and Postgres MCP servers were
unreachable during these runs).
