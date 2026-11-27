#!/usr/bin/env python3
"""
cost-latency.py — bir SubFlow gecisinin sync start'a ekledigi maliyet (sure + veritabani komutu)

    python3 api-tests/subflow-cost-lab/cost-latency.py --base-url http://localhost:4201 \
        --es-url http://localhost:9200 --rounds 40 --warmup 5 --publish

Her turda iki sync start sirayla gonderilir: `subflow-cost-lab-parent` (cocugu subflow olarak calistirir) ve
`subflow-cost-lab-child` (ayni cocuk tek basina). Her istek kendi W3C `traceparent`'ini tasir; boylece istegin
trace'i yerel Elastic APM'de kesin bulunur ve istek basina sayilir: veritabani komutlari (SELECT/INSERT/UPDATE),
veri yazma turlari (`Db.LockAndReadHead`), sunucu suresi.

`--publish` iki akisi `definitions/publish` ile yukler (wf CLI gerekmez; 409 = zaten yuklu).
Yalniz Python standart kutuphanesi. Bir olcum aracidir, onay testi degil: cikis kodu yalniz bir start
basarisiz olursa ya da parent `C` olmazsa 1'dir.
"""

import argparse
import json
import os
import pathlib
import statistics
import sys
import time
import urllib.error
import urllib.request
import uuid

DOMAIN = "core"
PARENT = "subflow-cost-lab-parent"
CHILD = "subflow-cost-lab-child"
FIXTURES = pathlib.Path(__file__).resolve().parents[2] / "core" / "Workflows" / "subflow-cost-lab"


def call(url, method="GET", body=None, headers=None, timeout=60):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    for k, v in (headers or {}).items():
        req.add_header(k, v)
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


def publish(base_url):
    for name in (CHILD, PARENT):
        component = json.loads((FIXTURES / f"{name}.json").read_text())
        status, body, _ = call(f"{base_url}/api/v1/definitions/publish", "POST", component)
        print(f"publish {name}: {status}" + ("" if status < 300 else f" {json.dumps(body)[:200]}"))
    status, _, _ = call(f"{base_url}/api/v1/definitions/publish/completed", "POST", {})
    print(f"publish/completed: {status}")


def start(base_url, flow):
    trace_id = uuid.uuid4().hex
    headers = {
        "traceparent": f"00-{trace_id}-{uuid.uuid4().hex[:16]}-01",
        "user_reference": "subflow-cost-lab",
        "x-request-id": str(uuid.uuid4()),
    }
    body = {
        "key": f"cost-{uuid.uuid4().hex[:12]}",
        "tags": ["subflow-cost-lab"],
        "attributes": {"userName": "1234", "clientId": "IbWeb", "scope": "1234"},
    }
    status, resp, ms = call(
        f"{base_url}/api/v1/{DOMAIN}/workflows/{flow}/instances/start?sync=true", "POST", body, headers)
    return status, resp, ms, trace_id


def es_search(es_url, body):
    _, resp, _ = call(f"{es_url}/traces-apm*/_search", "POST", body, timeout=30)
    return resp


def trace_costs(es_url, trace_ids):
    """Per trace: root server duration, DB commands by verb, data-write rounds, span count."""
    body = {
        "size": 0,
        "query": {"terms": {"trace.id": trace_ids}},
        "aggs": {"t": {"terms": {"field": "trace.id", "size": len(trace_ids)}, "aggs": {
            "root": {"filter": {"term": {"processor.event": "transaction"}},
                     "aggs": {"d": {"max": {"field": "transaction.duration.us"}}}},
            "db": {"filter": {"term": {"span.type": "db"}}, "aggs": {
                "n": {"terms": {"field": "span.name", "size": 10}},
                "sum": {"sum": {"field": "span.duration.us"}}}},
            "writes": {"filter": {"term": {"span.name": "Db.LockAndReadHead"}}},
            "spans": {"filter": {"term": {"processor.event": "span"}}},
        }}},
    }
    out = {}
    for b in es_search(es_url, body)["aggregations"]["t"]["buckets"]:
        verbs = {x["key"]: x["doc_count"] for x in b["db"]["n"]["buckets"]}
        out[b["key"]] = {
            "server_ms": (b["root"]["d"]["value"] or 0) / 1000,
            "db": b["db"]["doc_count"],
            "db_ms": b["db"]["sum"]["value"] / 1000,
            "select": verbs.get("Db.SELECT", 0),
            "insert": verbs.get("Db.INSERT", 0),
            "update": verbs.get("Db.UPDATE", 0),
            "data_writes": b["writes"]["doc_count"],
            "spans": b["spans"]["doc_count"],
        }
    return out


def pct(values, p):
    values = sorted(values)
    if not values:
        return float("nan")
    k = (len(values) - 1) * p / 100
    lo, hi = int(k), min(int(k) + 1, len(values) - 1)
    return values[lo] + (values[hi] - values[lo]) * (k - lo)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base-url", default=os.environ.get("VNEXT_BASE_URL", "http://localhost:4201"))
    ap.add_argument("--es-url", default="http://localhost:9200")
    ap.add_argument("--rounds", type=int, default=40)
    ap.add_argument("--warmup", type=int, default=5)
    ap.add_argument("--publish", action="store_true")
    ap.add_argument("--label", default="run")
    ap.add_argument("--json-out")
    args = ap.parse_args()

    if args.publish:
        publish(args.base_url)

    samples = {PARENT: [], CHILD: []}
    failed = 0
    for i in range(args.warmup + args.rounds):
        for flow in (PARENT, CHILD):
            status, resp, ms, trace_id = start(args.base_url, flow)
            ok = 200 <= status < 300 and (flow != PARENT or resp.get("status") in ("C", "Completed"))
            if not ok:
                failed += 1
                print(f"FAIL {flow}: {status} {json.dumps(resp)[:300]}", file=sys.stderr)
                continue
            if i >= args.warmup:
                samples[flow].append({"client_ms": ms, "trace": trace_id})

    # The APM pipeline (otel collector -> apm server -> ES) lands spans a few seconds late.
    time.sleep(12)
    costs = trace_costs(args.es_url, [s["trace"] for v in samples.values() for s in v])

    result = {"label": args.label, "rounds": args.rounds, "flows": {}}
    print(f"\n[{args.label}] rounds={args.rounds} warmup={args.warmup} failed={failed}")
    print(f"{'flow':<26}{'client p50':>11}{'p90':>8}{'server p50':>12}{'db/req':>8}{'sel':>5}{'ins':>5}"
          f"{'upd':>5}{'writes':>8}{'db ms p50':>11}{'spans':>7}{'traced':>8}")
    for flow, rows in samples.items():
        traced = [costs[r["trace"]] for r in rows if r["trace"] in costs]
        med = lambda key: statistics.median(t[key] for t in traced) if traced else float("nan")
        client = [r["client_ms"] for r in rows]
        summary = {
            "client_p50": pct(client, 50), "client_p90": pct(client, 90),
            "server_p50": med("server_ms"), "db": med("db"), "select": med("select"),
            "insert": med("insert"), "update": med("update"), "data_writes": med("data_writes"),
            "db_ms_p50": med("db_ms"), "spans": med("spans"), "traced": len(traced),
        }
        result["flows"][flow] = summary
        print(f"{flow:<26}{summary['client_p50']:>11.1f}{summary['client_p90']:>8.1f}{summary['server_p50']:>12.1f}"
              f"{summary['db']:>8.0f}{summary['select']:>5.0f}{summary['insert']:>5.0f}{summary['update']:>5.0f}"
              f"{summary['data_writes']:>8.0f}{summary['db_ms_p50']:>11.1f}{summary['spans']:>7.0f}"
              f"{len(traced):>5}/{len(rows)}")

    if args.json_out:
        pathlib.Path(args.json_out).write_text(json.dumps(result, indent=2))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
