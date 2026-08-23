#!/usr/bin/env python3
"""
script-perf-lab yuk surucusu — soguk/sicak faz makro baseline + Katman 0 metrics snapshot.

    python3 api-tests/script-perf-lab/perf-load.py --publish --parallel 20 --iterations 3 \\
        --payload-kb 4 --fanout-count 25

## Ne olcuyor

1. **Soguk faz** (`--skip-cold` verilmedikce) — TEK instance baslatilir ve settle edilir.
   `coldLatencyS` script compiler'in ilk-dokunus (derleme + JIT) maliyetini tasir. Bu olcum
   YALNIZ bilesenler taze bir nonce ile uretildiginde (`build-script-perf-lab.py --nonce N`)
   anlamlidir — ayni nonce'la ikinci calistirma script cache'ine carpar ve sicak-faz sayisina
   yakin bir sure doner.

2. **Sicak faz** — `--iterations` tur x `--parallel` es zamanli instance. Her turda N instance
   ayni anda baslatilir (ThreadPoolExecutor), settle suresi (saniye) toplanir. Tum turlardan
   biriken latency listesinden p50/p95/p99 (`statistics.quantiles`, kucuk orneklemde sirali
   liste indekslemesine duser).

3. **Metrics snapshot** — sicak fazdan ONCE ve SONRA hem orchestration (`{base}/metrics`) hem
   execution (`:4202/metrics`) ucundan `script_` ile baslayan satirlar cekilir,
   `results/metrics-{before|after}-{timestamp}.txt`'ye yazilir. Stdout'a delta ozeti basilir:
   `script_compilations_total{result}` hit/miss toplamlari ve
   `script_execution_duration_seconds_count{script_type}` kirilimi (iki ucun toplami).

Akis her stage'de bir ScriptTask ile instance data'yi `chunkKb` boyutunda buyutur (10 stage,
B9 append profili) ve `perf-fanout`'ta `--fanout-count` item'i HTTP child olarak fan-out eder
(FanOutTask type 21, B6 branch klonu). Bkz. `core/Workflows/script-perf-lab/README.md`.

## Basari esikleri

  herhangi bir instance `F` (Faulted)         -> FAIL
  TIMEOUT orani > %5                          -> FAIL
  instance hic baslamadi (START-FAIL)         -> FAIL (pratik ek guvence; spesifikasyonun
                                                  F/TIMEOUT esiklerine ek, cunku pipeline'a hic
                                                  girmemis bir istek F/TIMEOUT olarak sayilmaz)

Hepsi gecerse cikis kodu 0, aksi halde 1.

## On kosullar

orchestration (4201) + execution (4202) + docker altyapisi ayakta; bilesenler yayinlanmis
degilse `--publish` ver (kardes `publish.py`'yi cagirir).
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

DOMAIN = "core"
WORKFLOW = "script-perf-lab"
TERMINAL = {"C", "F", "P"}
POLL_INTERVAL_S = 0.5

# main() basinda --base-url'den turetilir; sabit degil (race-load.py'nin aksine, bu senaryo
# orchestration disinda execution /metrics ucuna da BASE'den bagimsiz erisir).
BASE = None


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


def http_text(url, timeout=30):
    """`/metrics` gibi duz-metin (Prometheus exposition) uclar icin — http()'in json.loads'i buraya uymaz."""
    request = urllib.request.Request(url, method="GET")
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, response.read().decode()
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode()
    except Exception as error:  # noqa: BLE001
        return -1, str(error)


def publish():
    """Kardes publish.py'yi oldugu gibi calistirir; bilesen listesi orada tek yerde durur."""
    path = Path(__file__).resolve().parent / "publish.py"
    spec = importlib.util.spec_from_file_location("script_perf_lab_publish", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.main() == 0


def start_one(args, label):
    body = {
        "testId": "perf-%s-%s" % (label, uuid.uuid4().hex[:8]),
        "chunkKb": args.payload_kb,
        "fanoutItems": [{"id": "DOC-%03d" % i} for i in range(args.fanout_count)],
    }
    dispatched_at = time.time()
    status, response = http(
        "POST", "%s/%s/workflows/%s/instances/start?sync=false" % (BASE, DOMAIN, WORKFLOW), body)
    return {"label": label, "testId": body["testId"], "dispatchedAt": dispatched_at,
            "startedAt": time.time(), "httpStatus": status, "id": response.get("id"),
            "startError": None if status in (200, 202) else json.dumps(response)[:300]}


def incident_text(instance_id):
    """Detay yanitindaki `metadata.incident`ten fault metnini toplar."""
    status, detail = http("GET", "%s/%s/workflows/%s/instances/%s" % (BASE, DOMAIN, WORKFLOW, instance_id))
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
    """Terminale (veya timeout'a) kadar poll eder; kayda status/settle/incident yazar."""
    if not record["id"]:
        record["status"] = "START-FAIL"
        return record
    url = "%s/%s/workflows/%s/instances/%s/functions/state" % (BASE, DOMAIN, WORKFLOW, record["id"])
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


def run_cold(args):
    print("\n[SOGUK FAZ]")
    print("  Not: soguk faz ancak taze nonce'la anlamli — bkz. uretici --nonce "
          "(core/Workflows/script-perf-lab/build-script-perf-lab.py)")
    record = start_one(args, "cold")
    record = settle_one(record, args.timeout)
    print("  coldLatencyS: %.2f (status=%s, id=%s)"
          % (record.get("settleS", 0.0), record.get("status"), record.get("id")))
    if record["status"] == "F":
        print("  ! FAULTED: %s" % record.get("incident"))
    return record


def run_warm(args):
    print("\n[SICAK FAZ] %d tur x %d paralel" % (args.iterations, args.parallel))
    all_records = []
    all_latencies = []
    for round_idx in range(1, args.iterations + 1):
        labels = ["w%d-%d" % (round_idx, i) for i in range(args.parallel)]
        with ThreadPoolExecutor(max_workers=args.parallel) as pool:
            records = list(pool.map(lambda label: start_one(args, label), labels))
        with ThreadPoolExecutor(max_workers=max(1, args.parallel)) as pool:
            records = list(pool.map(lambda r: settle_one(r, args.timeout), records))

        counts = {}
        for record in records:
            counts[record["status"]] = counts.get(record["status"], 0) + 1
        round_latencies = [r["settleS"] for r in records if r.get("settleS") is not None]
        all_latencies.extend(round_latencies)
        all_records.extend(records)
        print("  tur %d: %s" % (round_idx, counts))

    return all_records, all_latencies


def percentiles(values):
    """p50/p95/p99. Kucuk orneklemde `statistics.quantiles` guvenilmez (StatisticsError riski
    ve az veri noktasinda anlamsiz interpolasyon) — 20'nin altinda sirali liste indekslemesine
    duser."""
    if not values:
        return {}
    values_sorted = sorted(values)
    if len(values_sorted) >= 20:
        cuts = statistics.quantiles(values_sorted, n=100, method="inclusive")
        return {"p50": cuts[49], "p95": cuts[94], "p99": cuts[98]}
    result = {}
    for label, pct in (("p50", 0.50), ("p95", 0.95), ("p99", 0.99)):
        idx = min(len(values_sorted) - 1, int(round(pct * (len(values_sorted) - 1))))
        result[label] = values_sorted[idx]
    return result


def evaluate(records):
    counts = {}
    for record in records:
        status = record["status"]
        key = "TIMEOUT" if status.startswith("TIMEOUT") else status
        counts[key] = counts.get(key, 0) + 1
    total = len(records)
    faulted = counts.get("F", 0)
    start_failed = counts.get("START-FAIL", 0)
    timeout = counts.get("TIMEOUT", 0)
    timeout_ratio = (timeout / total) if total else 0.0
    ok = faulted == 0 and start_failed == 0 and timeout_ratio <= 0.05
    return counts, faulted, start_failed, timeout_ratio, ok


def parse_metric_line(line):
    """`name{label="value",...} 123` veya `name 123` satirini (name, labels, value) olarak coker."""
    if "{" in line:
        name, rest = line.split("{", 1)
        try:
            labels_part, value_part = rest.rsplit("}", 1)
        except ValueError:
            return None
        labels = {}
        for kv in labels_part.split(","):
            if not kv or "=" not in kv:
                continue
            key, value = kv.split("=", 1)
            labels[key.strip()] = value.strip().strip('"')
        value_str = value_part.strip()
    else:
        parts = line.rsplit(" ", 1)
        if len(parts) != 2:
            return None
        name, value_str = parts[0], parts[1]
        labels = {}
    try:
        value = float(value_str)
    except ValueError:
        return None
    return name.strip(), labels, value


def snapshot_metrics(args, phase, results_dir):
    """`{base}/metrics` (orchestration) + `:4202/metrics` (execution) ucundan `script_`
    satirlarini cekip results/metrics-{phase}-{timestamp}.txt'ye yazar; kaynak basina
    ham satir listesini dondurur (delta hesaplamasi icin)."""
    orch_base = args.base_url.rstrip("/")
    sources = {"orchestration": "%s/metrics" % orch_base}
    if ":4201" in orch_base:
        sources["execution"] = "%s/metrics" % orch_base.replace(":4201", ":4202")
    else:
        # Ozel portta execution ucunu tahmin etmeyiz: ayni ucu iki kez sayip delta'yi sessizce
        # ikiye katlamaktansa yalniz orchestration'i olceriz.
        print("  ! --base-url :4201 icermiyor; execution /metrics atlandi (delta yalniz orchestration)")

    captured = {}
    blocks = []
    for name, url in sources.items():
        status, text = http_text(url)
        if status != 200:
            print("  ! %s metrics HTTP %s" % (name, status))
            captured[name] = []
            blocks.append("# %s (HTTP %s)" % (name, status))
            continue
        lines = [line for line in text.splitlines() if line.startswith("script_")]
        captured[name] = lines
        blocks.append("# %s\n%s" % (name, "\n".join(lines)))

    results_dir.mkdir(parents=True, exist_ok=True)
    ts = time.strftime("%Y%m%dT%H%M%SZ", time.gmtime())
    out_path = results_dir / ("metrics-%s-%s.txt" % (phase, ts))
    out_path.write_text("\n\n".join(blocks) + "\n")
    print("  metrics snapshot yazildi: %s" % out_path)
    return captured


def aggregate(captured, metric_name, label_key):
    """metric_name'e ait satirlari label_key kirilimina gore, TUM kaynaklar toplanarak topler."""
    totals = {}
    for lines in captured.values():
        for line in lines:
            parsed = parse_metric_line(line)
            if not parsed:
                continue
            name, labels, value = parsed
            if name != metric_name:
                continue
            key = labels.get(label_key, "(none)")
            totals[key] = totals.get(key, 0.0) + value
    return totals


def print_metrics_delta(before, after):
    print("\n[METRICS DELTA] (orchestration + execution toplami)")
    comp_before = aggregate(before, "script_compilations_total", "result")
    comp_after = aggregate(after, "script_compilations_total", "result")
    for key in sorted(set(comp_before) | set(comp_after)):
        b, a = comp_before.get(key, 0.0), comp_after.get(key, 0.0)
        print("  script_compilations_total{result=%s}: %d -> %d (delta %+d)" % (key, b, a, a - b))

    dur_before = aggregate(before, "script_execution_duration_seconds_count", "script_type")
    dur_after = aggregate(after, "script_execution_duration_seconds_count", "script_type")
    for key in sorted(set(dur_before) | set(dur_after)):
        b, a = dur_before.get(key, 0.0), dur_after.get(key, 0.0)
        print("  script_execution_duration_seconds_count{script_type=%s}: %d -> %d (delta %+d)"
              % (key, b, a, a - b))


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--base-url", default="http://localhost:4201",
                        help="orchestration base URL (execution /metrics :4201->:4202 ile turetilir)")
    parser.add_argument("--parallel", type=int, default=20, help="sicak fazda tur basina paralel instance")
    parser.add_argument("--iterations", type=int, default=3, help="sicak faz tur sayisi")
    parser.add_argument("--payload-kb", type=int, default=4, help="start body chunkKb (stage basi buyume)")
    parser.add_argument("--fanout-count", type=int, default=25, help="perf-fanout item sayisi")
    parser.add_argument("--timeout", type=int, default=300, help="instance basina settle butcesi (s)")
    parser.add_argument("--publish", action="store_true", help="olcumden once bilesenleri publish et")
    parser.add_argument("--skip-cold", action="store_true", help="soguk fazi atla, dogrudan sicak faza gec")
    args = parser.parse_args()

    global BASE
    BASE = "%s/api/v1" % args.base_url.rstrip("/")

    if args.publish:
        print("Publish:")
        if not publish():
            return 1

    if not args.skip_cold:
        run_cold(args)
    else:
        print("\n[SOGUK FAZ] --skip-cold ile atlandi")

    results_dir = Path(__file__).resolve().parent / "results"
    before_metrics = snapshot_metrics(args, "before", results_dir)

    records, latencies = run_warm(args)

    after_metrics = snapshot_metrics(args, "after", results_dir)

    counts, faulted, start_failed, timeout_ratio, ok = evaluate(records)
    pct = percentiles(latencies)

    print("\n" + "=" * 78)
    print("SONUC (%d instance, %d tur x %d paralel)" % (len(records), args.iterations, args.parallel))
    for status in sorted(counts):
        print("  %-12s %d" % (status, counts[status]))
    if pct:
        print("  latency p50/p95/p99: %.2fs / %.2fs / %.2fs"
              % (pct.get("p50", 0.0), pct.get("p95", 0.0), pct.get("p99", 0.0)))
    print("  timeout orani: %.1f%%" % (timeout_ratio * 100))

    print_metrics_delta(before_metrics, after_metrics)

    faulted_records = [r for r in records if r["status"] == "F"]
    for record in faulted_records[:10]:
        print("\n  FAULTED %s (%s)" % (record["id"], record["testId"]))
        print("    %s" % record["incident"])

    print("\nVERDICT:")
    if ok:
        print("  PASS — 0 Faulted, 0 START-FAIL, TIMEOUT orani %.1f%% <= %%5" % (timeout_ratio * 100))
    else:
        reasons = []
        if faulted:
            reasons.append("%d Faulted" % faulted)
        if start_failed:
            reasons.append("%d START-FAIL" % start_failed)
        if timeout_ratio > 0.05:
            reasons.append("TIMEOUT orani %.1f%% > %%5" % (timeout_ratio * 100))
        print("  FAIL — %s" % ", ".join(reasons))
    print("=" * 78)

    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
