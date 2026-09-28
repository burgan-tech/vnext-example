#!/usr/bin/env python3
"""
depth-latency.py — ic ice SubFlow derinligine gore sync transition gecikmesi

    python3 api-tests/subflow-depth-lab/depth-latency.py \
        --base-url http://localhost:4201 --depths 1,2,3,4,5 --instances 5 --pings 10

Her derinlik d icin `subflow-depth-lab-l{6-d}` sync=true baslatilir (zincir auto ile yapraga
kadar kurulur), ardindan ROOT'a `leaf-ping` sync=true gonderilir. `leaf-ping` her seviyede
forward edilerek l5'e iner; yaprakta task'siz bir self-loop'tur. Olculen, client'in root
uzerinden gordugu uctan uca yanit suresidir (seviye basi forward + settle + yukari state relay).

Cikti: derinlik basina p50/p95/p99/max (ms) ve seviye basi egim (ms/level, d=1'e gore).
Sonda `leaf-finish` ile zincir kapatilir (unwind suresi de raporlanir), boylece lab temiz kalir.

Basarisizlik esigi (varsayilan): herhangi bir ping HTTP 2xx degilse veya zincir 30 s icinde
kurulmazsa cikis kodu 1. Gecikme esigi yoktur — bu script bir olcum aracidir, onay testi degil.
"""

import argparse
import json
import statistics
import sys
import time
import urllib.error
import urllib.request
import uuid

DOMAIN = "core"
PREFIX = "subflow-depth-lab"
LEAF_STATE = "l5-waiting"
USER = "depth-latency"


def http(base_url, method, path, body=None, timeout=60):
    url = f"{base_url}/api/v1/{path}"
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    req.add_header("user_reference", USER)
    req.add_header("x-request-id", str(uuid.uuid4()))
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
    except Exception as e:  # connection errors
        return -1, {"error": str(e)}, (time.perf_counter() - t0) * 1000


def find_field(obj, keys):
    if isinstance(obj, dict):
        for k, v in obj.items():
            if k in keys and isinstance(v, str):
                return v
        for v in obj.values():
            r = find_field(v, keys)
            if r:
                return r
    return None


def wait_leaf(base_url, workflow, iid, timeout_s=30):
    """Root state function subflow'a iner; yaprak Active + l5-waiting gorulene kadar bekle."""
    deadline = time.time() + timeout_s
    last = None
    while time.time() < deadline:
        st, body, _ = http(base_url, "GET", f"{DOMAIN}/workflows/{workflow}/instances/{iid}/functions/state")
        if st == 200:
            last = (find_field(body, {"state", "currentState"}), find_field(body, {"status"}))
            if last == (LEAF_STATE, "A"):
                return True, last
        time.sleep(0.2)
    return False, last


def pct(values, p):
    s = sorted(values)
    if not s:
        return float("nan")
    idx = min(len(s) - 1, max(0, int(round(p / 100 * (len(s) - 1)))))
    return s[idx]


def run_depth(base_url, depth, instances, pings, warmup):
    workflow = f"{PREFIX}-l{6 - depth}"
    lat, finish_lat, errors = [], [], []
    for i in range(instances):
        st, body, _ = http(base_url, "POST", f"{DOMAIN}/workflows/{workflow}/instances/start?sync=true",
                           {"testId": f"d{depth}-{i}-{uuid.uuid4().hex[:8]}"})
        iid = (body.get("id") or body.get("Id")) if isinstance(body, dict) else None
        if st not in (200, 201) or not iid:
            errors.append(f"d={depth} start HTTP {st}: {body}")
            continue
        ok, last = wait_leaf(base_url, workflow, iid)
        if not ok:
            errors.append(f"d={depth} {iid} chain not ready (last={last})")
            continue
        for n in range(warmup + pings):
            st, body, ms = http(base_url, "PATCH",
                                f"{DOMAIN}/workflows/{workflow}/instances/{iid}/transitions/leaf-ping?sync=true",
                                {"ping": n})
            if st not in (200, 202):
                errors.append(f"d={depth} {iid} ping#{n} HTTP {st}: {body}")
                break
            if n >= warmup:
                lat.append(ms)
        st, body, ms = http(base_url, "PATCH",
                            f"{DOMAIN}/workflows/{workflow}/instances/{iid}/transitions/leaf-finish?sync=true",
                            {})
        if st in (200, 202):
            finish_lat.append(ms)
        else:
            errors.append(f"d={depth} {iid} finish HTTP {st}: {body}")
    return lat, finish_lat, errors


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base-url", default="http://localhost:4201")
    ap.add_argument("--depths", default="1,2,3,4,5")
    ap.add_argument("--instances", type=int, default=5, help="derinlik basina instance")
    ap.add_argument("--pings", type=int, default=10, help="instance basina olculen leaf-ping")
    ap.add_argument("--warmup", type=int, default=2, help="instance basina olculmeyen ilk ping")
    args = ap.parse_args()

    depths = [int(d) for d in args.depths.split(",")]
    if any(d < 1 or d > 5 for d in depths):
        sys.exit("depths 1..5 araliginda olmali")

    rows, all_errors = {}, []
    for d in depths:
        print(f"depth {d}: {args.instances} instance x {args.pings} ping ...", flush=True)
        lat, fin, errs = run_depth(args.base_url, d, args.instances, args.pings, args.warmup)
        rows[d] = (lat, fin)
        all_errors += errs

    print()
    print(f"{'depth':>5} {'n':>4} {'p50':>8} {'p95':>8} {'p99':>8} {'max':>8} {'finish p50':>11}  (ms)")
    for d in depths:
        lat, fin = rows[d]
        if not lat:
            print(f"{d:>5} {0:>4}  -- olcum yok --")
            continue
        print(f"{d:>5} {len(lat):>4} {pct(lat,50):>8.1f} {pct(lat,95):>8.1f} {pct(lat,99):>8.1f} "
              f"{max(lat):>8.1f} {(statistics.median(fin) if fin else float('nan')):>11.1f}")

    measured = [d for d in depths if rows[d][0]]
    if len(measured) >= 2:
        lo, hi = min(measured), max(measured)
        slope = (statistics.median(rows[hi][0]) - statistics.median(rows[lo][0])) / (hi - lo)
        print(f"\nseviye basi egim (p50, d={lo}->{hi}): {slope:.1f} ms/level")

    if all_errors:
        print(f"\n{len(all_errors)} hata:")
        for e in all_errors[:20]:
            print(f"  - {e}")
        sys.exit(1)


if __name__ == "__main__":
    main()
