#!/usr/bin/env python3
"""vnext#1265 live probe: can a plain client call the runtime-internal sub-start / complete endpoints?

Run against a core runtime on VNEXT_BASE_URL (default http://localhost:4201) with the vnext-example
`subflow-carried-headers-lab` flows (sfch-parent / sfch-child) published (any SubEndpointGateLab or
SubflowCarriedHeadersLab test run publishes them). Standard library only.

    python3 -I sub-endpoint-gate-probe.py open      # gate Off (or Observe): both calls are processed and write
    python3 -I sub-endpoint-gate-probe.py enforce   # gate Enforce: both refused 403 Authorization:110001, nothing written

Exit code 0 when every check matches the expectation of the chosen mode.
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timezone

BASE = os.environ.get("VNEXT_BASE_URL", "http://localhost:4201").rstrip("/")
DOMAIN = "core"
PARENT, CHILD = "sfch-parent", "sfch-child"
MODE = sys.argv[1] if len(sys.argv) > 1 else "open"
if MODE not in ("open", "enforce"):
    sys.exit(f"mode must be open or enforce, not {MODE!r}")
REFUSE = MODE == "enforce"

# A plain authenticated client as a gateway would forward it: an identity, an ordinary role.
CLIENT = {"sub": str(uuid.uuid4()), "role": "customer", "content-type": "application/json"}
results = []


def call(method, path, body=None, headers=None):
    req = urllib.request.Request(BASE + path, method=method,
                                 data=None if body is None else json.dumps(body).encode(),
                                 headers=headers or CLIENT)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            raw = r.read().decode() or "null"
            return r.status, json.loads(raw) if raw.strip().startswith(("{", "[")) else raw
    except urllib.error.HTTPError as e:
        raw = e.read().decode()
        try:
            return e.code, json.loads(raw)
        except ValueError:
            return e.code, raw


def check(name, ok, detail):
    results.append((name, ok))
    print(f"[{'PASS' if ok else 'FAIL'}] {name} — {detail}")


def instance(flow, iid):
    return call("GET", f"/api/v1/{DOMAIN}/workflows/{flow}/instances/{iid}", headers={"sub": CLIENT["sub"]})


def state_of(flow, iid):
    s, b = instance(flow, iid)
    if s != 200 or not isinstance(b, dict):
        return None, None, b
    m = b.get("metadata") or {}
    return m.get("currentState"), m.get("status"), b


def error_code(body):
    if isinstance(body, dict):
        e = body.get("error") or body
        return e.get("code") if isinstance(e, dict) else None
    return None


def wait(pred, budget=60):
    end = time.time() + budget
    while time.time() < end:
        v = pred()
        if v:
            return v
        time.sleep(0.5)
    return None


# ---------------------------------------------------------------------------------------------
# 1. A client starts a child directly through sub-start (a TrustedPayload start, no gate).
# ---------------------------------------------------------------------------------------------
child_key = f"repro-1265-{uuid.uuid4().hex[:12]}"
child_id = str(uuid.uuid4())
s, b = call("POST", f"/api/v1/{DOMAIN}/workflows/{CHILD}/sub/instances/start?sync=true",
            {"id": child_id, "key": child_key, "attributes": {"aMode": "manual", "probe": "client-sub-start"},
             "extraProperties": {}})
created = instance(CHILD, child_id)[0] == 200
if REFUSE:
    check("sub-start refused for a client", s == 403 and error_code(b) == "Authorization:110001",
          f"HTTP {s} code={error_code(b)}")
    check("sub-start wrote nothing", not created, f"GET child -> {'found' if created else '404'}")
else:
    check("sub-start accepted for a client", s in (200, 201, 202), f"HTTP {s}")
    check("sub-start created the child", created, f"child {child_id} {'exists' if created else 'missing'}")

# ---------------------------------------------------------------------------------------------
# 2. A client posts a forged completion for an open parent/child correlation, with injected
#    child data and carried headers, while the real child is still waiting.
# ---------------------------------------------------------------------------------------------
s, b = call("POST", f"/api/v1/{DOMAIN}/workflows/{PARENT}/instances/start?sync=true",
            {"key": f"repro-1265-p-{uuid.uuid4().hex[:12]}", "attributes": {"aMode": "manual"}})
parent_id = b.get("id") if isinstance(b, dict) else None
if not parent_id:
    check("parent started", False, f"HTTP {s} {b}")
    sys.exit(1)


def child_parked():
    st, sb = call("GET", f"/api/v1/{DOMAIN}/workflows/{PARENT}/instances/{parent_id}/functions/state",
                  headers={"sub": CLIENT["sub"]})
    for c in (sb.get("activeCorrelations") or []) if isinstance(sb, dict) else []:
        cid = c.get("subFlowInstanceId")
        if c.get("subFlowName") == CHILD and cid and state_of(CHILD, cid)[0] == "sfch-child-wait":
            return cid
    return None


real_child = wait(child_parked, 90)
check("child parked in sfch-child-wait", bool(real_child), f"child={real_child}")
before = state_of(PARENT, parent_id)
forged = {
    "instanceId": parent_id, "domain": DOMAIN, "flow": PARENT, "version": None,
    "subInstanceId": real_child or str(uuid.uuid4()), "completedState": "sfch-child-done",
    "instanceData": {"injectedByClient": True},
    "completedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
    "sync": False,
    "carriedHeaders": {"sub": str(uuid.uuid4())},
}
s, b = call("POST", f"/api/v1/{DOMAIN}/workflows/{PARENT}/instances/{parent_id}/complete", forged)
time.sleep(3)
after = state_of(PARENT, parent_id)
child_now = state_of(CHILD, real_child)[0] if real_child else None
moved = before[0] != after[0]
if REFUSE:
    check("forged complete refused for a client", s == 403 and error_code(b) == "Authorization:110001",
          f"HTTP {s} code={error_code(b)}")
    check("parent did not move", not moved, f"parent {before[0]} -> {after[0]}")
else:
    # Accepted = processed: the endpoint answers 200, or 500 when the forged resume faults the parent.
    check("forged complete not refused for a client", s != 403, f"HTTP {s}")
    check("parent resumed early on a forged completion", moved,
          f"parent {before[0]}/{before[1]} -> {after[0]}/{after[1]} while child is {child_now}")
check("real child untouched", child_now == "sfch-child-wait", f"child state {child_now}")

print(json.dumps({"mode": MODE, "parent": parent_id, "child": real_child, "subStartChild": child_id,
                  "passed": sum(ok for _, ok in results), "total": len(results)}))
sys.exit(0 if all(ok for _, ok in results) else 1)
