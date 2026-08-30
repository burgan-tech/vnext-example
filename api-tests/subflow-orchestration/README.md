# subflow-orchestration — load & concurrency probes

Scripts in this folder exercise `subflow-orchestration-parent` /
`-child` / `-grandchild` (domain `core`), a 3-level SubFlow chain used as
the platform's reference scenario for parent/child correlation, updateData
fan-in, and subflow terminal-relay behaviour.

## Scenarios

| Script | Neyi denetliyor | Neden eklendi |
|---|---|---|
| `updatedata-concurrency-test.py` | Concurrent `updateData` correctness under the busy-as-mutex locking model | feature/busy-as-mutex-locking |
| `terminal-relay-load.py` | child-completed → parent-resumed latency, server-side (subflow terminal relay + outbox wakeup signal) | 2026-08-29 event-publish-modes plan (`docs/superpowers/plans/2026-08-29-event-publish-modes.md` in the vnext repo) |

---

## terminal-relay-load.py

### Neyi denetliyor

The vnext runtime relays a subflow's terminal event to its parent
**post-commit**, same-domain, targeting a relay gap of effectively zero
(outbox+Inbox remain as a durable fallback, accelerated by a wakeup signal —
they are not the primary delivery path for the same-domain case). This
script drives N parent instances through a full async subflow lifecycle and
measures the **child-completed → parent-resumed** latency directly from
persisted Postgres timestamps — never from client polling, which is used
only to drive the scenario and as a black-box stuck check.

### Dependencies + install

```bash
pip install psycopg2-binary
```

The script checks for `psycopg2` at startup and exits with a clear message
if it is missing (no docker-exec/psql fallback — this script always
connects via `--db-dsn`, unlike `updatedata-concurrency-test.py` which
shells out to `docker exec vnext-postgres psql`).

### Preconditions

- Orchestration host reachable at `--base-url` (default `http://localhost:4201`).
- Docker infra (Postgres) up and reachable at `--db-dsn`.
- The three `subflow-orchestration-*` flows already published. If not yet
  published in this environment, run once:
  ```bash
  python3 api-tests/subflow-orchestration/updatedata-concurrency-test.py --publish --iterations 0
  ```
  (or publish via the CLI / any other existing mechanism — this script does
  not publish).

### Run command

```bash
python3 api-tests/subflow-orchestration/terminal-relay-load.py \
  --base-url http://localhost:4201 \
  --db-dsn postgresql://postgres:postgres@localhost:5432/Aether_WorkflowDb \
  --instances 20 \
  --concurrency 5
```

Parameters:

| Flag | Default | Meaning |
|---|---|---|
| `--base-url` | `http://localhost:4201` | Orchestration host |
| `--db-dsn` | `postgresql://postgres:postgres@localhost:5432/Aether_WorkflowDb` | Matches `etc/docker` defaults in the vnext repo (`vnext-postgres`, user/pass `postgres`, db `Aether_WorkflowDb`) |
| `--instances` | 20 | Total parent instances driven through the full lifecycle |
| `--concurrency` | 5 | Max instances driven concurrently (thread pool size) |

### What it measures, and where the numbers come from

Each iteration starts a parent instance with `updateThreshold=1`, sends one
`update-parent-progress` updateData to fire the auto-collect gate into
`parent-subflow-state` (spawning the child), drives the child into its own
subflow (spawning the grandchild), then completes the grandchild — which
resumes the child, which completes it, which resumes the parent. Client
polling only drives these steps and detects stuck instances; it is never the
source of the reported histogram.

After the drive phase, the script queries Postgres **once** (a single
round trip, `UNNEST`-joined) for every successfully-driven `(parent_id,
child_id)` pair:

- **Child terminal timestamp**: `"FinishedAt"` on
  `"subflow_orchestration_child"."InstanceTransitions"` where
  `"ToState" = 'child-completed'` — the child workflow's Finish state.
- **Parent resume timestamp**: `"StartedAt"` on
  `"subflow_orchestration_parent"."InstanceTransitions"` where
  `"FromState" = 'parent-subflow-state'` and `"TriggerType" = 1`
  (Automatic) — this is the fresh transition record created when
  `RunAutomaticTransitionsStep` fires the parent's next automatic
  transition after `SubflowCompletionService.ResumePipelineAsync` resumes
  the pipeline. The resume itself creates **no** transition record (it
  runs with an empty `TransitionKey`, "for logging purposes only"), so the
  first fresh record on `FromState = parent-subflow-state` is the earliest
  reliable marker of the relay having reached the parent. `StartedAt` (not
  `FinishedAt`) is used so the gap excludes the parent's own local pipeline
  processing time.

  See the script's module docstring for the full schema investigation,
  including why `InstancesCorrelations."InstanceId"` (a legacy, sometimes-
  NULL shadow column) is avoided in favour of `"ParentInstanceId"`, and a
  worked example against a live run's rows.

`gap_ms = parent_resume_ts - child_terminal_ts`, computed per pair; p50/p95/
p99/max reported over all successfully-measured pairs.

The script also counts `sys_queues."OutboxMessages"` and
`sys_queues."InboxMessages"` rows created since the run's start timestamp,
to quantify the queue-row cost the pure-outbox/inbox backup path produces
for this scenario (informational — not a pass/fail criterion).

### Failure thresholds (each printed as a separate verdict)

| Verdict | Threshold |
|---|---|
| p99 relay gap | **≤ 250 ms** (design objective — hard threshold) |
| p95 relay gap | **FAIL if > 1000 ms** |
| Stuck instances | **FAIL if any instance doesn't reach `parent-completed` within 30 s** of the terminal hop being triggered |

Overall exit code is 0 only if all three verdicts pass and no instance
failed for another reason (HTTP error, missing correlation, etc.).

### How to read the output

```
== Drive phase done in 8.3s: 20/20 completed, 0 stuck, 0 failed-other ==

== Server-side relay gap (parent_resume_ts - child_terminal_ts) ==
  samples : 20
  p50     : 28.4 ms
  p95     : 61.2 ms
  p99     : 74.0 ms
  max     : 74.0 ms

== Queue-row cost since run start (...) ==
  sys_queues.OutboxMessages rows: 60
  sys_queues.InboxMessages  rows: 60

================================================================
VERDICT p99 <= 250 ms (design objective) : PASS (p99=74.0 ms)
VERDICT p95 <= 1000 ms                    : PASS (p95=61.2 ms)
VERDICT no instance stuck > 30s           : PASS (stuck=0)
================================================================
SONUC: PASS
```

- **Drive phase** counts and errors are client-observed (HTTP/polling) —
  useful to sanity-check the scenario ran cleanly, but not the latency
  metric.
- **Server-side relay gap** is the headline metric. A `WARNING` line above
  it means some pair could not be resolved to both timestamps (e.g. the
  driving sequence didn't actually reach `child-completed` for that
  instance) — those pairs are excluded from the percentiles, not
  silently zero-filled.
- Exit code: `0` on overall PASS, `1` otherwise (check each VERDICT line
  to see which one failed).
