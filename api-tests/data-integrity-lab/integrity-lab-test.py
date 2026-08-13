#!/usr/bin/env python3
"""

python3 api-tests/data-integrity-lab/integrity-lab-test.py --publish --iterations 6 --threshold 4 --burst 4

data-integrity-lab — InstanceData v2 (anında persist + lock altında kimlik) uçtan uca testi

Dogruladigi gelistirmeler (vnext feature/busy-as-mutex-locking, InstanceData v2):
  1. SIRALI task zinciri        -> her task ciktisi ANINDA persist edilir; seq2 task'i
     seq1'in ciktisini snapshot'ta GORUR (seq1SeenBySeq2=true) ve her adim ayri
     versiyon satiridir (run-sequential = tam +3 satir).
  2. DataHash dedup (task)      -> zincirin 4. task'i mevcut veriyi bayt-aynen echo'lar;
     merged-hash dedup yeni satir YARATMAZ (+3, +4 degil).
  3. DataHash dedup (updateData)-> {"noop":true} probe'u 202 kabul edilir ama veri
     degismedigi icin satir eklemez.
  4. PARALEL task'lar           -> 4 branch ayni order'da, her biri kendi DbContext'i ile
     mocklab'a HTTP atip kendi key'ini yazar; FOR UPDATE lock serilestirir: VersionNo
     ardisik/teksiz, IsLatest tek, 4 key'in HICHBIRI kaybolmaz (+4 satir).
  5. LOCK cakismasi             -> run-parallel kosarken eszamanli updateData firtinasi
     baslar: task-yazicilar ile updateData-yazicilar ayni satir kilidinde carpisir;
     labUpdateCount == kabul edilen 202 sayisi (kayip/cift artis yok).
  6. Auto gate                  -> threshold dolunca auto-lab-complete ateslenir,
     instance Completed olur; hicbir instance Busy'de takili kalmaz.
  7. Toplam satir matematigi    -> COUNT == 2(start: request payload + start task) +
     3(seq) + 4(par) + 2*kabul202 (her kabul: transition payload'i + counter ciktisi)
     + 1 (ilk noop'un payload'i); COUNT(DISTINCT DataHash) == COUNT.

Kesfedilen runtime kurali (tasarim geregi): AYNI task ayni transition'da AYNI order'la
birden fazla parallel branch'te KULLANILAMAZ — task journal'in ExecutionKey'i
(transition+task+order) cakisir (UX_InstanceTasks_ExecutionKey) ve transition fault eder.
Bu yuzden 4 parallel branch 4 AYRI task tanimina (lab-probe-task-1..4) baglidir.

Kullanim:
  python3 integrity-lab-test.py [--iterations 6] [--threshold 4] [--burst 4] [--publish]

On kosullar: orchestration host (4201) + docker infra (postgres, mocklab) ayakta.
"""

import argparse
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
WF = "data-integrity-lab"
SCHEMA = "data_integrity_lab"
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


def raw_state(instance_id):
    rows = psql(f'SELECT "CurrentState" || \'|\' || "Status" FROM "{SCHEMA}"."Instances" '
                f"WHERE \"Id\" = '{instance_id}'")
    if not rows:
        return None, None
    state, status = rows[0].split("|")
    return state, status


def wait_state(instance_id, predicate, timeout_s, poll=0.4):
    deadline = time.time() + timeout_s
    last = (None, None)
    while time.time() < deadline:
        last = raw_state(instance_id)
        if predicate(*last):
            return True, last
        time.sleep(poll)
    return False, last


def data_row_count(instance_id):
    rows = psql(f'SELECT COUNT(*) FROM "{SCHEMA}"."InstancesData" WHERE "InstanceId" = \'{instance_id}\'')
    return int(rows[0])


def run_iteration(it, threshold, burst):
    r = {"it": it, "ok": False, "errors": [], "accepted": 0, "conflict409": 0}
    try:
        # 1) Start ---------------------------------------------------------------------------
        status, body = http("POST", f"{BASE}/{DOMAIN}/workflows/{WF}/instances/start",
                            {"testId": f"dil-{it}-{uuid.uuid4().hex[:8]}", "labThreshold": threshold})
        if status not in (200, 201, 202):
            r["errors"].append(f"start HTTP {status}: {body}")
            return r
        pid = body.get("id") or body.get("Id")
        r["id"] = pid
        log(it, f"instance {pid} started")

        ok, last = wait_state(pid, lambda s, st: s == "lab-sequential" and st == "A", 30)
        if not ok:
            r["errors"].append(f"lab-sequential Active beklenirken kaldi (son: {last})")
            return r
        # Start yolu 2 satir yazar: request payload'in ilk versiyonu + start task'inin ciktisi.
        count_after_start = data_row_count(pid)
        if count_after_start != 2:
            r["errors"].append(f"start sonrasi satir {count_after_start} != 2")

        # 2) SIRALI zincir: seq1..3 + dedup echo  (beklenen delta: tam +3) --------------------
        st, bd = http("PATCH", f"{BASE}/{DOMAIN}/workflows/{WF}/instances/{pid}/transitions/run-sequential")
        if st not in (200, 202):
            r["errors"].append(f"run-sequential HTTP {st}: {bd}")
            return r
        ok, last = wait_state(pid, lambda s, s2: s == "lab-parallel" and s2 == "A", 30)
        if not ok:
            r["errors"].append(f"lab-parallel'e ulasamadi (son: {last})")
            return r
        count_after_seq = data_row_count(pid)
        if count_after_seq - count_after_start != 3:
            r["errors"].append(
                f"SIRALI delta {count_after_seq - count_after_start} != 3 "
                f"(dedup echo satir yaratti ya da bir task ciktisi kayip!)")
        else:
            log(it, "sequential: +3 satir (dedup echo satir yaratmadi)")

        # 3) PARALEL 4 branch + ES ZAMANLI updateData firtinasi (lock cakismasi) --------------
        st, bd = http("PATCH", f"{BASE}/{DOMAIN}/workflows/{WF}/instances/{pid}/transitions/run-parallel")
        if st not in (200, 202):
            r["errors"].append(f"run-parallel HTTP {st}: {bd}")
            return r

        # updateData'lar run-parallel pipeline'i kosarken de kabul edilir (Unconditional
        # admission, status-neutral) — task-yazicilarla ayni FOR UPDATE kilidinde carpisirlar.
        # Firtinayi threshold-1'de durduruyoruz: gate son kabulle, kontrollu ateslenecek.
        upd_url = f"{BASE}/{DOMAIN}/workflows/{WF}/instances/{pid}/transitions/update-lab-progress"
        storm_target = threshold - 1
        attempts = 0
        with cf.ThreadPoolExecutor(max_workers=burst) as pool:
            while r["accepted"] < storm_target and attempts < threshold * 40:
                need = storm_target - r["accepted"]
                futs = [pool.submit(http, "PATCH", upd_url,
                                    {"updateNonce": f"{it}-{uuid.uuid4().hex[:6]}"})
                        for _ in range(min(burst, need + 1))]
                attempts += len(futs)
                for f in futs:
                    st2, bd2 = f.result()
                    if st2 in (200, 202):
                        r["accepted"] += 1
                    elif st2 == 409:
                        r["conflict409"] += 1  # ayni-an dedupe — beklenen
                    else:
                        r["errors"].append(f"updateData beklenmeyen HTTP {st2}: {bd2}")
                time.sleep(0.12)
        log(it, f"updateData firtinasi: {r['accepted']} kabul (202), {r['conflict409']} çakışma (409), {attempts} deneme")

        # 3b) Sakinlesme: paralel yazimlar + kabul edilen tum updateData'lar islenmis olmali
        #     (satir sayisi tam beklenen degere oturana kadar bekle) — noop dedup probe'unun
        #     olcumu ancak sakin sistemde anlamli. Her kabul edilen updateData 2 satirdir:
        #     transition request payload'i (updateNonce) + counter task ciktisi.
        expected_before_noop = 2 + 3 + 4 + 2 * r["accepted"]
        deadline = time.time() + 30
        count_now = -1
        while time.time() < deadline:
            state_now, _ = raw_state(pid)
            count_now = data_row_count(pid)
            if state_now == "lab-collect" and count_now == expected_before_noop:
                break
            time.sleep(0.5)
        if count_now != expected_before_noop:
            r["errors"].append(
                f"sakinlesme basarisiz: satir {count_now} != beklenen {expected_before_noop} "
                f"(kayip yazim ya da dedup kacagi!)")
            return r

        # 3c) Noop dedup probe (sakin sistemde), iki asamali:
        #     - noop#1: {"noop":true} payload'i head'e YENI key ekler -> tam +1 satir
        #       (counter task'i delta'sinda mevcut key'i ayni degerle dondugu icin task
        #       ciktisi dedup'lanir — +2 degil +1 olmasi task-dedup kanitidir).
        #     - noop#2: ayni payload artik head'de -> payload DA task ciktisi DA dedup ->
        #       +0 satir (cift tarafli DataHash dedup kaniti).
        def patch_until_accepted(body, timeout_s=15):
            """409 (aktif job dedupe'u) gorurse kabul edilene kadar tekrar dener."""
            end = time.time() + timeout_s
            while True:
                st_x, bd_x = http("PATCH", upd_url, body)
                if st_x != 409 or time.time() > end:
                    if st_x == 409:
                        r["conflict409"] += 1
                    return st_x, bd_x
                r["conflict409"] += 1
                time.sleep(0.3)

        st_noop, _ = patch_until_accepted({"noop": True})
        time.sleep(1.5)
        after_noop1 = data_row_count(pid)
        if st_noop not in (200, 202):
            r["errors"].append(f"noop#1 updateData HTTP {st_noop}")
        elif after_noop1 != expected_before_noop + 1:
            r["errors"].append(
                f"noop#1 satir beklentisi tutmadi ({expected_before_noop} -> {after_noop1}, "
                f"beklenen +1) — task-dedup kacagi!")

        st_noop2, _ = patch_until_accepted({"noop": True})
        time.sleep(1.5)
        after_noop2 = data_row_count(pid)
        if st_noop2 not in (200, 202):
            r["errors"].append(f"noop#2 updateData HTTP {st_noop2}")
        elif after_noop2 != after_noop1:
            r["errors"].append(
                f"noop#2 satir ekledi ({after_noop1} -> {after_noop2}) — dedup calismiyor!")
        else:
            log(it, "noop dedup probe: #1 +1 (payload), #2 +0 (tam dedup) — OK")

        # 3d) Son kabul: threshold'a tamamla -> gate ateslenmeli.
        deadline = time.time() + 20
        while r["accepted"] < threshold and time.time() < deadline:
            st2, bd2 = http("PATCH", upd_url, {"updateNonce": f"{it}-final-{uuid.uuid4().hex[:6]}"})
            if st2 in (200, 202):
                r["accepted"] += 1
            elif st2 == 409:
                r["conflict409"] += 1
                time.sleep(0.3)
            else:
                r["errors"].append(f"final updateData beklenmeyen HTTP {st2}: {bd2}")
                break
        if r["accepted"] < threshold:
            r["errors"].append(f"threshold kadar 202 toplanamadi ({r['accepted']}/{threshold})")
            return r

        # 4) Gate ateslenip Completed olmali ---------------------------------------------------
        ok, last = wait_state(pid, lambda s, st3: st3 == "C", 45, poll=0.6)
        if not ok:
            r["errors"].append(f"Completed'a ulasamadi (son: {last})  [gate/stuck-Busy regresyonu?]")
            return r
        log(it, "instance Completed")

        r["ok"] = not r["errors"]
        return r
    except Exception as e:
        r["errors"].append(f"exception: {e}")
        return r


def db_asserts(results):
    failures = []
    for r in results:
        pid = r.get("id")
        if not pid:
            continue
        it = r["it"]
        # start(2) + seq(3) + par(4) + 2*kabul (payload+counter) + 1 (noop#1 payload'i)
        expected_rows = 2 + 3 + 4 + 2 * r["accepted"] + 1

        # a) Toplam satir matematigi + DataHash tekilligi
        rows = psql(f'SELECT COUNT(*), COUNT(DISTINCT "DataHash") '
                    f'FROM "{SCHEMA}"."InstancesData" WHERE "InstanceId" = \'{pid}\'')
        cnt, dsth = (int(x) for x in rows[0].split("|"))
        if cnt != expected_rows:
            failures.append(f"it{it:02d}: satir sayisi {cnt} != beklenen {expected_rows} "
                            f"(kayip yazim ya da dedup kacagi!)")

        # b) VersionNo line-scoped: HER Version grubu icinde ardisik/teksiz 1..k
        rows = psql(f'SELECT "Version", COUNT(*), MAX("VersionNo"), MIN("VersionNo"), '
                    f'COUNT(DISTINCT "VersionNo") '
                    f'FROM "{SCHEMA}"."InstancesData" WHERE "InstanceId" = \'{pid}\' '
                    f'GROUP BY "Version"')
        for line in rows:
            ver, lcnt, lmx, lmn, ldst = line.split("|")
            if not (int(lcnt) == int(lmx) == int(ldst)) or int(lmn) != 1:
                failures.append(f"it{it:02d}: VersionNo line tutarsiz — {ver}: "
                                f"count={lcnt} min={lmn} max={lmx} distinct={ldst}")

        # c) DataHash: her satir farkli icerik (bayt-aynı ardisik versiyon = dedup kacagi)
        if dsth != cnt:
            failures.append(f"it{it:02d}: DataHash tekrarli — {dsth} distinct / {cnt} satir")

        # d) IsLatest tam 1 ve kendi Version line'inin MAX'inda
        rows = psql(f'SELECT COUNT(*) FROM "{SCHEMA}"."InstancesData" '
                    f'WHERE "InstanceId" = \'{pid}\' AND "IsLatest"')
        if int(rows[0]) != 1:
            failures.append(f"it{it:02d}: IsLatest satir sayisi {rows[0]} != 1")
        rows = psql(f'SELECT COUNT(*) FROM "{SCHEMA}"."InstancesData" l '
                    f'WHERE l."InstanceId" = \'{pid}\' AND l."IsLatest" '
                    f'AND l."VersionNo" <> (SELECT MAX(d."VersionNo") FROM "{SCHEMA}"."InstancesData" d '
                    f'WHERE d."InstanceId" = l."InstanceId" AND d."Version" = l."Version")')
        if rows and int(rows[0]) != 0:
            failures.append(f"it{it:02d}: IsLatest kendi line'inin MAX VersionNo'sunda degil")

        # e) Kayipsiz merge + siralilik probe'u + sayac
        rows = psql(f'SELECT "Data" FROM "{SCHEMA}"."InstancesData" '
                    f'WHERE "InstanceId" = \'{pid}\' AND "IsLatest"')
        data = json.loads(rows[0]) if rows else {}
        for key in ["seq1", "seq2", "seq3", "par1", "par2", "par3", "par4"]:
            if data.get(key) is not True:
                failures.append(f"it{it:02d}: latest data '{key}' icermiyor — YAZIM KAYBI!")
        if data.get("seq1SeenBySeq2") is not True:
            failures.append(f"it{it:02d}: seq2 task'i seq1'i GORMEDI — anında-persist/snapshot sirasi bozuk!")
        if int(data.get("labUpdateCount", -1)) != r["accepted"]:
            failures.append(f"it{it:02d}: labUpdateCount={data.get('labUpdateCount')} != kabul {r['accepted']}")

        # f) Busy kalmadi
        rows = psql(f'SELECT "Status" FROM "{SCHEMA}"."Instances" WHERE "Id" = \'{pid}\'')
        if rows and rows[0] == "B":
            failures.append(f"it{it:02d}: instance BUSY kaldi — stuck-Busy!")
    return failures


def publish_definitions():
    root = Path(__file__).resolve().parents[2] / "core"
    docs = [
        root / "Tasks" / "data-integrity-lab" / "lab-script-task.json",
        root / "Tasks" / "data-integrity-lab" / "lab-probe-task-1.json",
        root / "Tasks" / "data-integrity-lab" / "lab-probe-task-2.json",
        root / "Tasks" / "data-integrity-lab" / "lab-probe-task-3.json",
        root / "Tasks" / "data-integrity-lab" / "lab-probe-task-4.json",
        root / "Workflows" / "data-integrity-lab" / "data-integrity-lab.json",
    ]
    for path in docs:
        doc = json.loads(path.read_text())
        status, body = http("POST", f"{BASE}/definitions/publish", doc, timeout=60)
        if status in (200, 201):
            print(f"  publish {path.name}: OK (v{doc['version']})")
        elif status == 409 and "100002" in json.dumps(body):
            print(f"  publish {path.name}: SKIP (v{doc['version']} zaten yayinda)")
        else:
            print(f"  publish {path.name}: HTTP {status}: {json.dumps(body)[:300]}")
            sys.exit(1)
    http("GET", f"{BASE}/definitions/re-initialize")
    time.sleep(2)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--iterations", type=int, default=6)
    ap.add_argument("--threshold", type=int, default=4)
    ap.add_argument("--burst", type=int, default=4)
    ap.add_argument("--publish", action="store_true", help="once task+flow tanimlarini publish et")
    args = ap.parse_args()

    if args.publish:
        print("== Definition publish ==")
        publish_definitions()

    print(f"== {args.iterations} iterasyon eszamanli basliyor (threshold={args.threshold}, burst={args.burst}) ==")
    t0 = time.time()
    with cf.ThreadPoolExecutor(max_workers=args.iterations) as pool:
        results = list(pool.map(lambda i: run_iteration(i, args.threshold, args.burst),
                                range(1, args.iterations + 1)))
    elapsed = time.time() - t0

    print("\n== DB tutarlilik assertleri ==")
    db_failures = db_asserts(results)

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
        print("DB assertleri  : PASS (satir matematigi, VersionNo, DataHash, IsLatest, "
              "kayipsiz merge, sayac, Busy yok)")

    verdict = passed == len(results) and not db_failures
    print("=" * 64)
    print("SONUC:", "PASS — sirali/paralel task + dedup + lock cakismasi tutarli" if verdict
          else "FAIL — yukaridaki bulgulari incele")
    sys.exit(0 if verdict else 1)


if __name__ == "__main__":
    main()
