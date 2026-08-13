#!/usr/bin/env python3
"""

python3 api-tests/subflow-orchestration/updatedata-concurrency-test.py --iterations 20 --threshold 8 --burst 6

updateData eszamanli veri tutarliligi testi — subflow-orchestration-parent

Dogruladigi gelistirmeler (vnext feature/busy-as-mutex-locking):
  1. updateData HER kosulda auto transition degerlendirir  -> parent-collect'teki
     updateCount >= updateThreshold gate'i ateslenir (F1 fix).
  2. updateData instance status'una ASLA dokunmaz          -> firtina sonrasi hicbir
     instance Busy'de takili kalmaz (F1a/F8 fix).
  3. Eszamanli InstanceData yazimlari                      -> VersionNo'lar ardisik ve
     teksiz, IsLatest tam 1 satir (explicit write service + FOR UPDATE lock).
  4. Sayac tutarliligi                                     -> kabul edilen (202) her
     updateData tam 1 artis; final updateCount == kabul sayisi (kayip/cift artis yok).
  5. Aktif subflow'da updateData data-only                 -> state degismez, subflow
     restart olmaz, ikinci child korelasyonu olusmaz.
  6. Tam yasam dongusu: child + grandchild surulur, zincir yukari cozulur,
     parent Completed'a ulasir (subflow resume + $self auto zinciri saglikli).

Kullanim:
  python3 updatedata-concurrency-test.py [--iterations 15] [--threshold 5] [--burst 4]

On kosullar: orchestration host (4201) + docker infra ayakta; duzenlenen
subflow-orchestration flow'lari publish edilmis olmali (--publish ile script yapar).
"""

import argparse
import base64
import concurrent.futures as cf
import json
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

BASE = "http://localhost:4201/api/v1"
DOMAIN = "core"
PARENT_WF = "subflow-orchestration-parent"
CHILD_WF = "subflow-orchestration-child"
GRANDCHILD_WF = "subflow-orchestration-grandchild"
USER = "11111111-1111-1111-1111-111111111111"
PG = ["docker", "exec", "vnext-postgres", "psql", "-U", "postgres", "-d", "Aether_WorkflowDb", "-tA", "-c"]

PRINT_LOCK = threading.Lock()


def log(iteration, msg):
    with PRINT_LOCK:
        print(f"  [it{iteration:02d}] {msg}", flush=True)


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
    except Exception as e:  # connection errors
        return -1, {"error": str(e)}


def psql(query):
    out = subprocess.run(PG + [query], capture_output=True, text=True, timeout=30)
    if out.returncode != 0:
        raise RuntimeError(f"psql failed: {out.stderr.strip()}")
    return [line for line in out.stdout.strip().splitlines() if line]


def get_state(workflow, instance_id):
    """State function -> (currentState, status)."""
    status, body = http("GET", f"{BASE}/{DOMAIN}/workflows/{workflow}/instances/{instance_id}/functions/state")
    if status != 200:
        return None, None
    # Savunmaci parse: state/status alanlarini govdenin neresinde olursa olsun bul.
    def find(obj, keys):
        if isinstance(obj, dict):
            for k, v in obj.items():
                if k in keys and isinstance(v, str):
                    return v
            for v in obj.values():
                r = find(v, keys)
                if r:
                    return r
        return None
    return find(body, {"state", "currentState"}), find(body, {"status"})


def wait_for(workflow, instance_id, predicate, timeout_s, poll=0.4):
    deadline = time.time() + timeout_s
    last = (None, None)
    while time.time() < deadline:
        last = get_state(workflow, instance_id)
        if predicate(*last):
            return True, last
        time.sleep(poll)
    return False, last


def run_iteration(it, threshold, burst):
    r = {"it": it, "ok": False, "errors": [], "accepted": 0, "conflict409": 0}
    try:
        # 1) Start (async) -----------------------------------------------------------------
        status, body = http("POST", f"{BASE}/{DOMAIN}/workflows/{PARENT_WF}/instances/start",
                            {"testId": f"upd-conc-{it}-{uuid.uuid4().hex[:8]}", "updateThreshold": threshold})
        if status not in (200, 201, 202):
            r["errors"].append(f"start HTTP {status}: {body}")
            return r
        pid = body.get("id") or body.get("Id")
        r["parentId"] = pid
        log(it, f"parent {pid} started")

        # 2) collect state'ini bekle. NOT: auto transition'i olan state Busy PARK eder
        #    (ResolveAvailableStep tasarimi) — updateData Unconditional oldugu icin Busy'de de
        #    kabul edilir; bu yuzden yalniz state'i bekleriz, status'u degil.
        ok, last = wait_for(PARENT_WF, pid, lambda s, st: s == "parent-collect", 30)
        if not ok:
            r["errors"].append(f"parent-collect'e ulasamadi (son: {last})")
            return r

        # 3) Eszamanli updateData firtinasi: tam `threshold` adet 202 toplayana kadar --------
        url = f"{BASE}/{DOMAIN}/workflows/{PARENT_WF}/instances/{pid}/transitions/update-parent-progress"
        attempts = 0
        with cf.ThreadPoolExecutor(max_workers=burst) as pool:
            while r["accepted"] < threshold and attempts < threshold * 30:
                need = threshold - r["accepted"]
                futs = [pool.submit(http, "PATCH", url, {"updateNonce": f"{it}-{uuid.uuid4().hex[:6]}"})
                        for _ in range(min(burst, need + 2))]
                attempts += len(futs)
                for f in futs:
                    st, bd = f.result()
                    if st in (200, 202):
                        r["accepted"] += 1
                    elif st == 409:
                        r["conflict409"] += 1  # ayni anda aktif job dedupe'u — beklenen
                    else:
                        r["errors"].append(f"updateData beklenmeyen HTTP {st}: {bd}")
                time.sleep(0.15)
        log(it, f"updateData: {r['accepted']} kabul (202), {r['conflict409']} çakışma (409), {attempts} deneme")
        if r["accepted"] < threshold:
            r["errors"].append(f"threshold kadar 202 toplanamadi ({r['accepted']}/{threshold})")
            return r

        # 4) Gate ateslenmeli: parent'in HAM state'i parent-subflow-state olmali.
        #    (State function subflow aktiflesince child gorunumune delege eder — DB'den okuruz.)
        deadline = time.time() + 30
        raw_state = None
        while time.time() < deadline:
            rows = psql(f'SELECT "CurrentState" FROM "subflow_orchestration_parent"."Instances" '
                        f'WHERE "Id" = \'{pid}\'')
            raw_state = rows[0] if rows else None
            if raw_state == "parent-subflow-state":
                break
            time.sleep(0.5)
        if raw_state != "parent-subflow-state":
            r["errors"].append(f"GATE ATESLENMEDI - kaldigi state: {raw_state}  [F1/F7 regresyonu!]")
            return r
        log(it, "gate ateslendi -> parent-subflow-state")

        # 5) Aktif subflow'da data-only updateData: parent'in HAM state'i degismemeli.
        #    (State function aktif subflow'un gorunumune delege eder — o yuzden DB'den okuruz.)
        st1, _ = http("PATCH", url, {"updateNonce": f"{it}-subflow-probe"})
        time.sleep(1.2)
        rows = psql(f'SELECT "CurrentState" FROM "subflow_orchestration_parent"."Instances" '
                    f'WHERE "Id" = \'{pid}\'')
        if rows and rows[0] != "parent-subflow-state":
            r["errors"].append(f"data-only updateData parent state degistirdi: {rows[0]}")
        corr = psql(f'SELECT COUNT(*) FROM "subflow_orchestration_parent"."InstancesCorrelations" '
                    f'WHERE "InstanceId" = \'{pid}\'')
        if corr and int(corr[0]) > 1:
            r["errors"].append(f"subflow restart suphesi: {corr[0]} korelasyon")

        # 6) Orkestrasyonu tamamla: child -> grandchild -> zincir yukari ---------------------
        rows = psql(f'SELECT "SubFlowInstanceId" FROM "subflow_orchestration_parent"."InstancesCorrelations" '
                    f'WHERE "InstanceId" = \'{pid}\' LIMIT 1')
        if not rows:
            r["errors"].append("child korelasyonu bulunamadi")
            return r
        child_id = rows[0]

        ok, last = wait_for(CHILD_WF, child_id, lambda s, st: s == "child-manual-state" and st == "A", 30)
        if not ok:
            r["errors"].append(f"child manual-state'e ulasamadi (son: {last})")
            return r
        st, bd = http("PATCH", f"{BASE}/{DOMAIN}/workflows/{CHILD_WF}/instances/{child_id}/transitions/proceed-to-subflow")
        if st not in (200, 202):
            r["errors"].append(f"child proceed HTTP {st}: {bd}")
            return r

        gc_id = None
        deadline = time.time() + 30
        while time.time() < deadline and not gc_id:
            rows = psql(f'SELECT "SubFlowInstanceId" FROM "subflow_orchestration_child"."InstancesCorrelations" '
                        f'WHERE "InstanceId" = \'{child_id}\' LIMIT 1')
            gc_id = rows[0] if rows else None
            if not gc_id:
                time.sleep(0.5)
        if not gc_id:
            r["errors"].append("grandchild korelasyonu bulunamadi")
            return r

        ok, _ = wait_for(GRANDCHILD_WF, gc_id, lambda s, st: s == "grandchild-initial" and st == "A", 30)
        if ok:
            http("PATCH", f"{BASE}/{DOMAIN}/workflows/{GRANDCHILD_WF}/instances/{gc_id}/transitions/complete-grandchild")

        # 7) Parent Completed'a ulasmali -----------------------------------------------------
        ok, last = wait_for(PARENT_WF, pid, lambda s, st: st == "C", 60, poll=0.8)
        if not ok:
            r["errors"].append(f"parent Completed'a ulasamadi (son: {last})  [stuck-Busy/resume regresyonu?]")
            return r
        log(it, "parent Completed")

        r["ok"] = not r["errors"]
        return r
    except Exception as e:
        r["errors"].append(f"exception: {e}")
        return r


def db_asserts(results, threshold):
    """DB seviyesinde veri tutarliligi assertleri (tum iterasyonlar icin toplu)."""
    failures = []
    for r in results:
        pid = r.get("parentId")
        if not pid:
            continue
        it = r["it"]

        # a) VersionNo line-scoped teksiz/ardisik: HER Version grubu icinde 1..k
        rows = psql(f'SELECT "Version", COUNT(*), MAX("VersionNo"), MIN("VersionNo"), '
                    f'COUNT(DISTINCT "VersionNo") '
                    f'FROM "subflow_orchestration_parent"."InstancesData" '
                    f'WHERE "InstanceId" = \'{pid}\' GROUP BY "Version"')
        for line in rows:
            ver, lcnt, lmx, lmn, ldst = line.split("|")
            if not (int(lcnt) == int(lmx) == int(ldst)) or int(lmn) != 1:
                failures.append(f"it{it:02d}: VersionNo line tutarsiz — {ver}: "
                                f"count={lcnt} min={lmn} max={lmx} distinct={ldst} (bosluk/cift!)")

        # b) IsLatest tam 1
        rows = psql(f'SELECT COUNT(*) FROM "subflow_orchestration_parent"."InstancesData" '
                    f'WHERE "InstanceId" = \'{pid}\' AND "IsLatest"')
        if int(rows[0]) != 1:
            failures.append(f"it{it:02d}: IsLatest satir sayisi {rows[0]} != 1")

        # c) Sayac == kabul edilen 202 sayisi (kayip artis yok). Gate atesledikten sonra
        #    subflow'da atilan data-only probe sayaci ARTIRMAZ (task'lar atlanir).
        rows = psql(f'SELECT "Data"->>\'updateCount\' FROM "subflow_orchestration_parent"."InstancesData" '
                    f'WHERE "InstanceId" = \'{pid}\' AND "IsLatest"')
        final_count = int(rows[0]) if rows and rows[0] else -1
        if final_count != r.get("accepted", -2):
            failures.append(f"it{it:02d}: updateCount={final_count} != kabul edilen {r.get('accepted')} (kayip/cift artis!)")

        # d) SAHIPSIZ Busy yok: Busy yalniz aktif (tamamlanmamis) bir subflow korelasyonu
        #    varken mesrudur (yarim kalan iterasyonlar dahil). Korelasyonsuz Busy = stuck.
        rows = psql(f'SELECT i."Status", COUNT(c."Id") FILTER (WHERE NOT c."IsCompleted") '
                    f'FROM "subflow_orchestration_parent"."Instances" i '
                    f'LEFT JOIN "subflow_orchestration_parent"."InstancesCorrelations" c '
                    f'  ON c."InstanceId" = i."Id" '
                    f'WHERE i."Id" = \'{pid}\' GROUP BY i."Status"')
        if rows:
            status_val, open_corr = rows[0].split("|")
            if status_val == "B" and int(open_corr) == 0:
                failures.append(f"it{it:02d}: sahipsiz BUSY (aktif subflow yok) — stuck-Busy regresyonu!")
    return failures


def publish_flows():
    """Duzenlenen 3 flow'u publish eder (JSON dosyalari PublishInput seklindedir)."""
    root = Path(__file__).resolve().parents[2] / "core" / "Workflows" / "subflow-orchestration"
    for name in [f"{GRANDCHILD_WF}.json", f"{CHILD_WF}.json", f"{PARENT_WF}.json"]:
        doc = json.loads((root / name).read_text())
        status, body = http("POST", f"{BASE}/definitions/publish", doc, timeout=60)
        if status in (200, 201):
            print(f"  publish {name}: OK (v{doc['version']})")
        elif status == 409 and "100002" in json.dumps(body):
            print(f"  publish {name}: SKIP (v{doc['version']} zaten yayinda)")
        else:
            print(f"  publish {name}: HTTP {status}: {json.dumps(body)[:200]}")
            sys.exit(1)
    http("GET", f"{BASE}/definitions/re-initialize")
    time.sleep(2)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--iterations", type=int, default=15)
    ap.add_argument("--threshold", type=int, default=5)
    ap.add_argument("--burst", type=int, default=4)
    ap.add_argument("--publish", action="store_true", help="once flow'lari publish et")
    args = ap.parse_args()

    if args.publish:
        print("== Flow publish ==")
        publish_flows()

    print(f"== {args.iterations} iterasyon eszamanli basliyor (threshold={args.threshold}, burst={args.burst}) ==")
    t0 = time.time()
    with cf.ThreadPoolExecutor(max_workers=args.iterations) as pool:
        results = list(pool.map(lambda i: run_iteration(i, args.threshold, args.burst),
                                range(1, args.iterations + 1)))
    elapsed = time.time() - t0

    print("\n== DB tutarlilik assertleri ==")
    db_failures = db_asserts(results, args.threshold)

    print("\n" + "=" * 64)
    passed = sum(1 for r in results if r["ok"])
    total409 = sum(r["conflict409"] for r in results)
    print(f"Akis sonuclari : {passed}/{len(results)} iterasyon PASS  ({elapsed:.1f}s)")
    print(f"409 dagilimi   : toplam {total409} (ayni-an dedupe — beklenen davranis)")
    for r in results:
        if not r["ok"]:
            for e in r["errors"]:
                print(f"  FAIL it{r['it']:02d}: {e}")
    if db_failures:
        print("DB assertleri  : FAIL")
        for f in db_failures:
            print(f"  {f}")
    else:
        print("DB assertleri  : PASS (VersionNo ardisik/teksiz, IsLatest=1, sayac=kabul, Busy yok)")

    verdict = passed == len(results) and not db_failures
    print("=" * 64)
    print("SONUC:", "PASS — gelistirme eszamanli updateData altinda tutarli" if verdict
          else "FAIL — yukaridaki bulgulari incele")
    sys.exit(0 if verdict else 1)


if __name__ == "__main__":
    main()
