#!/usr/bin/env python3
"""
script-race-lab yuk surucusu — paylasilan ALC'de cift-derleme yarisini olcer.

    python3 api-tests/script-race-lab/race-load.py --parallel 30
    python3 api-tests/script-race-lab/race-load.py --publish --parallel 30 --timeout 240

N instance AYNI ANDA baslatilir (ThreadPoolExecutor, max_workers=N). Ortusme olcumun
kendisidir: parent'in output mapping'i global script helper yuzunden paylasilan bir
AssemblyLoadContext'e derlenir. Ayni Roslyn emit penceresine dusen N derlemede fix'siz
runtime kaybedenlere `FileLoadException: Assembly with same name is already loaded`
atar; mapping duser ve parent KALICI olarak fault'lanir (`Instance:100030`).

Fix'li runtime'da 30/30 `C` beklenir. Baslatma penceresi (ilk/son start yaniti arasi)
saniyenin cok altinda olmali — seri baslatan bir surucu hicbir sey kanitlamaz.

Incident metni instance detay yanitinin `metadata.incident` alanindadir
(`active`, yoksa `history[0]`: message / errorCode / errorLayer).

JMeter plani (jmeter/tests/script-race-lab.jmx) ayni olcumu hedefler; bu surucu
JMeter kurulu olmayan makineler icin stdlib-only karsiligidir.
"""

import argparse
import importlib.util
import json
import statistics
import sys
import time
import urllib.error
import urllib.request
import uuid
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

BASE = "http://localhost:4201/api/v1"
DOMAIN = "core"
PARENT_WF = "script-race-lab-parent"
TERMINAL = {"C", "F", "P"}
POLL_INTERVAL_S = 0.5
# Reproduksiyon imzasi: fault metninde bunlardan HERHANGI biri gorulmeli.
SIGNATURE_MARKERS = ("Instance:100030", "FileLoadException", "already loaded")


def http(method, url, body=None, timeout=60):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(url, data=data, method=method,
                                     headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            raw = response.read().decode()
            return response.status, (json.loads(raw) if raw else {})
    except urllib.error.HTTPError as error:
        raw = error.read().decode()
        try:
            return error.code, json.loads(raw)
        except json.JSONDecodeError:
            return error.code, {"raw": raw}
    except Exception as error:  # noqa: BLE001 — surucu, hata raporlanir
        return -1, {"error": str(error)}


def publish():
    """Kardes publish.py'yi oldugu gibi calistirir; bilesen listesi orada tek yerde durur."""
    path = Path(__file__).resolve().parent / "publish.py"
    spec = importlib.util.spec_from_file_location("script_race_lab_publish", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.main() == 0


def start_one(index):
    body = {"testId": "race-load-%d-%s" % (index, uuid.uuid4().hex[:8])}
    dispatched_at = time.time()
    status, response = http(
        "POST", "%s/%s/workflows/%s/instances/start?sync=false" % (BASE, DOMAIN, PARENT_WF), body)
    return {"index": index, "testId": body["testId"], "dispatchedAt": dispatched_at,
            "startedAt": time.time(), "httpStatus": status, "id": response.get("id"),
            "startError": None if status in (200, 202) else json.dumps(response)[:300]}


def incident_text(instance_id):
    """Detay yanitindaki `metadata.incident`ten fault metnini toplar."""
    status, detail = http("GET", "%s/%s/workflows/%s/instances/%s" % (BASE, DOMAIN, PARENT_WF, instance_id))
    if status != 200:
        return "instance detay HTTP %s: %s" % (status, json.dumps(detail)[:200])
    incident = (detail.get("metadata") or {}).get("incident") or {}
    entry = incident.get("active") or (incident.get("history") or [None])[0]
    if not entry:
        return "detayda incident yok (metadata.incident bos)"
    return " | ".join(str(entry.get(field)) for field in
                      ("errorCode", "errorLayer", "state", "transition", "task", "message")
                      if entry.get(field) is not None)


def settle_one(record, timeout_s):
    """Terminale (veya timeout'a) kadar poll eder; kayda status/settle/error yazar."""
    if not record["id"]:
        record["status"] = "START-FAIL"
        return record
    url = "%s/%s/workflows/%s/instances/%s/functions/state" % (BASE, DOMAIN, PARENT_WF, record["id"])
    deadline = record["startedAt"] + timeout_s
    status = None
    while time.time() < deadline:
        code, body = http("GET", url, timeout=30)
        if code in (200, 304):
            status = body.get("status")
            if status in TERMINAL:
                break
        time.sleep(POLL_INTERVAL_S)
    record["settleS"] = time.time() - record["startedAt"]
    record["status"] = status if status in TERMINAL else "TIMEOUT(%s)" % status
    record["incident"] = incident_text(record["id"]) if record["status"] == "F" else None
    return record


def main():
    parser = argparse.ArgumentParser(description="script-race-lab paralel yuk surucusu")
    parser.add_argument("--parallel", type=int, default=30, help="baslatilacak instance sayisi")
    parser.add_argument("--publish", action="store_true", help="olcumden once publish adimini kos")
    parser.add_argument("--timeout", type=int, default=180, help="instance basina settle butcesi (s)")
    args = parser.parse_args()

    if args.publish:
        print("Publish:")
        if not publish():
            return 1

    print("\n%d instance baslatiliyor (max_workers=%d)..." % (args.parallel, args.parallel))
    with ThreadPoolExecutor(max_workers=args.parallel) as pool:
        records = list(pool.map(start_one, range(args.parallel)))

    stamps = [r["startedAt"] for r in records if r["id"]]
    sent = [r["dispatchedAt"] for r in records if r["id"]]
    window_ms = (max(stamps) - min(stamps)) * 1000 if stamps else 0.0
    # Dispatch yayilimi istegin GERCEKTEN es zamanli gonderildiginin kanitidir; yanit
    # penceresi buna ek olarak sunucunun accept gecikmesini de tasir.
    dispatch_ms = (max(sent) - min(sent)) * 1000 if sent else 0.0
    print("  dispatch yayilimi: %.1f ms | yanit penceresi: %.1f ms (%d/%d kabul edildi)"
          % (dispatch_ms, window_ms, len(stamps), args.parallel))
    for record in records:
        if record["startError"]:
            print("  ! start[%d] HTTP %s: %s" % (record["index"], record["httpStatus"], record["startError"]))

    print("\nSettle bekleniyor (butce %ds)..." % args.timeout)
    with ThreadPoolExecutor(max_workers=max(1, args.parallel)) as pool:
        records = list(pool.map(lambda r: settle_one(r, args.timeout), records))

    counts = {}
    for record in records:
        counts[record["status"]] = counts.get(record["status"], 0) + 1
    settles = sorted(r["settleS"] for r in records if r.get("settleS") is not None)

    print("\n" + "=" * 78)
    print("SONUC (%d instance)" % len(records))
    for status in sorted(counts):
        print("  %-14s %d" % (status, counts[status]))
    print("  dispatch yayilimi: %.1f ms | yanit penceresi: %.1f ms" % (dispatch_ms, window_ms))
    if settles:
        print("  settle min/median/max: %.2fs / %.2fs / %.2fs"
              % (settles[0], statistics.median(settles), settles[-1]))

    faulted = [r for r in records if r["status"] == "F"]
    for record in faulted:
        print("\n  FAULTED %s (%s)" % (record["id"], record["testId"]))
        print("    %s" % record["incident"])

    seen = sorted({marker for r in faulted for marker in SIGNATURE_MARKERS
                   if marker in (r["incident"] or "")})
    other = [r for r in faulted if not any(m in (r["incident"] or "") for m in SIGNATURE_MARKERS)]

    print("\nVERDICT:")
    completed = counts.get("C", 0)
    if completed == len(records):
        print("  TEMIZ — %d/%d instance `C`; reproduksiyon imzasi GORULMEDI." % (completed, len(records)))
    elif seen:
        print("  REPRODUKSIYON — %d fault, imza marker'lari: %s" % (len(faulted), ", ".join(seen)))
        if other:
            print("  DIKKAT — ayrica %d fault imzasiz; bunlar BASKA bir hata." % len(other))
    elif faulted:
        print("  BASKA HATA — %d fault ama HICBIRINDE imza marker'i yok "
              "(%s). Bu yarisin reproduksiyonu DEGIL." % (len(faulted), ", ".join(SIGNATURE_MARKERS)))
    else:
        print("  EKSIK — fault yok ama %d instance `C` degil (timeout/start-fail); olcum gecersiz."
              % (len(records) - completed))
    print("=" * 78)
    return 0 if completed == len(records) else 1


if __name__ == "__main__":
    sys.exit(main())
