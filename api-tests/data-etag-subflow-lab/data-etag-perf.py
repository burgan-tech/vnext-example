#!/usr/bin/env python3
"""
data-etag-perf.py — data function cost with and without an active SubFlow (vnext#1084)

Runs the same read scenarios against one runtime and reports, per scenario:
  * client latency avg / p50 / p95 / max (sequential, keep-alive connection),
  * the same at concurrency C for the two 304 scenarios,
  * server-side `Db.*` span count and transaction duration per request (Elastic APM,
    matched by the `x-request-id` this script sends).

Scenarios (instance fixtures are created by the script):
  S1  no SubFlow, If-None-Match matches        -> 304   (hot path, regression guard)
  S2  no SubFlow, unconditional                -> 200
  S3  active SubFlow, If-None-Match matches    -> 304   (branch: parent + child fingerprint)
  S4  active SubFlow, stale child part         -> 200   (branch: child changed path)
  S5  active SubFlow, unconditional            -> 200

Usage:
    python3 api-tests/data-etag-subflow-lab/data-etag-perf.py run --label branch --out /tmp/branch.json
    python3 api-tests/data-etag-subflow-lab/data-etag-perf.py run --label base   --out /tmp/base.json
    python3 api-tests/data-etag-subflow-lab/data-etag-perf.py compare /tmp/base.json /tmp/branch.json

  On a busy machine prefer the interleaved A/B mode — two runtimes up at once (the second as a
  side domain with its own offset), requests alternating between them:
    python3 api-tests/data-etag-subflow-lab/data-etag-perf.py ab --a-url http://localhost:4241 \
        --a-domain etagbase --a-label base --b-url http://localhost:4201 --b-label branch --out /tmp/ab.json

Needs: local runtime on core with `data-etag-subflow-lab` + `auto-priority-lab` published (the
integration suite publishes both), Elasticsearch on :9200. Python standard library only.
"""
import argparse
import http.client
import json
import statistics
import sys
import threading
import time
import urllib.parse
import urllib.request
import uuid

DOMAIN = "core"
PARENT = "data-etag-subflow-lab"
CHILD = "data-etag-subflow-lab-child"
PLAIN = "auto-priority-lab"
ES_INDEX = ".ds-traces-apm*,traces-apm*"
HEADERS = {"Content-Type": "application/json", "user_reference": "data-etag-perf",
           "sub": "data-etag-perf", "act_sub": "data-etag-perf"}


class Client:
    """One keep-alive connection; latency is measured around a single request/response."""

    def __init__(self, base_url):
        u = urllib.parse.urlparse(base_url)
        self.host, self.port = u.hostname, u.port or 80
        self.conn = http.client.HTTPConnection(self.host, self.port, timeout=60)

    def request(self, method, path, body=None, headers=None):
        h = dict(HEADERS)
        if headers:
            h.update(headers)
        data = json.dumps(body).encode() if body is not None else None
        t0 = time.perf_counter()
        try:
            self.conn.request(method, f"/api/v1/{path}", body=data, headers=h)
            resp = self.conn.getresponse()
            raw = resp.read()
        except (http.client.HTTPException, ConnectionError):
            self.conn.close()
            self.conn = http.client.HTTPConnection(self.host, self.port, timeout=60)
            return self.request(method, path, body, headers)
        ms = (time.perf_counter() - t0) * 1000
        return resp.status, resp.getheader("ETag"), raw, ms


def wait_until(fn, what, timeout=60):
    end = time.time() + timeout
    while time.time() < end:
        value = fn()
        if value:
            return value
        time.sleep(0.3)
    sys.exit(f"timeout: {what}")


def fixtures(c, domain=DOMAIN, parent_wf=PARENT, child_wf=CHILD):
    st, _, raw, _ = c.request("POST", f"{domain}/workflows/{PLAIN}/instances/start?sync=true",
                              {"key": uuid.uuid4().hex[:20], "attributes": {"testId": "perf"}})
    assert st < 400, raw
    plain = json.loads(raw)["id"]

    st, _, raw, _ = c.request("POST", f"{domain}/workflows/{parent_wf}/instances/start?sync=true",
                              {"key": uuid.uuid4().hex[:20], "attributes": {"testId": "perf"}})
    assert st < 400, raw
    parent = json.loads(raw)["id"]

    def child_id():
        s, _, r, _ = c.request("GET", f"{domain}/workflows/{parent_wf}/instances/{parent}/functions/state")
        if s != 200:
            return None
        for corr in json.loads(r).get("activeCorrelations", []):
            if corr.get("subFlowName") == child_wf:
                return corr.get("subFlowInstanceId")
        return None

    child = wait_until(child_id, "parent opened its child")

    def child_ready():
        # Through the parent's state function, which descends to the leaf in any domain.
        s, _, r, _ = c.request("GET", f"{domain}/workflows/{parent_wf}/instances/{parent}/functions/state")
        return s == 200 and json.loads(r).get("state") == "otp" and json.loads(r).get("status") == "A"

    wait_until(child_ready, "child settled")
    return plain, parent, child


def scenarios(plain, parent, domain=DOMAIN, parent_wf=PARENT):
    plain_path = f"{domain}/workflows/{PLAIN}/instances/{plain}/functions/data"
    parent_path = f"{domain}/workflows/{parent_wf}/instances/{parent}/functions/data"

    def etag_of(c, path):
        s, e, r, _ = c.request("GET", path)
        assert s == 200 and e, (s, r[:300])
        return e

    def stale(e):
        v = e.strip('"')
        return '"' + (v.split(".", 1)[0] + ".stale") + '"'

    return [
        ("S1", plain_path, lambda c: etag_of(c, plain_path), 304),
        ("S2", plain_path, lambda c: None, 200),
        ("S3", parent_path, lambda c: etag_of(c, parent_path), 304),
        ("S4", parent_path, lambda c: stale(etag_of(c, parent_path)), 200),
        ("S5", parent_path, lambda c: None, 200),
    ]


def pct(values, p):
    s = sorted(values)
    return s[min(len(s) - 1, int(round(p / 100 * (len(s) - 1))))]


def summarize(values):
    return {"n": len(values), "avg": round(statistics.mean(values), 3), "p50": round(pct(values, 50), 3),
            "p95": round(pct(values, 95), 3), "max": round(max(values), 3)}


def run_sequential(c, name, path, inm_fn, expected, n, warmup, label):
    inm = inm_fn(c)
    headers = {"If-None-Match": inm} if inm else {}
    for _ in range(warmup):
        c.request("GET", path, headers=headers)
    lat, statuses, prefix = [], {}, f"{label}-{name}-"
    for i in range(n):
        h = dict(headers)
        h["x-request-id"] = f"{prefix}{i:05d}"
        st, _, _, ms = c.request("GET", path, headers=h)
        statuses[st] = statuses.get(st, 0) + 1
        lat.append(ms)
    return {"expected": expected, "statuses": statuses, "latency": summarize(lat), "prefix": prefix}


def run_concurrent(base_url, path, inm, concurrency, per_thread):
    lat, lock = [], threading.Lock()

    def worker():
        c = Client(base_url)
        mine = []
        for _ in range(per_thread):
            _, _, _, ms = c.request("GET", path, headers={"If-None-Match": inm} if inm else {})
            mine.append(ms)
        with lock:
            lat.extend(mine)

    threads = [threading.Thread(target=worker) for _ in range(concurrency)]
    t0 = time.perf_counter()
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    wall = time.perf_counter() - t0
    out = summarize(lat)
    out["throughput_rps"] = round(len(lat) / wall, 1)
    return out


def es(es_url, body):
    req = urllib.request.Request(f"{es_url}/{ES_INDEX}/_search", data=json.dumps(body).encode(),
                                 method="POST", headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=60) as resp:
        return json.loads(resp.read())


def server_side(es_url, prefix):
    """Db.* spans per request and request transaction duration, by this run's x-request-id."""
    db = es(es_url, {"size": 0, "query": {"bool": {"filter": [
        {"term": {"processor.event": "span"}}, {"term": {"span.type": "db"}},
        {"prefix": {"labels.x_request_id": prefix}}]}},
        "aggs": {"rid": {"terms": {"field": "labels.x_request_id", "size": 5000}}}})
    per_request = [b["doc_count"] for b in db["aggregations"]["rid"]["buckets"]]
    tx = es(es_url, {"size": 0, "query": {"bool": {"filter": [
        {"term": {"processor.event": "transaction"}}, {"prefix": {"labels.x_request_id": prefix}}]}},
        "aggs": {"d": {"percentiles": {"field": "transaction.duration.us", "percents": [50, 95]}},
                 "a": {"avg": {"field": "transaction.duration.us"}}}})
    return {
        "requests_with_db_spans": len(per_request),
        "db_spans_per_request_avg": round(statistics.mean(per_request), 2) if per_request else None,
        "db_spans_per_request_max": max(per_request) if per_request else None,
        "tx_count": tx["hits"]["total"]["value"],
        "tx_avg_ms": round((tx["aggregations"]["a"]["value"] or 0) / 1000, 3),
        "tx_p50_ms": round((tx["aggregations"]["d"]["values"].get("50.0") or 0) / 1000, 3),
        "tx_p95_ms": round((tx["aggregations"]["d"]["values"].get("95.0") or 0) / 1000, 3),
    }


def cmd_run(a):
    c = Client(a.base_url)
    plain, parent, child = fixtures(c)
    result = {"label": a.label, "base_url": a.base_url, "plain": plain, "parent": parent, "child": child,
              "iterations": a.iterations, "scenarios": {}, "concurrent": {}}
    for name, path, inm_fn, expected in scenarios(plain, parent):
        result["scenarios"][name] = run_sequential(c, name, path, inm_fn, expected, a.iterations, a.warmup,
                                                   a.label + "-" + uuid.uuid4().hex[:6])
        print(name, result["scenarios"][name]["statuses"], result["scenarios"][name]["latency"], flush=True)
    for name, path, inm_fn, _ in scenarios(plain, parent):
        if name in ("S1", "S3"):
            result["concurrent"][name] = run_concurrent(a.base_url, path, inm_fn(c), a.concurrency, a.per_thread)
            print(name, "c=" + str(a.concurrency), result["concurrent"][name], flush=True)

    time.sleep(a.es_wait)
    urllib.request.urlopen(urllib.request.Request(f"{a.es_url}/{ES_INDEX}/_refresh", method="POST"), timeout=60)
    for name, s in result["scenarios"].items():
        s["server"] = server_side(a.es_url, s["prefix"])
        print(name, "server", s["server"], flush=True)
    with open(a.out, "w") as f:
        json.dump(result, f, indent=2)


def cmd_ab(a):
    """Interleaved A/B: the same scenario alternates request by request between two runtimes, so load
    drift on a busy machine hits both sides equally. Client latency only (plus Db spans per side)."""
    sides = {}
    for tag, url, dom, pwf, cwf in (("A", a.a_url, a.a_domain, a.a_parent, a.a_child),
                                    ("B", a.b_url, a.b_domain, a.b_parent, a.b_child)):
        c = Client(url)
        plain, parent, _child = fixtures(c, dom, pwf, cwf)
        sides[tag] = (c, scenarios(plain, parent, dom, pwf))
    result = {"a": {"url": a.a_url, "domain": a.a_domain, "label": a.a_label},
              "b": {"url": a.b_url, "domain": a.b_domain, "label": a.b_label}, "scenarios": {}}
    names = [s[0] for s in sides["A"][1]]
    for i, name in enumerate(names):
        prep = {}
        for tag in ("A", "B"):
            c, sc = sides[tag]
            _, path, inm_fn, expected = sc[i]
            inm = inm_fn(c)
            prep[tag] = (c, path, {"If-None-Match": inm} if inm else {})
            for _ in range(a.warmup):
                c.request("GET", path, headers=prep[tag][2])
        lat = {"A": [], "B": []}
        statuses = {"A": {}, "B": {}}
        run = uuid.uuid4().hex[:6]
        for k in range(a.iterations):
            order = ("A", "B") if k % 2 == 0 else ("B", "A")
            for tag in order:
                c, path, h = prep[tag]
                hh = dict(h)
                hh["x-request-id"] = f"ab-{run}-{tag}-{name}-{k:05d}"
                st, _, _, ms = c.request("GET", path, headers=hh)
                statuses[tag][st] = statuses[tag].get(st, 0) + 1
                lat[tag].append(ms)
        result["scenarios"][name] = {tag: {"statuses": statuses[tag], "latency": summarize(lat[tag]),
                                           "prefix": f"ab-{run}-{tag}-{name}-"} for tag in ("A", "B")}
        r = result["scenarios"][name]
        print(f"{name}  {a.a_label} {r['A']['statuses']} p50 {r['A']['latency']['p50']} p95 {r['A']['latency']['p95']}"
              f"  |  {a.b_label} {r['B']['statuses']} p50 {r['B']['latency']['p50']} p95 {r['B']['latency']['p95']}", flush=True)
    time.sleep(a.es_wait)
    try:
        urllib.request.urlopen(urllib.request.Request(f"{a.es_url}/{ES_INDEX}/_refresh", method="POST"), timeout=60)
    except Exception:
        pass
    for name, r in result["scenarios"].items():
        for tag in ("A", "B"):
            try:
                r[tag]["server"] = server_side(a.es_url, r[tag]["prefix"])
            except Exception as e:  # trace backend absent or not receiving this runtime's spans
                r[tag]["server"] = {"error": str(e)}
        print(name, "server", a.a_label, r["A"]["server"], "|", a.b_label, r["B"]["server"], flush=True)
    with open(a.out, "w") as f:
        json.dump(result, f, indent=2)


def cmd_compare(a):
    base, branch = (json.load(open(p)) for p in (a.base, a.branch))
    print(f"| Scenario | status base → branch | p50 ms base / branch | p95 ms base / branch | "
          f"server tx p50 ms base / branch | Db spans/request base / branch |")
    print("|---|---|---|---|---|---|")
    for name in branch["scenarios"]:
        b, r = base["scenarios"][name], branch["scenarios"][name]
        print(f"| {name} | {b['statuses']} → {r['statuses']} | {b['latency']['p50']} / {r['latency']['p50']} | "
              f"{b['latency']['p95']} / {r['latency']['p95']} | "
              f"{b['server']['tx_p50_ms']} / {r['server']['tx_p50_ms']} | "
              f"{b['server']['db_spans_per_request_avg']} / {r['server']['db_spans_per_request_avg']} |")
    for name in branch["concurrent"]:
        b, r = base["concurrent"][name], branch["concurrent"][name]
        print(f"| {name} c={a.concurrency} | — | {b['p50']} / {r['p50']} | {b['p95']} / {r['p95']} | "
              f"rps {b['throughput_rps']} / {r['throughput_rps']} | — |")


def main():
    p = argparse.ArgumentParser()
    sub = p.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("run")
    r.add_argument("--label", required=True)
    r.add_argument("--out", required=True)
    r.add_argument("--base-url", default="http://localhost:4201")
    r.add_argument("--es-url", default="http://localhost:9200")
    r.add_argument("--iterations", type=int, default=300)
    r.add_argument("--warmup", type=int, default=50)
    r.add_argument("--concurrency", type=int, default=10)
    r.add_argument("--per-thread", type=int, default=100)
    r.add_argument("--es-wait", type=int, default=20)
    ab_ = sub.add_parser("ab")
    ab_.add_argument("--a-url", required=True)
    ab_.add_argument("--a-domain", default=DOMAIN)
    ab_.add_argument("--a-label", default="A")
    ab_.add_argument("--b-url", required=True)
    ab_.add_argument("--b-domain", default=DOMAIN)
    ab_.add_argument("--b-label", default="B")
    ab_.add_argument("--a-parent", default=PARENT)
    ab_.add_argument("--a-child", default=CHILD)
    ab_.add_argument("--b-parent", default=PARENT)
    ab_.add_argument("--b-child", default=CHILD)
    ab_.add_argument("--out", required=True)
    ab_.add_argument("--es-url", default="http://localhost:9200")
    ab_.add_argument("--iterations", type=int, default=500)
    ab_.add_argument("--warmup", type=int, default=200)
    ab_.add_argument("--es-wait", type=int, default=20)
    cmp_ = sub.add_parser("compare")
    cmp_.add_argument("base")
    cmp_.add_argument("branch")
    cmp_.add_argument("--concurrency", type=int, default=10)
    a = p.parse_args()
    {"run": cmd_run, "ab": cmd_ab, "compare": cmd_compare}[a.cmd](a)


if __name__ == "__main__":
    main()
