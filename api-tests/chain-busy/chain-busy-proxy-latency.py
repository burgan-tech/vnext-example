#!/usr/bin/env python3
"""
SubFlow transition proxy — istemci tarafi gecikme olcumu (chain-busy-* flow'lari).

    python3 api-tests/chain-busy/chain-busy-proxy-latency.py [--iterations 50] [--base-url URL]

## Ne olcer

Ayni async transition (`leaf-only-mark`, C'nin tekrarlanabilir `$self` shared transition'i)
iki yoldan gonderilir; her cagrinin HTTP yanit suresi (istek gonderiminden yanit govdesinin
okunmasina kadar) olculur:

  (a) PROXIED  — A'ya (aktif S zincirinin PARENT'i/root'u): runtime istegi leaf'e proxy'ler.
  (b) DIRECT   — dogrudan C'ye (leaf): proxy yok.

Iki yol AYNI zincirde, sirayla (a, b, a, b, ...) kosulur; her istekten once leaf'in Active'e
donmesi beklenir (bekleme sure olcumune GIRMEZ). Boylece sicak/soguk ve zincir durumu farki
iki yola esit dagilir.

## Sinirlar

Istemci tarafi, lokal, tek runtime, tek zincir: sunucu ici span'lari olcmez (OpenObserve /
Elasticsearch yoktu), ag maliyeti loopback'tir. p95/max, N=50'de gurultuye (GC, Dapr, scheduler)
acik. Fark = proxy'nin ekledigi hop (A -> B -> C icin iki Dapr/lokal forward) + 202 oncesi
parent EffectiveStatus pre-stamp'i. Mutlak degerler uretim icin degil, goreli karsilastirma icindir.
"""

import argparse
import json
import os
import statistics
import sys
import time
import urllib.error
import urllib.request
import uuid

DEFAULT_BASE_URL = os.environ.get("VNEXT_BASE_URL", "http://localhost:4201").rstrip("/")
BASE = DEFAULT_BASE_URL + "/api/v1"
DOMAIN = "core"
ROOT_WF, MIDDLE_WF, LEAF_WF = "chain-busy-root", "chain-busy-middle", "chain-busy-leaf"
USER = "11111111-1111-1111-1111-111111111111"
TRANSITION = "leaf-only-mark"


def http(method, url, body=None, timeout=30):
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
    except Exception as e:
        return -1, {"error": str(e)}, (time.perf_counter() - t0) * 1000


def state_of(workflow, instance_id):
    st, body, _ = http("GET", f"{BASE}/{DOMAIN}/workflows/{workflow}/instances/{instance_id}/functions/state")
    return (body.get("state"), body.get("status")) if st == 200 else (None, None)


def wait_for(workflow, instance_id, predicate, timeout_s=30):
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        s = state_of(workflow, instance_id)
        if s[0] is not None and predicate(*s):
            return True
        time.sleep(0.05)
    return False


def leaf_id_of(root_id):
    """Zinciri state function'in activeCorrelations'indan degil, instance GET'lerinden asagi iner."""
    st, body, _ = http("GET", f"{BASE}/{DOMAIN}/workflows/{ROOT_WF}/instances/{root_id}/functions/state")
    # activeCorrelations: [{ subFlowInstanceId, ... }] — ayni mantik middle icin tekrarlanir.
    mid = body["activeCorrelations"][0]["subFlowInstanceId"] if body.get("activeCorrelations") else None
    if not mid:
        return None
    st, body, _ = http("GET", f"{BASE}/{DOMAIN}/workflows/{MIDDLE_WF}/instances/{mid}/functions/state")
    return body["activeCorrelations"][0]["subFlowInstanceId"] if body.get("activeCorrelations") else None


def pct(sorted_vals, p):
    k = max(0, min(len(sorted_vals) - 1, int(round(p / 100 * len(sorted_vals) + 0.5)) - 1))
    return sorted_vals[k]


def summarize(name, vals):
    s = sorted(vals)
    return (f"{name:9s} n={len(s):3d}  min={s[0]:7.1f}  p50={statistics.median(s):7.1f}  "
            f"p95={pct(s, 95):7.1f}  max={s[-1]:7.1f}  mean={statistics.mean(s):7.1f} ms")


def main():
    global BASE
    ap = argparse.ArgumentParser()
    ap.add_argument("--iterations", type=int, default=50)
    ap.add_argument("--warmup", type=int, default=3)
    ap.add_argument("--base-url", default=DEFAULT_BASE_URL)
    args = ap.parse_args()
    BASE = args.base_url.rstrip("/") + "/api/v1"

    st, body, _ = http("POST", f"{BASE}/{DOMAIN}/workflows/{ROOT_WF}/instances/start",
                       {"testId": f"proxy-latency-{uuid.uuid4().hex[:8]}"})
    if st not in (200, 201, 202):
        print(f"start HTTP {st}: {body}")
        return 1
    root_id = body.get("id") or body.get("Id")
    if not wait_for(ROOT_WF, root_id, lambda s, _: s == "leaf-waiting", 45):
        print("zincir leaf-waiting'e ulasmadi")
        return 1
    leaf_id = leaf_id_of(root_id)
    if not leaf_id:
        print("leaf instance id bulunamadi")
        return 1
    print(f"root={root_id} leaf={leaf_id} base={BASE}")

    def settle():
        # Leaf gorunur status'u Active'e donene kadar bekle (olcume girmez).
        return wait_for(LEAF_WF, leaf_id, lambda s, stt: s == "leaf-waiting" and stt == "A", 30)

    def send(target):
        wf, iid = (ROOT_WF, root_id) if target == "proxied" else (LEAF_WF, leaf_id)
        return http("PATCH", f"{BASE}/{DOMAIN}/workflows/{wf}/instances/{iid}/transitions/{TRANSITION}?sync=false", {})

    times = {"proxied": [], "direct": []}
    failures = []
    rejected = {"proxied": 0, "direct": 0}
    for i in range(args.warmup + args.iterations):
        for target in ("proxied", "direct"):
            if not settle():
                failures.append(f"it{i} {target}: leaf Active'e donmedi")
                continue
            code, resp, ms = send(target)
            # 409 Transition:100009 ("already being processed"): leaf'in gorunur status'u A'ya
            # dondu ama onceki transition'in son temizligi (ayni-key korumasi) bitmemis. Olcume
            # alinmaz; kisa bekleyip yeniden denenir, reddedilen istek sayisi raporlanir.
            tries = 0
            while code == 409 and tries < 10:
                rejected[target] += 1
                time.sleep(0.2)
                settle()
                code, resp, ms = send(target)
                tries += 1
            if code not in (200, 202):
                failures.append(f"it{i} {target}: HTTP {code} {resp}")
                continue
            if i >= args.warmup:
                times[target].append(ms)

    print()
    for name in ("proxied", "direct"):
        if times[name]:
            print(summarize(name, times[name]))
    if times["proxied"] and times["direct"]:
        d50 = statistics.median(times["proxied"]) - statistics.median(times["direct"])
        print(f"p50 farki (proxied - direct): {d50:+.1f} ms")
    print(f"409 (already being processed) yeniden denemeleri: {rejected}")
    for f in failures:
        print("! " + f)
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
