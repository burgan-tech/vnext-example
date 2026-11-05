#!/usr/bin/env python3
"""
db-cost.py — transition pipeline'inin istek basina veritabani maliyeti (komut + commit)

Ayni senaryolari bir runtime'a karsi N kez sirayla kosar ve uc kaynaktan sayar:

  1. Pencere toplami (asil sayi): senaryo penceresindeki TUM `Db.*` span'leri (Elastic APM,
     servis ve komut tipine gore), bostaki arka plan orani (outbox/inbox polling) dusulerek.
     Post-commit lane'leri, relay'ler ve worker'lar dahil — ayri trace'e dusen is de sayilir.
  2. Istek basina: `x_request_id` etiketi bu script'in verdigi id'yi tasiyan `Db.*` span'leri.
     Hangi cagrinin ne odedigini gosterir (start / transition / finish).
  3. Postgres: `pg_stat_database.xact_commit` farki (gercek commit = WAL flush sayisi), bostaki
     oran dusulerek.

Istemci gecikmesi (p50/p95) de raporlanir. Butun istekler sync=true'dur, boylece istek
dondugunde zincir dinlenme noktasina varmis olur.

Kullanim:
    python3 api-tests/db-cost-lab/db-cost.py run --label branch --iterations 20 --out /tmp/branch.json
    python3 api-tests/db-cost-lab/db-cost.py run --label base   --iterations 20 --out /tmp/base.json
    python3 api-tests/db-cost-lab/db-cost.py compare /tmp/base.json /tmp/branch.json

Gereken: lokal runtime (`run-docker.sh up core`), core domain'e `wf sync` ile yuklenmis
`payload-modes`, `chain-busy`, `subflow-depth-lab` akislari, Elasticsearch (9200) ve
`vnext-postgres` konteyneri. Yalniz Python standart kutuphanesi.
"""
import argparse
import json
import os
import statistics
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

DOMAIN = "core"
USER = "db-cost-lab"
CALLER = "db-cost-lab"        # --caller ''  sends no identity
DB_NAME_DEFAULT = "Aether_WorkflowDb"
ES_INDEX = ".ds-traces-apm*,traces-apm*"
# An idle pooled backend reports its table/IO counters only after PGSTAT_IDLE_INTERVAL (10 s).
PG_FLUSH_WAIT = 12
DB_VERBS = ("Db.SELECT", "Db.INSERT", "Db.UPDATE", "Db.DELETE", "Db.Query")


# ── HTTP ────────────────────────────────────────────────────────────────────

def http(base_url, method, path, body=None, request_id=None, timeout=60):
    url = f"{base_url}/api/v1/{path}"
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    req.add_header("user_reference", USER)
    if CALLER:
        # The caller identity: what the audit columns (CreatedBy / ModifiedBy) record. Production
        # requests carry one; an anonymous run skips the audit stamp and measures a cheaper path.
        req.add_header("sub", CALLER)
        req.add_header("act_sub", CALLER)
    req.add_header("x-request-id", request_id or uuid.uuid4().hex)
    t0 = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode()
            return resp.status, (json.loads(raw) if raw else {}), (time.perf_counter() - t0) * 1000
    except urllib.error.HTTPError as e:
        raw = e.read().decode()
        try:
            return e.code, json.loads(raw), (time.perf_counter() - t0) * 1000
        except json.JSONDecodeError:
            return e.code, {"raw": raw}, (time.perf_counter() - t0) * 1000


def es_search(es_url, body):
    req = urllib.request.Request(
        f"{es_url}/{ES_INDEX}/_search", data=json.dumps(body).encode(), method="POST",
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=60) as resp:
        return json.loads(resp.read().decode())


def es_refresh(es_url):
    try:
        req = urllib.request.Request(f"{es_url}/{ES_INDEX}/_refresh", data=b"", method="POST")
        urllib.request.urlopen(req, timeout=30).read()
    except Exception:
        pass


# ── Postgres ────────────────────────────────────────────────────────────────

PG_SNAPSHOT_SQL = """
SELECT json_build_object(
  'xact_commit', (SELECT xact_commit FROM pg_stat_database WHERE datname = current_database()),
  'wal_bytes', pg_wal_lsn_diff(pg_current_wal_lsn(), '0/0')::bigint,
  'wal_records', (SELECT wal_records FROM pg_stat_wal),
  'wal_fsyncs', (SELECT COALESCE(sum(fsyncs), 0) FROM pg_stat_io WHERE object = 'wal'),
  'rows', (SELECT json_object_agg(t, json_build_array(ins, upd, del)) FROM (
      SELECT CASE WHEN schemaname = 'sys_queues' THEN 'sys_queues.' || relname ELSE relname END AS t,
             sum(n_tup_ins) AS ins, sum(n_tup_upd) AS upd, sum(n_tup_del) AS del
      FROM pg_stat_user_tables GROUP BY 1) r))
"""


def pg_snapshot(db_name):
    """Cluster WAL counters and this database's commit and per-table row-write counters.

    xact_commit counts every transaction, read-only ones too (each autocommit SELECT is one);
    wal_fsyncs / wal_records / wal_bytes and the row counters are the write side.
    """
    out = subprocess.run(
        ["docker", "exec", "vnext-postgres", "psql", "-U", "postgres", "-d", db_name, "-At", "-c",
         PG_SNAPSHOT_SQL],
        capture_output=True, text=True, check=True).stdout.strip()
    return json.loads(out)


def pg_diff(a, b):
    d = {k: b[k] - a[k] for k in ("xact_commit", "wal_bytes", "wal_records", "wal_fsyncs")}
    rows = {}
    for t, (i1, u1, d1) in (b.get("rows") or {}).items():
        i0, u0, d0 = (a.get("rows") or {}).get(t, (0, 0, 0))
        delta = (i1 - i0, u1 - u0, d1 - d0)
        if any(delta):
            rows[t] = delta
    d["rows"] = rows
    return d


# ── Elastic APM counting ────────────────────────────────────────────────────

def iso(ts):
    return time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(ts)) + f".{int((ts % 1) * 1000):03d}Z"


def db_spans_in_window(es_url, t0, t1):
    """Db.* span count per service and verb within [t0, t1]."""
    body = {
        "size": 0,
        "query": {"bool": {"filter": [
            {"term": {"processor.event": "span"}},
            {"term": {"span.type": "db"}},
            {"range": {"@timestamp": {"gte": iso(t0), "lte": iso(t1)}}},
        ]}},
        "aggs": {"svc": {"terms": {"field": "service.name", "size": 20},
                         "aggs": {"verb": {"terms": {"field": "span.name", "size": 20}}}}},
    }
    res = es_search(es_url, body)
    out = {}
    for s in res["aggregations"]["svc"]["buckets"]:
        out[s["key"]] = {v["key"]: v["doc_count"] for v in s["verb"]["buckets"]}
    return out


def db_spans_by_request(es_url, prefix, t0, t1):
    """Db.* span count per x_request_id (this run's ids only) and verb."""
    body = {
        "size": 0,
        "query": {"bool": {"filter": [
            {"term": {"processor.event": "span"}},
            {"term": {"span.type": "db"}},
            {"prefix": {"labels.x_request_id": prefix}},
            {"range": {"@timestamp": {"gte": iso(t0), "lte": iso(t1)}}},
        ]}},
        "aggs": {"rid": {"terms": {"field": "labels.x_request_id", "size": 5000},
                         "aggs": {"verb": {"terms": {"field": "span.name", "size": 20}}}}},
    }
    res = es_search(es_url, body)
    return {b["key"]: {v["key"]: v["doc_count"] for v in b["verb"]["buckets"]}
            for b in res["aggregations"]["rid"]["buckets"]}


def total(verbs):
    return sum(verbs.get(v, 0) for v in DB_VERBS)


# ── Scenarios ───────────────────────────────────────────────────────────────
# Each step: (name, method, path builder, body). A step returns the instance id it created/used.

PAYLOAD = {"session": "-", "customer": {"ownerUserId": "2321321"}}


def scenario_manual(ctx):
    """payload-modes: start -> submit-payload (data) -> complete. Manual hops, no task."""
    wf = "payload-modes"
    iid = ctx.start(wf, PAYLOAD)
    ctx.transition(wf, iid, "submit-payload", PAYLOAD)
    ctx.transition(wf, iid, "complete", {})
    return wf, iid, "done"


def scenario_tasks_timer(ctx):
    """chain-busy-leaf as a root: onEntry script tasks, auto hop, 30 min timer armed, then
    finish-leaf: CancelScheduledJobs + onExit + onExecute tasks + Finish."""
    wf = "chain-busy-leaf"
    iid = ctx.start(wf, {"lab": "db-cost"})
    ctx.transition(wf, iid, "finish-leaf", {})
    return wf, iid, "leaf-done"


def scenario_subflow(ctx):
    """subflow-depth-lab-l4 (depth 2): start enters an S SubFlow (l5); leaf-finish is proxied to
    the leaf, the leaf completes, the parent resumes and auto-finishes."""
    wf = "subflow-depth-lab-l4"
    iid = ctx.start(wf, {"testId": "db-cost"})
    ctx.transition(wf, iid, "leaf-finish", {}, ready_retry=True)
    return wf, iid, "l4-done"


def scenario_chain(ctx):
    """chain-busy-root (depth 3, script tasks on every level, leaf timer): start builds the
    chain, finish-leaf through the root unwinds it to root-done."""
    wf = "chain-busy-root"
    iid = ctx.start(wf, {"lab": "db-cost"})
    ctx.transition(wf, iid, "finish-leaf", {}, ready_retry=True)
    return wf, iid, "root-done"


SCENARIOS = {
    "manual": scenario_manual,
    "tasks-timer": scenario_tasks_timer,
    "subflow": scenario_subflow,
    "chain": scenario_chain,
}


class Ctx:
    def __init__(self, base_url, prefix):
        self.base_url = base_url
        self.prefix = prefix
        self.steps = []          # (step name, ms, status)
        self.errors = []

    def _rid(self, step):
        return f"{self.prefix}{len(self.steps):02d}-{step}"

    def start(self, wf, body):
        rid = self._rid("start")
        st, resp, ms = http(self.base_url, "POST", f"{DOMAIN}/workflows/{wf}/instances/start?sync=true",
                            body, request_id=rid)
        self.steps.append(("start", ms, st))
        iid = (resp.get("id") or resp.get("Id")) if isinstance(resp, dict) else None
        if st not in (200, 201) or not iid:
            raise RuntimeError(f"{wf} start HTTP {st}: {resp}")
        return iid

    def transition(self, wf, iid, key, body, ready_retry=False):
        # The proxied leaf of a fresh chain may still be settling when the start returns; a 409
        # is retried briefly (every retry is counted in the window, and reported).
        for attempt in range(50 if ready_retry else 1):
            rid = self._rid(key if attempt == 0 else f"{key}-retry")
            st, resp, ms = http(self.base_url, "PATCH",
                                f"{DOMAIN}/workflows/{wf}/instances/{iid}/transitions/{key}?sync=true",
                                body, request_id=rid)
            self.steps.append((key if attempt == 0 else f"{key}-retry", ms, st))
            if st in (200, 202):
                return
            if not ready_retry or st not in (409, 404, 400):
                break
            time.sleep(0.1)
        raise RuntimeError(f"{wf} {iid} {key} HTTP {st}: {resp}")


def final_state(base_url, wf, iid):
    st, body, _ = http(base_url, "GET", f"{DOMAIN}/workflows/{wf}/instances/{iid}")
    if st != 200 or not isinstance(body, dict):
        return None, None
    meta = body.get("metadata") or {}
    return (meta.get("currentState") or meta.get("state") or body.get("currentState"),
            meta.get("status") or body.get("status"))


def pct(values, p):
    s = sorted(values)
    if not s:
        return None
    return s[min(len(s) - 1, max(0, int(round(p / 100 * (len(s) - 1)))))]


def measure_idle(es_url, db_name, seconds):
    p0, t0 = pg_snapshot(db_name), time.time()
    time.sleep(seconds)
    t1 = time.time()
    time.sleep(PG_FLUSH_WAIT)
    p1 = pg_snapshot(db_name)
    time.sleep(3)
    es_refresh(es_url)
    spans = db_spans_in_window(es_url, t0, t1)
    dur = t1 - t0
    pd = pg_diff(p0, p1)
    return {
        "seconds": round(dur, 2),
        "pg_per_s": {k: pd[k] / dur for k in ("xact_commit", "wal_bytes", "wal_records", "wal_fsyncs")},
        "rows_per_s": {t: [x / dur for x in v] for t, v in pd["rows"].items()},
        "db_spans_per_s": {svc: total(v) / dur for svc, v in spans.items()},
    }


def run_scenario(args, name, fn, idle):
    run_id = uuid.uuid4().hex[:6]
    # Warm-up: caches, compiled scripts, connection pools. Not measured.
    for w in range(args.warmup):
        fn(Ctx(args.base_url, f"dbcw-{run_id}-{name}-{w}-"))
    # Long enough for the warm-up's own row/IO counters to be flushed BEFORE the window opens.
    time.sleep(max(args.settle, PG_FLUSH_WAIT))

    prefix = f"dbc-{args.label}-{run_id}-{name}-"
    ctxs, finals = [], []
    p0, t0 = pg_snapshot(args.db), time.time()
    for i in range(args.iterations):
        ctx = Ctx(args.base_url, f"{prefix}{i:03d}-")
        try:
            wf, iid, expected = fn(ctx)
            finals.append((wf, iid, expected))
        except Exception as e:  # recorded, the run goes on
            ctx.errors.append(str(e))
        ctxs.append(ctx)
    time.sleep(args.settle)        # post-commit relays, inbox backups, outbox flush
    t1 = time.time()
    time.sleep(PG_FLUSH_WAIT)
    p1 = pg_snapshot(args.db)

    time.sleep(5)                  # span export delay
    es_refresh(args.es_url)
    window = db_spans_in_window(args.es_url, t0, t1)
    by_request = db_spans_by_request(args.es_url, prefix, t0, t1)

    dur = t1 - t0
    n = args.iterations
    net_by_service = {}
    for svc, verbs in window.items():
        idle_rate = idle["db_spans_per_s"].get(svc, 0.0)
        net_by_service[svc] = {
            "raw": total(verbs),
            "idle_estimate": round(idle_rate * dur, 1),
            "net_per_iteration": round((total(verbs) - idle_rate * dur) / n, 2),
            "verbs_per_iteration": {v: round(c / n, 2) for v, c in sorted(verbs.items())},
        }
    pd = pg_diff(p0, p1)
    pg_dur = dur + PG_FLUSH_WAIT
    pg_net = {k: round((pd[k] - idle["pg_per_s"][k] * pg_dur) / n, 2)
              for k in ("xact_commit", "wal_bytes", "wal_records", "wal_fsyncs")}
    rows_net = {}
    for t, v in pd["rows"].items():
        idle_v = idle["rows_per_s"].get(t, [0, 0, 0])
        net = [round((v[k] - idle_v[k] * pg_dur) / n, 2) for k in range(3)]
        if any(abs(x) >= 0.05 for x in net):
            rows_net[t] = {"ins": net[0], "upd": net[1], "del": net[2]}

    # Per-step: request ids are "<prefix><iteration>-<NN>-<step>"; the step name may contain '-'.
    per_step = {}
    for rid, verbs in by_request.items():
        step = rid[len(prefix):].split("-", 2)[-1]
        per_step.setdefault(step, []).append(verbs)
    step_summary = {}
    for step, rows in sorted(per_step.items()):
        verbs = {}
        for r in rows:
            for v, c in r.items():
                verbs[v] = verbs.get(v, 0) + c
        step_summary[step] = {
            "requests": len(rows),
            "db_per_request": round(sum(total(r) for r in rows) / len(rows), 2),
            "verbs_per_request": {v: round(c / len(rows), 2) for v, c in sorted(verbs.items())},
        }

    latency = {}
    for ctx in ctxs:
        for step, ms, _ in ctx.steps:
            latency.setdefault(step, []).append(ms)

    wrong_final = []
    for wf, iid, expected in finals:
        state, status = final_state(args.base_url, wf, iid)
        if state != expected:
            wrong_final.append(f"{iid}: {state}/{status} (expected {expected})")

    return {
        "label": args.label,
        "iterations": n,
        "window_seconds": round(dur, 2),
        "db_commands_by_service": net_by_service,
        "db_commands_net_per_iteration": round(sum(s["net_per_iteration"] for s in net_by_service.values()), 2),
        "pg_net_per_iteration": pg_net,
        "rows_written_per_iteration": dict(sorted(rows_net.items())),
        "per_step": step_summary,
        "latency_ms": {s: {"p50": round(pct(v, 50), 1), "p95": round(pct(v, 95), 1), "n": len(v)}
                       for s, v in latency.items()},
        "errors": [e for c in ctxs for e in c.errors],
        "wrong_final_state": wrong_final,
    }


def cmd_run(args):
    names = args.scenarios.split(",")
    for nm in names:
        if nm not in SCENARIOS:
            sys.exit(f"unknown scenario {nm}; one of {', '.join(SCENARIOS)}")

    print(f"idle probe {args.idle}s ...", flush=True)
    idle = measure_idle(args.es_url, args.db, args.idle)
    print(f"  idle: {json.dumps({k: round(v, 2) for k, v in idle['pg_per_s'].items()})}, "
          f"db spans/s {json.dumps({k: round(v, 2) for k, v in idle['db_spans_per_s'].items()})}")

    result = {"label": args.label, "base_url": args.base_url, "caller": CALLER or None, "idle": idle, "scenarios": {}}
    for nm in names:
        print(f"scenario {nm}: warmup {args.warmup}, measure {args.iterations} ...", flush=True)
        r = run_scenario(args, nm, SCENARIOS[nm], idle)
        result["scenarios"][nm] = r
        print(f"  DB commands/iter (net) {r['db_commands_net_per_iteration']}, pg/iter (net) "
              f"{r['pg_net_per_iteration']}, errors {len(r['errors'])}, wrong final {len(r['wrong_final_state'])}")
        print(f"    rows/iter {json.dumps(r['rows_written_per_iteration'])}")
        for step, s in r["per_step"].items():
            print(f"    {step:24} {s['db_per_request']:7.2f} db/request  {s['verbs_per_request']}")

    with open(args.out, "w") as f:
        json.dump(result, f, indent=2)
    print(f"written {args.out}")
    bad = any(r["errors"] or r["wrong_final_state"] for r in result["scenarios"].values())
    return 1 if bad else 0


def cmd_compare(args):
    a, b = json.load(open(args.base)), json.load(open(args.branch))
    print(f"| Scenario | Metric | {a['label']} | {b['label']} | Δ | Δ % |")
    print("|---|---|---:|---:|---:|---:|")

    def row(sc, metric, x, y):
        if x is None or y is None:
            return
        d = y - x
        p = (d / x * 100) if x else 0
        print(f"| {sc} | {metric} | {x:.2f} | {y:.2f} | {d:+.2f} | {p:+.1f}% |")

    for sc in a["scenarios"]:
        if sc not in b["scenarios"]:
            continue
        ra, rb = a["scenarios"][sc], b["scenarios"][sc]
        row(sc, "DB commands / iteration (net, all services)",
            ra["db_commands_net_per_iteration"], rb["db_commands_net_per_iteration"])
        svcs = sorted(set(ra["db_commands_by_service"]) | set(rb["db_commands_by_service"]))
        for svc in svcs:
            va = ra["db_commands_by_service"].get(svc, {}).get("verbs_per_iteration", {})
            vb = rb["db_commands_by_service"].get(svc, {}).get("verbs_per_iteration", {})
            for verb in DB_VERBS:
                if verb in va or verb in vb:
                    row(sc, f"{svc} {verb} / iteration (raw)", va.get(verb, 0.0), vb.get(verb, 0.0))
        for k, label in (("xact_commit", "Transactions / iteration (incl. read-only)"),
                         ("wal_fsyncs", "WAL fsyncs / iteration"),
                         ("wal_records", "WAL records / iteration"),
                         ("wal_bytes", "WAL bytes / iteration")):
            row(sc, label, ra["pg_net_per_iteration"][k], rb["pg_net_per_iteration"][k])
        tables = sorted(set(ra["rows_written_per_iteration"]) | set(rb["rows_written_per_iteration"]))
        for t in tables:
            for op in ("ins", "upd", "del"):
                x = ra["rows_written_per_iteration"].get(t, {}).get(op, 0.0)
                y = rb["rows_written_per_iteration"].get(t, {}).get(op, 0.0)
                if abs(x) >= 0.05 or abs(y) >= 0.05:
                    row(sc, f"rows {op} `{t}` / iteration", x, y)
        for step in sorted(set(ra["per_step"]) & set(rb["per_step"])):
            row(sc, f"`{step}` DB / request", ra["per_step"][step]["db_per_request"],
                rb["per_step"][step]["db_per_request"])
        for step in sorted(set(ra["latency_ms"]) & set(rb["latency_ms"])):
            row(sc, f"`{step}` p50 ms", ra["latency_ms"][step]["p50"], rb["latency_ms"][step]["p50"])
            row(sc, f"`{step}` p95 ms", ra["latency_ms"][step]["p95"], rb["latency_ms"][step]["p95"])
    return 0


def set_caller(value):
    global CALLER
    CALLER = value


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("run")
    r.add_argument("--base-url", default="http://localhost:4201")
    r.add_argument("--es-url", default=os.environ.get("ES_URL", "http://localhost:9200"))
    r.add_argument("--db", default=DB_NAME_DEFAULT)
    r.add_argument("--label", required=True)
    r.add_argument("--scenarios", default=",".join(SCENARIOS))
    r.add_argument("--iterations", type=int, default=20)
    r.add_argument("--warmup", type=int, default=3)
    r.add_argument("--idle", type=int, default=20, help="seconds of idle probe before the scenarios")
    r.add_argument("--settle", type=float, default=6.0, help="seconds after a window for async work")
    r.add_argument("--caller", default="db-cost-lab", help="sub/act_sub identity; empty = anonymous caller")
    r.add_argument("--out", required=True)
    c = sub.add_parser("compare")
    c.add_argument("base")
    c.add_argument("branch")
    args = ap.parse_args()
    if args.cmd == "run":
        set_caller(args.caller)
    sys.exit(cmd_run(args) if args.cmd == "run" else cmd_compare(args))


if __name__ == "__main__":
    main()
