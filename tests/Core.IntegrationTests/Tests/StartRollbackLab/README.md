# StartRollbackLab — integration test

One test class, **eight tests**, over the `start-rollback-lab` flows (domain `core`).

| Test | What it checks |
|---|---|
| `RootStart_WithACustomer_CommitsTheInstanceWithItsMappedData` | Happy path: a root start whose start mapping succeeds is committed with its mapped data (`root-waiting`, `A`, `startMapped: true`). |
| `RootStart_WhoseMappingFails_LeavesNoInstance_AndTheKeyStartsCleanlyAfterwards` | A root start whose start mapping throws leaves no instance under its key (`GET` by key is `404`); the same key with a valid payload then starts a real instance with its data. |
| `SubStart_WithAnIdWhoseMappingFails_LeavesNoInstance_AndTheIdStartsCleanlyAfterwards` | The same through `sub/instances/start`, the call a cross-domain parent makes: a pre-assigned `id` and strict idempotency. The failed start leaves no instance under the id; a second start with the same id succeeds. |
| `ChildStart_WithACustomer_StartsTheChild` | Happy path: a parent whose data carries a customer starts its SubFlow child, which reaches `child-waiting` / `A`. |
| `ChildStart_WhoseMappingFails_LeavesNoChild_AndTheParentFaults` | A child whose start mapping throws leaves no child instance; the parent faults with an active incident that names the start-mapping failure. |
| `RetryingTheParent_RestartsTheChildStart_InsteadOfRetryingALeftoverChild` | The faulted parent's retry restarts the child start (it fails again, there is still no customer) and re-faults the parent with a **new** incident; still no child instance. |
| `SubProcessStart_WithACustomer_StartsTheProcessWithItsMappedData` | Happy path: a parent whose data carries a customer starts a process through its `SubProcess` task; the process reaches `process-waiting` / `A` with its mapped data. |
| `SubProcessStart_WhoseMappingFails_LeavesNoProcess_AndTheParentFaults` | A process whose start mapping throws leaves no process instance; the `SubProcess` task fails and the parent faults with an active incident that names the start-mapping failure. |

## Why this exists

Creating an instance runs in one transaction: the runtime inserts the instance row, then runs the
start transition's mapping, then appends the initial data version. Before the fix
(`InstanceCommandAppService.PrepareInstanceAsync`), that transaction was committed whether or not
these steps succeeded. (The start transition itself runs after the commit. Its own failure keeps the
committed instance, as it always did: faulted when one of its steps fails, unstarted when it fails
before its first step. Not covered here.) A start whose mapping threw answered with the error (`400 Execution:200002`)
but kept the instance, without its data. Reported as burgan-tech/vnext#1254 (2026-11-12) and raised
again by the AI Quality Gate review of burgan-tech/vnext#1326 (2026-11-20). It is fixed in vnext `8fa1d2e2`
(commit only on success, roll back otherwise).

Measured on a master build (`e4faefa8`, the fix stashed): **5 of 8 red**, the three happy paths
green (2026-11-23). The leftovers below come from one run each: the first three from the six-test
run of 2026-11-20, the SubProcess one from the eight-test run of 2026-11-23 (see the run record).

- **Root start.** The leftover stayed `A` in `root-waiting` with **0** data rows. A second start
  with the same key answered `200` with that empty instance (the idempotency probe), so a real one
  was never created. The test stops at the leftover, so this part was measured by hand on the same
  build: `GET` by key afterwards showed the instance with no `attributes`.
- **`sub/instances/start`.** The leftover child stayed **`B`** (a SubFlow child is created Busy,
  and no parent faults it here) with 0 data rows, under its pre-assigned id.
- **Child started by its parent.** The leftover child was faulted together with the parent
  (`child-fault`, logged as `Child subflow fault applied`). The parent's retry probes the child
  before restarting it: it found the faulted child, unfaulted the parent (resolving its incident)
  and handed the retry to the child, which failed. The parent was left **`A` in
  `parent-waits-child` with no active incident**, waiting on a dead child.
- **Process started by a `SubProcess` task.** The leftover process stayed **`B`** in
  `process-waiting` with 0 data rows, under the id the task had assigned. The parent faulted, but a
  parent's fault cascades only to its SubFlow children, so nothing ever reached the leftover.

## How to run

Prerequisites: the runtime under test running locally as `core`
(`etc/docker/run-docker.sh up core` in vnext) and `VNEXT_BASE_URL` in `test.runsettings` pointing
at it. No MockLab, no partner domain. The SDK publishes the flows itself.

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~StartRollbackLab" -v minimal
```

**Pass criterion:** 8/8. On a runtime without the fix the three happy paths pass and the other five
fail: `RootStart_WhoseMappingFails…`, `SubStart_WithAnIdWhoseMappingFails…`,
`ChildStart_WhoseMappingFails…`, `SubProcessStart_WhoseMappingFails…` and `RetryingTheParent…` (it
times out: the parent is left `A`).

## The fixture

`core/Workflows/start-rollback-lab/`, generated by `build-start-rollback-lab.py` from `./src/*.csx`
(edit the `.csx`, rerun the script, never hand-edit the base64 blobs):

- `start-rollback-lab-root` (type `F`) and `start-rollback-lab-child` (type `S`) share one start
  mapping, `RequireCustomerStartMapping.csx`. It throws when the start payload has no `customerId`,
  and otherwise maps it to `{ customerId, startMapped: true }`. Each flow has one state with no
  transitions, so a started instance stays observable.
- `start-rollback-lab-parent` (type `F`) auto-enters `parent-waits-child` (`stateType: 4`,
  `subFlow.type: "S"`). `ParentToChildMapping.csx` passes `customerId` on only when the parent's own
  data has one.
- `start-rollback-lab-process` (type `P`) has the same start mapping and one state,
  `process-waiting`. `start-rollback-lab-sp-parent` (type `F`) starts it from its initial state's
  onEntry through the `SubProcess` task `start-rollback-lab-start-process`
  (`core/Tasks/start-rollback-lab/`, hand-written: it has no code). `ParentToProcessMapping.csx`
  passes `customerId` on the same way.

The parent is started with `sync=false`: with `sync=true` the child's failure would be the answer
of the parent's own start call (see the SubflowStartFailureLab README). The retry cannot succeed:
a restart re-runs the original hop and carries no new data, and the parent's data still has no
customer. What it proves is that the retry restarts the child start instead of handing the retry
to a leftover child.

The child and process tests compare that flow's instance list before and after a parent run, so
they stay in one class: nothing else may start instances of those flows at the same time.

## Checking the database

```sql
-- leftovers: instances with no data version (run per schema: start_rollback_lab_root / _child / _process)
select i."Id", i."Status", i."CurrentState",
       (select count(*) from start_rollback_lab_root."InstancesData" d where d."InstanceId" = i."Id") as data_rows
from start_rollback_lab_root."Instances" i
where i."CreatedAt" >= '<run start, UTC>'
order by i."CreatedAt";
```

## Run record

| Date | Runtime | Lab | psql (rows created by the run) |
|---|---|---|---|
| 2026-11-23 | vnext `master` @ `e4faefa8` (fix stashed), one run, before the incident-message assertion was added | 5/8 red, the three happy paths green | process: 1 leftover `B` with 0 data rows, next to the happy path's `A` with data; the failed SubProcess parent `F` |
| 2026-11-23 | vnext `8fa1d2e2` (the fix) | **8/8** | process: 1 row, with data (happy path), no leftover; SubProcess parents: one `A`, one `F` whose incident is the task's `Failed to execute transition mapping: start-rollback-lab: the start payload has no customerId`; root and child: 2 rows each, all with data |
| 2026-11-20 | vnext `master` @ `e4faefa8` (fix stashed), the first six tests, one run | 4/6 red, the two happy paths green | root: 1 leftover `A` with 0 data rows; child: 1 leftover `B` and 2 `F`, all with 0 data rows; the retried parent `A` with no active incident (`HasActiveIncident` false) |
| 2026-11-20 | the fix (same runtime code as `8fa1d2e2`), the first six tests | **6/6** | root: 2 rows, both with data; child: 2 rows, both with data; no leftovers. Parent: the retry resolved the first incident and raised a new one |
| 2026-11-20 | same — **full suite** | 717 passed / 9 failed / 129 skipped. The 9 fail identically on a master build (`e4faefa8`): AccountOpening ×6, AuthorizationChainLab ack ×2, InstanceIdFilterLab `NotAGuid` ×1 — no new reds | — |

Local core (`run-docker.sh up core`), `VNEXT_BASE_URL=http://localhost:4201`. The MCP servers
(openobserve, postgres, redis) were not connected; verified with psql and the host logs.
