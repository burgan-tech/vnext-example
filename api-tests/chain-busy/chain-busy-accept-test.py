#!/usr/bin/env python3
"""
Accept-time SubFlow chain reserve — A -> B -> C dogrulama testi (chain-busy-* flow'lari).

    python3 api-tests/chain-busy/chain-busy-accept-test.py [--iterations 5] [--publish]

## Dogruladigi davranis

3 seviyeli subflow zinciri: root (A) -> middle (B) -> leaf (C).
Zincir kurulunca A ve B, acik SubFlow korelasyonu boyunca YAPISAL olarak Busy
(`Instance.AddCorrelation` -> `Busy()`), C ise `leaf-waiting` state'inde Active bekler.
State function en derindeki aktif subflow'un status'unu raporladigi icin client'in
gordugu tek sinyal C'nin status'udur.

Client A uzerinden `finish-leaf` transition'ini `sync=false` ile tetikledigin an:

  * BUG (fix oncesi)  -> 202 doner, ama hicbir sey Busy'e cekilmemistir. Client hemen
    long-poll yaparsa C'yi hala `A` (Active) gorur, "islem yok" diye yorumlar ve akisi
    ilerletmez. C ancak 3 ayri accept + Dapr turundan sonra Busy olur.
  * FIX               -> accept, ReserveSubflowChainAsync ile zinciri leaf'e kadar Busy'e
    ceker; 202 doner donmez state function `B` (Busy) raporlar.

Test tam olarak bunu olcer: 202'nin hemen ardindan state function'i okur ve status'un
`B` olmasini bekler. Ayrica forward'in gercekten C'ye ulasip akisi tamamladigini
dogrular (claim calismazsa leaf 409 verir ve zincir kilitlenir).

On kosullar: orchestration (4201) + execution (4202) host'lari ve docker altyapisi ayakta.
"""

import argparse
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

BASE = "http://localhost:4201/api/v1"
DOMAIN = "core"
ROOT_WF = "chain-busy-root"
MIDDLE_WF = "chain-busy-middle"
LEAF_WF = "chain-busy-leaf"
USER = "11111111-1111-1111-1111-111111111111"
COMPONENT_DIR = Path(__file__).resolve().parents[2] / "core" / "Workflows" / "chain-busy"
PG = ["docker", "exec", "vnext-postgres", "psql", "-U", "postgres",
      "-d", "Aether_WorkflowDb", "-tA", "-c"]


def http(method, url, body=None, timeout=30):
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
    except Exception as e:
        return -1, {"error": str(e)}


def psql(query):
    out = subprocess.run(PG + [query], capture_output=True, text=True, timeout=30)
    return out.stdout.strip() if out.returncode == 0 else f"<psql error: {out.stderr.strip()}>"


def state_of(workflow, instance_id):
    """State function — client'in gordugu goruntu (en derin aktif subflow)."""
    st, body = http("GET", f"{BASE}/{DOMAIN}/workflows/{workflow}/instances/{instance_id}/functions/state")
    if st not in (200, 304):
        return None, None, body
    return body.get("state"), body.get("status"), body


def wait_for(workflow, instance_id, predicate, timeout_s=30):
    deadline = time.time() + timeout_s
    last = None
    while time.time() < deadline:
        state, status, _ = state_of(workflow, instance_id)
        last = (state, status)
        if state is not None and predicate(state, status):
            return True, last
        time.sleep(0.25)
    return False, last


def publish():
    """Leaf-first publish — bir ust seviye referansini cozebilsin diye."""
    for name in ("leaf", "middle", "root"):
        path = COMPONENT_DIR / f"chain-busy-{name}.json"
        body = json.loads(path.read_text())
        st, resp = http("POST", f"{BASE}/definitions/publish", body)
        if st in (200, 201):
            print(f"  published chain-busy-{name}")
        elif st == 409:
            print(f"  chain-busy-{name} zaten publish edilmis (409)")
        else:
            print(f"  ! chain-busy-{name} publish HTTP {st}: {resp}")
            return False
    http("GET", f"{BASE}/definitions/re-initialize")
    print("  re-initialize ok")
    return True


def leaf_instance_id(root_id):
    """A -> B -> C zincirini korelasyonlardan asagi inerek leaf instance'i bulur."""
    middle = psql(
        "select \"SubFlowInstanceId\" from chain_busy_root.\"InstancesCorrelations\""
        f"where \"InstanceId\"='{root_id}' order by \"CreatedAt\" desc limit 1")
    if not middle or middle.startswith("<"):
        return None, None
    leaf = psql(
        "select \"SubFlowInstanceId\" from chain_busy_middle.\"InstancesCorrelations\""
        f"where \"InstanceId\"='{middle}' order by \"CreatedAt\" desc limit 1")
    if not leaf or leaf.startswith("<"):
        return middle, None
    return middle, leaf


def status_row(schema, instance_id):
    return psql(f"select \"Status\" from {schema}.\"Instances\" where \"Id\"='{instance_id}'")


def run_iteration(it):
    r = {"it": it, "ok": False, "errors": []}
    test_id = f"chain-busy-{it}-{uuid.uuid4().hex[:8]}"

    # 1) A'yi baslat — auto zincir A -> B -> C'yi kurar, C leaf-waiting'de Active bekler.
    st, body = http("POST", f"{BASE}/{DOMAIN}/workflows/{ROOT_WF}/instances/start", {"testId": test_id})
    if st not in (200, 201, 202):
        r["errors"].append(f"start HTTP {st}: {body}")
        return r
    root_id = body.get("id") or body.get("Id")
    r["rootId"] = root_id

    # 2) Zincirin kurulmasini bekle: state function C'nin state'ini raporlamali.
    ok, last = wait_for(ROOT_WF, root_id, lambda s, _: s == "leaf-waiting", 45)
    if not ok:
        r["errors"].append(f"leaf-waiting'e ulasilamadi (son: {last})")
        return r

    # 3) BASLANGIC DURUMU — bug'in on kosulu: A ve B Busy, C Active,
    #    ve client (state function) C'nin Active'ini goruyor.
    middle_id, leaf_id = leaf_instance_id(root_id)
    r["middleId"], r["leafId"] = middle_id, leaf_id
    before_state, before_status, _ = state_of(ROOT_WF, root_id)
    r["before"] = {
        "reportedState": before_state,
        "reportedStatus": before_status,
        "rootRow": status_row("chain_busy_root", root_id),
        "middleRow": status_row("chain_busy_middle", middle_id) if middle_id else None,
        "leafRow": status_row("chain_busy_leaf", leaf_id) if leaf_id else None,
    }
    if before_status != "A":
        r["errors"].append(f"on kosul saglanmadi: transition oncesi status {before_status}, beklenen A")
        return r

    # 4) ASIL OLCUM — A uzerinden async transition; 202'nin HEMEN ardindan state function.
    st, resp = http(
        "PATCH",
        f"{BASE}/{DOMAIN}/workflows/{ROOT_WF}/instances/{root_id}/transitions/finish-leaf?sync=false")
    r["acceptStatus"] = st
    if st not in (200, 202):
        r["errors"].append(f"finish-leaf accept HTTP {st}: {resp}")
        return r

    after_state, after_status, _ = state_of(ROOT_WF, root_id)
    r["after"] = {
        "reportedState": after_state,
        "reportedStatus": after_status,
        "leafRow": status_row("chain_busy_leaf", leaf_id) if leaf_id else None,
    }

    # Beklenen: accept zinciri leaf'e kadar Busy'e cektigi icin client B gorur.
    if after_status != "B":
        r["errors"].append(
            f"BUG: 202 sonrasi client status {after_status} gordu (beklenen B). "
            f"leaf DB satiri: {r['after']['leafRow']}")

    # 5) Forward gercekten C'ye ulasti mi — claim calismazsa leaf 409 verir, zincir kilitlenir.
    ok, last = wait_for(ROOT_WF, root_id, lambda s, stt: stt == "C" or s == "root-done", 60)
    if not ok:
        r["errors"].append(f"zincir tamamlanmadi (son: {last}) — forward leaf'te 409 almis olabilir")
        return r
    r["final"] = last

    r["ok"] = not r["errors"]
    return r


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--iterations", type=int, default=5)
    ap.add_argument("--publish", action="store_true", help="calistirmadan once flow'lari publish et")
    args = ap.parse_args()

    print("=" * 72)
    print("Accept-time SubFlow chain reserve testi — chain-busy A -> B -> C")
    print("=" * 72)

    if args.publish:
        print("\nPublish:")
        if not publish():
            return 1

    results = []
    for it in range(1, args.iterations + 1):
        res = run_iteration(it)
        results.append(res)
        mark = "PASS" if res["ok"] else "FAIL"
        print(f"\n[it{it:02d}] {mark}  root={res.get('rootId')}")
        if res.get("before"):
            b = res["before"]
            print(f"       once : client={b['reportedState']}/{b['reportedStatus']}  "
                  f"DB root={b['rootRow']} middle={b['middleRow']} leaf={b['leafRow']}")
        if res.get("after"):
            a = res["after"]
            print(f"       202 sonrasi: client={a['reportedState']}/{a['reportedStatus']}  "
                  f"DB leaf={a['leafRow']}")
        if res.get("final"):
            print(f"       final: {res['final']}")
        for err in res["errors"]:
            print(f"       ! {err}")

    passed = sum(1 for r in results if r["ok"])
    print("\n" + "=" * 72)
    print(f"SONUC: {passed}/{len(results)} gecti")
    print("=" * 72)
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
