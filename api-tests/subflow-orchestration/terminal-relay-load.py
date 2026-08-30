#!/usr/bin/env python3
"""
terminal-relay-load.py — subflow terminal relay + outbox wakeup signal load probe

python3 api-tests/subflow-orchestration/terminal-relay-load.py --instances 20 --concurrency 5

Measures: child-completed -> parent-resumed latency, SAME-DOMAIN, for the
subflow-orchestration example flows (parent -> child -> grandchild). This
validates the vnext runtime change where subflow terminal events relay to the
parent post-commit (design target: gap ~= 0 same-domain) with outbox+Inbox as
a durable backup accelerated by a wakeup signal (see
docs/superpowers/plans/2026-08-29-event-publish-modes.md in the vnext repo).

Reused flow (same one exercised by updatedata-concurrency-test.py in this
folder — do NOT invent new flow/domain/transition keys):
  domain            = core
  parent            = subflow-orchestration-parent
  child             = subflow-orchestration-child
  grandchild        = subflow-orchestration-grandchild
  parent schema     = subflow_orchestration_parent
  child schema      = subflow_orchestration_child
  grandchild schema = subflow_orchestration_grandchild

Each iteration drives ONE parent instance through its full async lifecycle:
  start (updateThreshold=1) -> parent-collect -> (1x update-parent-progress,
  fires the auto-collect gate) -> parent-subflow-state (spawns child) ->
  child-manual-state -> proceed-to-subflow -> child-subflow-state (spawns
  grandchild) -> grandchild-initial -> complete-grandchild -> grandchild
  resumes child -> child-completed -> child resumes parent ->
  parent-completed.

We only care about the LAST hop for the headline metric: child-completed
(child's own terminal transition) -> parent-resumed (the automatic
transition the parent's pipeline fires once SubflowCompletionService resumes
it). The grandchild->child hop happens too but is not part of this probe's
verdict (the brief scopes the measurement to child -> parent, same-domain).

--- Server-side timestamp source (schema investigated against a live local
    Postgres via `docker exec vnext-postgres psql ...`, 2026-08-30) ---------

Table  "<schema>"."InstanceTransitions"  (one row per attempted transition;
BBT.Workflow.Domain/Instances/InstanceTransition.cs +
BBT.Workflow.Infrastructure/Data/InstancesModelCreatingExtensions.cs):
  InstanceId, TransitionId, FromState, ToState, TriggerType (0 Manual,
  1 Automatic, 2 Scheduled, 3 Event), StartedAt, FinishedAt (NULL while the
  transition is still in flight / paused for a blocking SubFlow).

Child terminal timestamp:
  MAX("FinishedAt") on the CHILD schema's InstanceTransitions row that has
  ToState = 'child-completed' (the child workflow's Finish state, see
  core/Workflows/subflow-orchestration/subflow-orchestration-child.json).
  This is the moment the child instance itself reached Completed.

Parent resume timestamp — empirically NOT the row that entered
parent-subflow-state (confirmed live: that row completes immediately when
the subflow starts, StartedAt/FinishedAt both long before the child
finishes). SubflowCompletionService.ResumePipelineAsync
(src/BBT.Workflow.Application/SubFlow/Services/SubflowCompletionService.cs)
resumes the parent pipeline with ResumeFrom = ClearBusyOnResumeStep (79) and
an EMPTY TransitionKey ("for logging purposes only") — no transition record
is created for the resume step itself. RunAutomaticTransitionsStep (90) then
evaluates the parent's current state (parent-subflow-state) and fires the
NEXT automatic transition (auto-parent-to-completed in this flow), which
runs through the FULL pipeline as a brand-new TransitionExecutionContext and
therefore DOES get a fresh CreateTransitionRecordStep (order 20) row. That
new row is what we key on:
  MIN("StartedAt") on the PARENT schema's InstanceTransitions row with
  FromState = 'parent-subflow-state' AND TriggerType = 1 (Automatic).
  StartedAt (not FinishedAt) is used deliberately: it is the earliest
  recordable instant of the pipeline execution that resulted from the relay,
  so the gap excludes the parent's own local pipeline processing time
  (task execution, ChangeState, Finalize) and isolates the relay+eval hop.

Verified live against real rows from a prior updatedata-concurrency-test.py
run (pid=12bcde6a-..., cid=4831f565-...):
  child  InstanceTransitions: ToState='child-completed' FinishedAt=07:50:47.373889
  parent InstanceTransitions: FromState='parent-subflow-state' TriggerType=1
                               StartedAt=07:50:47.40735  (gap ~= 33 ms)

NOTE on InstancesCorrelations: the table has BOTH "ParentInstanceId" (NOT
NULL, the real FK — confirmed via psql's `\\d` on a live instance) and a legacy
"InstanceId" shadow column that is NULL on some rows (a leftover, not
reliably populated). The reference script in this folder
(updatedata-concurrency-test.py) filters on "InstanceId" for a single
synchronous debug read — harmless there since it always queries a row it
just created — but it is NOT safe as a general join key. This script uses
"ParentInstanceId" throughout.

Outbox/Inbox queue-row cost (per BBT.Aether.Infrastructure
InboxModelBuilderExtensions / OutboxModelBuilderExtensions): both tables are
schema-less at the C# model level, resolved at runtime via SET LOCAL
search_path into the fixed "sys_queues" schema — "sys_queues"."OutboxMessages"
and "sys_queues"."InboxMessages", both with a "CreatedAt" column. We count
rows with CreatedAt >= the run's start timestamp to quantify the queue-row
cost the run produced (pure-outbox event volume for this scenario).

Preconditions: orchestration host on --base-url (default localhost:4201) +
docker infra + Postgres reachable via --db-dsn; the 3 subflow-orchestration
flows already published (use updatedata-concurrency-test.py --publish once
if needed — this script does not publish).
"""

import argparse
import concurrent.futures as cf
import json
import statistics
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid

try:
    import psycopg2
    import psycopg2.extras
except ImportError:
    psycopg2 = None

DOMAIN = "core"
PARENT_WF = "subflow-orchestration-parent"
CHILD_WF = "subflow-orchestration-child"
GRANDCHILD_WF = "subflow-orchestration-grandchild"
PARENT_SCHEMA = "subflow_orchestration_parent"
CHILD_SCHEMA = "subflow_orchestration_child"
USER = "11111111-1111-1111-1111-111111111111"

PRINT_LOCK = threading.Lock()

# ---------------------------------------------------------------------------
# Pass/fail thresholds (per task brief — report each verdict separately)
# ---------------------------------------------------------------------------
P99_TARGET_MS = 250.0     # design objective, hard threshold
P95_FAIL_MS = 1000.0      # p95 above this ⇒ FAIL
STUCK_TIMEOUT_S = 30.0    # any instance not reaching parent-completed within
                          # this bound (measured from grandchild-complete
                          # trigger) ⇒ FAIL


def log(it, msg):
    with PRINT_LOCK:
        print(f"  [it{it:03d}] {msg}", flush=True)


def http(base_url, method, path, body=None, timeout=30):
    url = f"{base_url}/api/v1/{path}"
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    req.add_header("user_reference", USER)
    req.add_header("x-request-id", str(uuid.uuid4()))
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode()
            return resp.status, (json.loads(raw) if raw else {})
    except urllib.error.HTTPError as e:
        raw = e.read().decode()
        try:
            return e.code, json.loads(raw)
        except json.JSONDecodeError:
            return e.code, {"raw": raw}
    except Exception as e:  # connection errors
        return -1, {"error": str(e)}


def find_field(obj, keys):
    """Defensive parse: find one of `keys` anywhere in a nested dict body."""
    if isinstance(obj, dict):
        for k, v in obj.items():
            if k in keys and isinstance(v, str):
                return v
        for v in obj.values():
            r = find_field(v, keys)
            if r:
                return r
    return None


def get_state(base_url, workflow, instance_id):
    status, body = http(base_url, "GET",
                         f"{DOMAIN}/workflows/{workflow}/instances/{instance_id}/functions/state")
    if status != 200:
        return None, None
    return find_field(body, {"state", "currentState"}), find_field(body, {"status"})


def wait_for(base_url, workflow, instance_id, predicate, timeout_s, poll=0.4):
    deadline = time.time() + timeout_s
    last = (None, None)
    while time.time() < deadline:
        last = get_state(base_url, workflow, instance_id)
        if predicate(*last):
            return True, last
        time.sleep(poll)
    return False, last


def run_iteration(base_url, it):
    """Drives one parent instance through its full async subflow lifecycle
    (black-box, client-polling only used to DRIVE the scenario and to detect
    stuck instances — never as the latency source)."""
    r = {"it": it, "ok": False, "errors": [], "stuck": False,
         "parent_id": None, "child_id": None}
    try:
        # 1) Start (async). updateThreshold=1 so a single updateData fires the gate.
        status, body = http(base_url, "POST", f"{DOMAIN}/workflows/{PARENT_WF}/instances/start",
                             {"testId": f"relay-{it}-{uuid.uuid4().hex[:8]}", "updateThreshold": 1})
        if status not in (200, 201, 202):
            r["errors"].append(f"start HTTP {status}: {body}")
            return r
        pid = body.get("id") or body.get("Id")
        r["parent_id"] = pid
        log(it, f"parent {pid} started")

        ok, last = wait_for(base_url, PARENT_WF, pid, lambda s, st: s == "parent-collect", 30)
        if not ok:
            r["errors"].append(f"parent-collect unreachable (last={last})")
            return r

        # 2) Single updateData -> fires auto-collect-threshold-reached gate.
        st, bd = http(base_url, "PATCH",
                      f"{DOMAIN}/workflows/{PARENT_WF}/instances/{pid}/transitions/update-parent-progress",
                      {"updateNonce": f"{it}-{uuid.uuid4().hex[:6]}"})
        if st not in (200, 202):
            r["errors"].append(f"update-parent-progress HTTP {st}: {bd}")
            return r

        ok, last = wait_for(base_url, PARENT_WF, pid,
                             lambda s, st_: s in ("parent-subflow-state", None) and st_ in ("A", "B"),
                             30)
        # parent-subflow-state delegates the state-function view to the child once the
        # subflow is active, so we may never observe the raw parent state via the client;
        # that's expected — proceed regardless as long as the request path is progressing.

        # 3) Locate the child correlation (child_id) via the state function delegation:
        #    once the parent is in parent-subflow-state, GET .../functions/state on the
        #    PARENT resolves to the CHILD's view, so we drive by workflow/state directly
        #    once we discover the child id from the DB (server-side data is authoritative
        #    for identity; this is not part of the latency measurement).
        child_id = _poll_child_id(base_url, pid, it)
        if not child_id:
            r["errors"].append("child correlation not found within timeout")
            return r
        r["child_id"] = child_id
        log(it, f"child {child_id} correlated")

        ok, last = wait_for(base_url, CHILD_WF, child_id,
                             lambda s, st_: s == "child-manual-state" and st_ == "A", 30)
        if not ok:
            r["errors"].append(f"child-manual-state unreachable (last={last})")
            return r

        st, bd = http(base_url, "PATCH",
                      f"{DOMAIN}/workflows/{CHILD_WF}/instances/{child_id}/transitions/proceed-to-subflow")
        if st not in (200, 202):
            r["errors"].append(f"child proceed HTTP {st}: {bd}")
            return r

        gc_id = _poll_grandchild_id(base_url, child_id, it)
        if not gc_id:
            r["errors"].append("grandchild correlation not found within timeout")
            return r

        ok, _ = wait_for(base_url, GRANDCHILD_WF, gc_id,
                          lambda s, st_: s == "grandchild-initial" and st_ == "A", 30)
        if not ok:
            r["errors"].append("grandchild-initial unreachable")
            return r

        # 4) Fire the terminal hop we are measuring: grandchild completes -> resumes
        #    child -> child completes -> resumes parent. Start the stuck-check clock here.
        stuck_clock_start = time.time()
        st, bd = http(base_url, "PATCH",
                      f"{DOMAIN}/workflows/{GRANDCHILD_WF}/instances/{gc_id}/transitions/complete-grandchild")
        if st not in (200, 202):
            r["errors"].append(f"complete-grandchild HTTP {st}: {bd}")
            return r

        # Bounded black-box stuck check ONLY (never the latency source): parent must
        # reach Completed within STUCK_TIMEOUT_S of triggering the terminal hop.
        ok, last = wait_for(base_url, PARENT_WF, pid, lambda s, st_: st_ == "C",
                             STUCK_TIMEOUT_S, poll=0.3)
        if not ok:
            r["stuck"] = True
            r["errors"].append(f"parent did not reach Completed within {STUCK_TIMEOUT_S:.0f}s "
                                f"(last={last}, elapsed={time.time() - stuck_clock_start:.1f}s)")
            return r

        log(it, f"parent Completed ({time.time() - stuck_clock_start:.2f}s after trigger)")
        r["ok"] = True
        return r
    except Exception as e:
        r["errors"].append(f"exception: {e}")
        return r


def _poll_child_id(base_url, pid, it, timeout_s=30, poll=0.5):
    """Polls the DB is avoided on the hot driving path per-instance to keep the script
    usable without a DB for the drive phase in principle, but discovering the child id
    has no non-DB signal once the parent delegates its state view — use the DB here.
    This call is NOT part of the measured latency; it only unblocks the next HTTP step."""
    if psycopg2 is None:
        return None
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        cid = _query_scalar(
            f'SELECT "SubFlowInstanceId" FROM "{PARENT_SCHEMA}"."InstancesCorrelations" '
            f'WHERE "ParentInstanceId" = %s ORDER BY "CreatedAt" DESC LIMIT 1',
            (pid,))
        if cid:
            return str(cid)
        time.sleep(poll)
    return None


def _poll_grandchild_id(base_url, child_id, it, timeout_s=30, poll=0.5):
    if psycopg2 is None:
        return None
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        gid = _query_scalar(
            f'SELECT "SubFlowInstanceId" FROM "{CHILD_SCHEMA}"."InstancesCorrelations" '
            f'WHERE "ParentInstanceId" = %s ORDER BY "CreatedAt" DESC LIMIT 1',
            (child_id,))
        if gid:
            return str(gid)
        time.sleep(poll)
    return None


# ---------------------------------------------------------------------------
# DB access — a fresh short-lived connection per call keeps this safe to use
# from a thread pool (psycopg2 connections are not thread-safe to share).
# ---------------------------------------------------------------------------
_DB_DSN = None


def _connect():
    if psycopg2 is None:
        raise RuntimeError(
            "psycopg2 is required for the DB-backed measurement phase but is not installed.\n"
            "Install it with:  pip install psycopg2-binary")
    return psycopg2.connect(_DB_DSN)


def _query_scalar(query, params):
    with _connect() as conn:
        with conn.cursor() as cur:
            cur.execute(query, params)
            row = cur.fetchone()
            return row[0] if row else None


def fetch_relay_gaps(pairs):
    """Single round trip: for every (parent_id, child_id) pair, resolve the child's
    terminal transition FinishedAt and the parent's resume transition StartedAt via
    the schema investigation documented in this file's module docstring."""
    if not pairs:
        return []
    pids = [p for p, _ in pairs]
    cids = [c for _, c in pairs]
    query = f"""
        WITH pairs(pid, cid) AS (
            SELECT * FROM UNNEST(%(pids)s::uuid[], %(cids)s::uuid[])
        )
        SELECT
            p.pid,
            p.cid,
            ct."FinishedAt" AS child_terminal_ts,
            pr."StartedAt"  AS parent_resume_ts
        FROM pairs p
        LEFT JOIN LATERAL (
            SELECT "FinishedAt"
            FROM "{CHILD_SCHEMA}"."InstanceTransitions"
            WHERE "InstanceId" = p.cid
              AND "ToState" = 'child-completed'
              AND "FinishedAt" IS NOT NULL
            ORDER BY "FinishedAt" DESC
            LIMIT 1
        ) ct ON true
        LEFT JOIN LATERAL (
            SELECT "StartedAt"
            FROM "{PARENT_SCHEMA}"."InstanceTransitions"
            WHERE "InstanceId" = p.pid
              AND "FromState" = 'parent-subflow-state'
              AND "TriggerType" = 1
            ORDER BY "StartedAt" ASC
            LIMIT 1
        ) pr ON true;
    """
    with _connect() as conn:
        with conn.cursor() as cur:
            cur.execute(query, {"pids": pids, "cids": cids})
            rows = cur.fetchall()

    results = []
    for pid, cid, child_ts, parent_ts in rows:
        if child_ts is None or parent_ts is None:
            results.append({"pid": str(pid), "cid": str(cid), "gap_ms": None,
                             "child_ts": child_ts, "parent_ts": parent_ts})
            continue
        gap_ms = (parent_ts - child_ts).total_seconds() * 1000.0
        results.append({"pid": str(pid), "cid": str(cid), "gap_ms": gap_ms,
                         "child_ts": child_ts, "parent_ts": parent_ts})
    return results


def count_queue_rows(since_ts):
    outbox = _query_scalar(
        'SELECT COUNT(*) FROM "sys_queues"."OutboxMessages" WHERE "CreatedAt" >= %s',
        (since_ts,))
    inbox = _query_scalar(
        'SELECT COUNT(*) FROM "sys_queues"."InboxMessages" WHERE "CreatedAt" >= %s',
        (since_ts,))
    return outbox or 0, inbox or 0


def percentile(sorted_values, pct):
    """Linear-interpolation percentile (numpy-compatible 'linear' method), no numpy dep."""
    if not sorted_values:
        return None
    if len(sorted_values) == 1:
        return sorted_values[0]
    k = (len(sorted_values) - 1) * (pct / 100.0)
    f = int(k)
    c = min(f + 1, len(sorted_values) - 1)
    if f == c:
        return sorted_values[f]
    return sorted_values[f] + (sorted_values[c] - sorted_values[f]) * (k - f)


def main():
    global _DB_DSN

    ap = argparse.ArgumentParser(description=__doc__,
                                  formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base-url", default="http://localhost:4201",
                    help="Orchestration host base URL (default: http://localhost:4201)")
    ap.add_argument("--db-dsn", default="postgresql://postgres:postgres@localhost:5432/Aether_WorkflowDb",
                    help="Postgres connection string (matches etc/docker defaults)")
    ap.add_argument("--instances", type=int, default=20,
                    help="Number of parent instances to drive through the full lifecycle")
    ap.add_argument("--concurrency", type=int, default=5,
                    help="Max instances driven concurrently")
    args = ap.parse_args()

    if psycopg2 is None:
        print("ERROR: psycopg2 is not installed but this script's primary measurement is "
              "server-side (Postgres). Install it with:\n"
              "    pip install psycopg2-binary\n"
              "and re-run.", file=sys.stderr)
        sys.exit(2)

    _DB_DSN = args.db_dsn

    print(f"== terminal-relay-load: {args.instances} instances, concurrency={args.concurrency} ==")
    print(f"   base-url={args.base_url}")
    print(f"   db-dsn  ={args.db_dsn}")

    run_start_ts = None
    with _connect() as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT now()")
            run_start_ts = cur.fetchone()[0]

    t0 = time.time()
    with cf.ThreadPoolExecutor(max_workers=args.concurrency) as pool:
        results = list(pool.map(lambda i: run_iteration(args.base_url, i),
                                 range(1, args.instances + 1)))
    elapsed = time.time() - t0

    ok_results = [r for r in results if r["ok"]]
    stuck_results = [r for r in results if r["stuck"]]
    failed_other = [r for r in results if not r["ok"] and not r["stuck"]]

    print(f"\n== Drive phase done in {elapsed:.1f}s: "
          f"{len(ok_results)}/{len(results)} completed, "
          f"{len(stuck_results)} stuck, {len(failed_other)} failed-other ==")
    for r in failed_other:
        for e in r["errors"]:
            print(f"  FAIL it{r['it']:03d}: {e}")
    for r in stuck_results:
        for e in r["errors"]:
            print(f"  STUCK it{r['it']:03d}: {e}")

    # --- Server-side latency histogram --------------------------------------
    pairs = [(r["parent_id"], r["child_id"]) for r in ok_results
             if r["parent_id"] and r["child_id"]]
    relay_rows = fetch_relay_gaps(pairs)
    gaps = sorted(row["gap_ms"] for row in relay_rows if row["gap_ms"] is not None)
    missing = [row for row in relay_rows if row["gap_ms"] is None]

    print("\n== Server-side relay gap (parent_resume_ts - child_terminal_ts) ==")
    if missing:
        print(f"  WARNING: {len(missing)} pair(s) missing a timestamp (see rows below) — "
              f"excluded from percentiles:")
        for row in missing:
            print(f"    pid={row['pid']} cid={row['cid']} "
                  f"child_ts={row['child_ts']} parent_ts={row['parent_ts']}")

    p50 = percentile(gaps, 50)
    p95 = percentile(gaps, 95)
    p99 = percentile(gaps, 99)
    pmax = max(gaps) if gaps else None

    def fmt(v):
        return f"{v:.1f} ms" if v is not None else "n/a"

    print(f"  samples : {len(gaps)}")
    print(f"  p50     : {fmt(p50)}")
    print(f"  p95     : {fmt(p95)}")
    print(f"  p99     : {fmt(p99)}")
    print(f"  max     : {fmt(pmax)}")

    # --- Queue-row cost -------------------------------------------------------
    outbox_count, inbox_count = count_queue_rows(run_start_ts)
    print(f"\n== Queue-row cost since run start ({run_start_ts}) ==")
    print(f"  sys_queues.OutboxMessages rows: {outbox_count}")
    print(f"  sys_queues.InboxMessages  rows: {inbox_count}")

    # --- Explicit pass/fail verdicts (printed SEPARATELY per brief) ----------
    print("\n" + "=" * 64)
    verdict_p99 = gaps and p99 <= P99_TARGET_MS
    verdict_p95 = not (gaps and p95 > P95_FAIL_MS)
    verdict_stuck = len(stuck_results) == 0

    print(f"VERDICT p99 <= {P99_TARGET_MS:.0f} ms (design objective) : "
          f"{'PASS' if verdict_p99 else 'FAIL'} (p99={fmt(p99)})")
    print(f"VERDICT p95 <= {P95_FAIL_MS:.0f} ms                       : "
          f"{'PASS' if verdict_p95 else 'FAIL'} (p95={fmt(p95)})")
    print(f"VERDICT no instance stuck > {STUCK_TIMEOUT_S:.0f}s               : "
          f"{'PASS' if verdict_stuck else 'FAIL'} (stuck={len(stuck_results)})")
    print("=" * 64)

    overall = bool(gaps) and verdict_p99 and verdict_p95 and verdict_stuck and not failed_other
    print("SONUC:", "PASS" if overall else "FAIL")
    sys.exit(0 if overall else 1)


if __name__ == "__main__":
    main()
