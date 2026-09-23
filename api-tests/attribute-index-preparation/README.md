# Manual attribute index preparation (current contract)

Publication creates no index jobs or generated columns. `wf indexes generate` writes an offline SQL batch;
the DBA executes it in a maintenance window. The former automatic scenario below is historical.

Run `test_manual_indexes.py` against the locally built worktree runtime and an isolated test domain:

```sh
VNEXT_BASE_URL=http://localhost:<port> \
VNEXT_TEST_DOMAIN=<isolated-domain> \
VNEXT_TEST_PG_CONTAINER=<isolated-postgres> \
VNEXT_TEST_DATABASE=<isolated-database> \
VNEXT_ALLOW_ISOLATED_TEST_DDL=true \
python3 api-tests/attribute-index-preparation/test_manual_indexes.py
```

`NODE_BINARY` and `VNEXT_CLI_ROOT` may override the local Node executable and sibling CLI checkout.
The harness uses a unique flow, executes generated SQL only on that fixture, and leaves it for inspection.
It never modifies pre-existing workflow schemas. The CLI itself never contacts the database/API.

Verified 2026-09-14: **4 checks passed**, local Orchestration built from the dirty worktree, PostgreSQL 18.6,
`autoindexdirect` isolated domain on localhost:4301. Covered: no automatic work on first publication;
CLI generation leaves DB unchanged; explicit SQL creates ready catalog and replay preserves OIDs;
subsequent indexed Master update creates no jobs and leaves catalog unchanged.

## Historical automatic experiment (superseded)

# Automatic attribute index preparation

Run against an isolated **locally built** runtime with automatic preparation enabled. The selected `directly: true` path works
with the existing background-job poller disabled. The script publishes uniquely named components through HTTP.
PostgreSQL is inspected with read-only SQL; one read lock is held to prove the no-op path does
not request ACCESS EXCLUSIVE. No direct optimization DDL is performed by the test.

```sh
VNEXT_BASE_URL=http://localhost:4301 \
VNEXT_TEST_DOMAIN=autoindexdirect \
VNEXT_TEST_PG_CONTAINER=auto-index-pg \
VNEXT_TEST_DATABASE=vNext_autoindex_direct \
python3 api-tests/attribute-index-preparation/test_auto_indexes.py
```

Expected checks: unindexed schema; Master package update affects latest but not pinned flow;
trigram/B-tree generation; no-op while a conflicting read lock is held; conflict/exception rollback;
workflow reference update. Job status and retry assertions require a dedicated clean job database.

## 2026-09-11 result: blocked, not passed

Worktree runtime on `localhost:4301`, PostgreSQL 18.6, Dapr 1.16.19, isolated Redis/Scheduler.
Some initial jobs completed and created projections. Immediate Dapr callbacks also arrived
before Aether's arming confirmation committed; dispatcher acknowledged them as `not found in
an active state`, then their rows became Scheduled and stayed there. The scenario timed out
at its first job-completion gate. Later checks are **not verified**.

Aether 1.0.39 SDK changes require separate authorization under the runtime repository policy.
No sleep-based timing workaround or direct DB status repair is included in this test. Re-run
all checks after the SDK race is fixed. Aether source was not modified during this investigation.

## 2026-09-11 direct-path follow-up: passed

User selected `directly: true` without modifying Aether. Automatic preparation was enabled;
`BackgroundJob:WithHostedService=false` was deliberately retained during verification. A fresh
isolated database (`vNext_autoindex_direct`, domain `autoindexdirect`) preserved the earlier
failed-run evidence. All **6 checks passed**, and **13 jobs completed with zero retries**.
The 409 conflict and 500 extra-data cast failure are intentional rollback probes, not unexpected
failures. Tables are small/empty fixtures; this run is not a populated 10M-row migration benchmark.

This closes the direct-path scenario. Commit-before-schedule crashes and Pending/Retrying
recovery remain SDK limitations; the earlier failure above has not been erased or fixed.
