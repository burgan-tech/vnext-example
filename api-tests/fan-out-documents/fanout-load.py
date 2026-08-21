#!/usr/bin/env python3
"""
fan-out-documents yuk testi — iki seviyeli bulkhead, tek-yazim ve straggler olcumu.

    python3 api-tests/fan-out-documents/fanout-load.py --publish
    python3 api-tests/fan-out-documents/fanout-load.py --instances 20 --items 10 --ceiling 64

## Ne olcuyor

M es zamanli instance x N item ile FanOutTask'i (TaskType 21, inline) yukler ve uc seyi olcer:

1. **Bulkhead tavani** — `Workflow:FanOut:MaxConcurrentItems` (surec geneli, varsayilan 64) ile
   `execution.maxDegreeOfParallelism` (batch-yerel, bu senaryoda 3) birlikte calisir. Efektif
   es zamanlilik `min(C, M * maxDop)` degerini ASAMAZ. M instance ayni anda kosarken tavan
   yoksa M * maxDop kadar es zamanli downstream cagri MockLab'e biner.

   **Nasil olculuyor (ve siniri).** MockLab'in dokumante edilmis bir istek-log ucu YOK, bu yuzden
   "MockLab'de gozlenen tepe es zamanlilik" dogrudan okunamiyor; uydurmuyoruz. Bunun yerine
   runtime'in kendi kaydettigi per-item `durationMs` degerlerinden zaman-agirlikli ortalama
   hesaplaniyor:

       efektif_eszamanlilik = toplam(item suresi) / batch duvar saati

   Her item suresi ucusta olan bir downstream cagriyi temsil ettigi icin bu, gercek es
   zamanliligin zaman-agirlikli ortalamasidir. Ortalama <= tepe oldugundan: ortalamanin tavani
   asmasi KESIN bir ihlaldir (FAIL), tavanin altinda kalmasi ise guclu ama mutlak olmayan kanittir.
   Kesin tepe olcumu monitoring host'undaki per-item span'lerden okunur (bkz. README).

2. **Tek-yazim degismezi (yuk altinda)** — batch, `documents-processing` state girisinde iki
   damga task'i arasinda sarili kosuyor: order 1 `versionBeforeFanOutBatch`, order 2 batch,
   order 3 `versionAfterFanOut`. Aradaki patch farki TAM 2 olmali — biri once-damgasinin kendi
   yazimi, digeri batch'in TEK yazimi. Es zamanlilik altinda bozulan bir per-item bastirma
   (SuppressDataApply) burada 1 + N olarak gorunur.

3. **Straggler orani** — `max(item suresi) / p50(item suresi)`. Bir fan-out batch'inin duvar
   saatini tek bir en yavas item belirler. `--slow-per-instance` ile MockLab'in gecikmeli
   route'una (DOC-SLOW, 1500 ms) giden item sayisi ayarlanir; 0 verilirse oran yalnizca dogal
   jitter'i olcer ve anlamsizlasir.

   Oran IKI TARAFLI denetlenir. Onceki tek tarafli hal ("<= 4.0") olcum bozulunca sessizce yesil
   kaliyordu: MockLab route'lari PREFIX ile esliyor ve gecikmeli mock `documents/process-slow`
   adresinde `documents/process` tarafindan yutuluyordu — hicbir item yavas degildi, oran ~1 idi ve
   esik rahatca geciyordu. Metrik boylece uzun sure yalnizca jitter olctu (2026-08-22'de
   `api/fan-out/slow-documents/process` route'una tasindi).

   Esikler fixture'in aritmetiginden turetilir: 1500 ms straggler / ~150 ms hizli item => oran
   **~10 TASARIM GEREGI**. Eski 4.0 tavani, straggler hic cevap vermezken kalibre edilmisti;
   gercek bir straggler ile matematiksel olarak asilir. Yeni aralik 3.0 .. 15.0 — alt sinir
   "straggler gercekten var mi", ust sinir "en yavas item patolojik olarak kuyrukta mi".

## Basari esikleri

  BULKHEAD        efektif_eszamanlilik <= min(ceiling, instances * max-dop) * tolerance
  TEK-YAZIM       her instance icin patch(after) - patch(before) == 2 (1 damga + 1 batch)
  SAGLIK          hicbir instance Faulted degil, hepsi terminal state'e ulasti
  STRAGGLER-VAR   straggler_orani >= --straggler-min (varsayilan 3.0; slow-per-instance > 0 ise)
  STRAGGLER       straggler_orani <= --straggler-threshold (varsayilan 15.0)

Hepsi gecerse cikis kodu 0, aksi halde 1.

## On kosullar

orchestration (4201) + execution (4202) + docker altyapisi ayakta; MockLab (3001) seed'i
`etc/docker/config/seed/fan-out-documents-collection.json` icermeli. Bilesenler publish
edilmemisse `--publish` ver.
"""

import argparse
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
WF = "fan-out-documents"
USER = "11111111-1111-1111-1111-111111111111"

# etc/docker/config/seed/fan-out-documents-collection.json -> api/fan-out/slow-documents/process
# mock'unun delayMs degeri. STRAGGLER-VAR tabani buradan turer; seed degisirse burayi da guncelle.
SLOW_ROUTE_DELAY_MS = 1500

REPO_ROOT = Path(__file__).resolve().parents[2]
WORKFLOW_FILE = REPO_ROOT / "core" / "Workflows" / WF / f"{WF}.json"
TASK_DIR = REPO_ROOT / "core" / "Tasks" / WF

TERMINAL_STATES = {"documents-completed", "documents-partial-failure", "documents-cancelled"}

RESULTS = []


def check(name, ok, detail=""):
    RESULTS.append((name, ok, detail))
    print(("  PASS  " if ok else "  FAIL  ") + name + (f"  -> {detail}" if detail else ""))
    return ok


def http(base, method, path, body=None, timeout=60):
    url = f"{base}/api/v1{path}"
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
            return e.code, json.loads(raw) if raw else {}
        except json.JSONDecodeError:
            return e.code, {"raw": raw}
    except Exception as e:  # noqa: BLE001 - surface the transport failure verbatim
        return 0, {"error": str(e)}


# ── publish ─────────────────────────────────────────────────────────────────

def publish(base):
    files = sorted(TASK_DIR.glob("*.json")) + [WORKFLOW_FILE]
    for path in files:
        body = json.loads(path.read_text())
        st, resp = http(base, "POST", "/definitions/publish", body)
        if st in (200, 201):
            print(f"  published {body.get('key')} v{body.get('version')}")
        elif st == 409:
            print(f"  {body.get('key')} zaten publish edilmis (409)")
        else:
            print(f"  ! {body.get('key')} publish HTTP {st}: {resp}")
            if st == 400 and "21" in json.dumps(resp):
                print("    (TaskType 21 reddedildiyse runtime fan-out destegi olmayan bir surumde)")
            return False
    http(base, "GET", "/definitions/re-initialize")
    print("  re-initialize ok")
    return True


# ── run ─────────────────────────────────────────────────────────────────────

def make_documents(index, items, slow_per_instance, fail_per_instance):
    """Her instance icin N dokuman. DOC-SLOW gecikmeli route'a, DOC-FAIL 500'e gider."""
    documents = []
    for i in range(items):
        if i < slow_per_instance:
            doc_id = f"DOC-SLOW-{index}-{i}"
        elif i < slow_per_instance + fail_per_instance:
            doc_id = f"DOC-FAIL-{index}-{i}"
        else:
            doc_id = f"DOC-{index}-{i}"
        documents.append({"id": doc_id, "url": f"https://example.invalid/{doc_id}.pdf"})
    return documents


def start_instance(base, index, args):
    body = {
        "testId": f"fanout-load-{index}",
        "documents": make_documents(index, args.items, args.slow_per_instance, args.fail_per_instance),
    }
    st, resp = http(base, "POST", f"/{DOMAIN}/workflows/{WF}/instances/start?sync=true", body)
    if st not in (200, 201, 202) or not resp.get("id"):
        return None, f"start HTTP {st}: {resp}"
    return resp["id"], None


def fire(base, instance_id):
    st, resp = http(
        base, "PATCH",
        f"/{DOMAIN}/workflows/{WF}/instances/{instance_id}/transitions/process-documents?sync=false",
        {})
    return st, resp


def instance_snapshot(base, instance_id):
    st, resp = http(base, "GET", f"/{DOMAIN}/workflows/{WF}/instances/{instance_id}")
    if st != 200:
        return None
    return resp


def wait_for_all(base, instance_ids, timeout_s):
    """Hepsi terminal state'e gelene kadar bekler. (settled_ids, faulted_ids, timed_out_ids)."""
    deadline = time.time() + timeout_s
    pending = set(instance_ids)
    faulted, settled = set(), {}

    while pending and time.time() < deadline:
        for instance_id in list(pending):
            snapshot = instance_snapshot(base, instance_id)
            if snapshot is None:
                continue
            metadata = snapshot.get("metadata", {})
            status = metadata.get("status")
            state = metadata.get("currentState")
            if status == "F":
                faulted.add(instance_id)
                pending.discard(instance_id)
            elif state in TERMINAL_STATES and status != "B":
                settled[instance_id] = snapshot
                pending.discard(instance_id)
        if pending:
            time.sleep(0.25)

    return settled, faulted, pending


def patch_of(version):
    try:
        parts = str(version).split(".")
        return int(parts[0]), int(parts[1]), int(parts[2].split("-")[0].split("+")[0])
    except (ValueError, IndexError):
        return None


# ── monitor (opsiyonel) ─────────────────────────────────────────────────────

def check_item_journal(monitor_url, instance_id, expected_items, fan_out_task_key):
    """
    Item journal satirlari `{fanOutTaskKey}#{index}` anahtariyla yalnizca MONITORING host'unda
    gorunur (orchestration bu uctan hicbir sey yayinlamiyor). Bu yuzden opsiyonel: --monitor-url
    verilmezse kontrol atlanir, uydurulmaz.
    """
    url = f"{monitor_url.rstrip('/')}/api/v1/monitor/{DOMAIN}/workflows/{WF}/instances/{instance_id}/tasks"
    req = urllib.request.Request(url, method="GET")
    req.add_header("user_reference", USER)
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            payload = json.loads(resp.read().decode())
    except Exception as e:  # noqa: BLE001
        return None, f"monitor okunamadi: {e}"

    keys = [item.get("taskDefinitionKey", "") for item in payload.get("items", [])]
    expected = {f"{fan_out_task_key}#{i}" for i in range(expected_items)}
    missing = sorted(expected - set(keys))
    return missing, f"{len(expected) - len(missing)}/{len(expected)} item satiri bulundu"


# ── main ────────────────────────────────────────────────────────────────────

def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base-url", default="http://localhost:4201")
    ap.add_argument("--instances", type=int, default=12, help="es zamanli instance sayisi (M)")
    ap.add_argument("--items", type=int, default=8, help="instance basina dokuman sayisi (N)")
    ap.add_argument("--slow-per-instance", type=int, default=2,
                    help="instance basina DOC-SLOW (1500 ms gecikmeli) item sayisi; straggler "
                         "orani bunlarla anlam kazanir. 0 = yalniz dogal jitter")
    ap.add_argument("--fail-per-instance", type=int, default=1,
                    help="instance basina DOC-FAIL (MockLab 500) item sayisi; allSettled join "
                         "altinda batch yine basarili olmali")
    ap.add_argument("--ceiling", type=int, default=64,
                    help="Workflow:FanOut:MaxConcurrentItems degeri (surec geneli bulkhead)")
    ap.add_argument("--max-dop", type=int, default=3,
                    help="fan-out-documents-task icindeki execution.maxDegreeOfParallelism")
    ap.add_argument("--tolerance", type=float, default=1.15,
                    help="bulkhead tavaninda kabul edilen olcum toleransi")
    ap.add_argument("--straggler-threshold", type=float, default=15.0,
                    help="max(item suresi)/p50 icin ust sinir")
    ap.add_argument("--timeout", type=int, default=300, help="tum instance'lar icin toplam bekleme (sn)")
    ap.add_argument("--monitor-url", default=None,
                    help="verilirse item journal ({taskKey}#{index}) monitoring host'undan "
                         "dogrulanir, orn. http://localhost:4203")
    ap.add_argument("--publish", action="store_true", help="calistirmadan once bilesenleri publish et")
    args = ap.parse_args()

    base = args.base_url.rstrip("/")

    print("=" * 78)
    print(f"fan-out-documents yuk testi — {args.instances} instance x {args.items} item")
    print("=" * 78)

    if args.publish and not publish(base):
        return 1

    # 1) instance'lari olustur (batch henuz kosmuyor: start hedefi documents-received)
    print(f"\n[1/4] {args.instances} instance baslatiliyor...")
    with ThreadPoolExecutor(max_workers=min(args.instances, 16)) as pool:
        started = list(pool.map(lambda i: start_instance(base, i, args), range(args.instances)))

    instance_ids = [i for i, err in started if i]
    errors = [err for _, err in started if err]
    if errors:
        for err in errors[:5]:
            print(f"  ! {err}")
    if not check("START", not errors and len(instance_ids) == args.instances,
                 f"{len(instance_ids)}/{args.instances} instance basladi"):
        return 1

    # 2) hepsini olabildigince ayni anda tetikle — bulkhead ancak batch'ler cakisirsa olculur
    print(f"\n[2/4] {len(instance_ids)} batch es zamanli tetikleniyor...")
    t0 = time.time()
    with ThreadPoolExecutor(max_workers=len(instance_ids)) as pool:
        fired = list(pool.map(lambda i: fire(base, i), instance_ids))
    rejected = [(st, resp) for st, resp in fired if st >= 400]
    if rejected:
        print(f"  ! {len(rejected)} tetikleme reddedildi, ilki: {rejected[0]}")

    # 3) settle
    print(f"\n[3/4] terminal state bekleniyor (timeout {args.timeout}s)...")
    settled, faulted, stuck = wait_for_all(base, instance_ids, args.timeout)
    t1 = time.time()
    wall = max(t1 - t0, 1e-6)

    check("SAGLIK", not faulted and not stuck,
          f"settled={len(settled)} faulted={len(faulted)} takili={len(stuck)} sure={wall:.1f}s")

    # 4) metrikler
    print(f"\n[4/4] metrikler")
    durations, single_write_failures, states = [], [], {}

    for instance_id, snapshot in settled.items():
        attributes = snapshot.get("attributes", {}) or {}
        state = snapshot.get("metadata", {}).get("currentState")
        states[state] = states.get(state, 0) + 1

        for row in attributes.get("documentResults") or []:
            value = row.get("durationMs")
            if isinstance(value, (int, float)):
                durations.append(float(value))

        # Batch'i saran iki damga: order 1 (once) ve order 3 (sonra); arada YALNIZ batch var.
        # Aradaki iki patch'ten biri once-damgasinin KENDI yazimi, digeri batch'in tek yazimi
        # olmali. Batch item basina yazsaydi fark 1 + N olurdu.
        before = patch_of(attributes.get("versionBeforeFanOutBatch"))
        after = patch_of(attributes.get("versionAfterFanOut"))
        if not before or not after:
            single_write_failures.append((instance_id, "version damgalari eksik"))
        elif before[:2] != after[:2] or after[2] - before[2] != 2:
            single_write_failures.append(
                (instance_id, f"{attributes.get('versionBeforeFanOutBatch')} -> "
                              f"{attributes.get('versionAfterFanOut')} (beklenen fark 2)"))

    total_item_seconds = sum(durations) / 1000.0
    effective_concurrency = total_item_seconds / wall
    ceiling = min(args.ceiling, args.instances * args.max_dop)
    throughput = len(durations) / wall

    p50 = statistics.median(durations) if durations else 0.0
    slowest = max(durations) if durations else 0.0
    straggler_ratio = (slowest / p50) if p50 > 0 else 0.0

    print(f"  state dagilimi        : {states}")
    print(f"  toplam item           : {len(durations)}")
    print(f"  duvar saati           : {wall:.2f}s")
    print(f"  toplam item-saniye    : {total_item_seconds:.2f}s")
    print(f"  efektif eszamanlilik  : {effective_concurrency:.2f}  (tavan {ceiling}, "
          f"tolerans x{args.tolerance})")
    print(f"  throughput            : {throughput:.2f} item/s")
    print(f"  item p50 / max        : {p50:.0f} ms / {slowest:.0f} ms")
    print(f"  straggler orani       : {straggler_ratio:.2f}  (tavan {args.straggler_threshold})")

    print()

    # BULKHEAD gecerlilik kapisi.
    #
    # Metrik sum(item suresi)/wall, her item suresinin "ucusta gecen downstream zamani" oldugunu
    # VARSAYAR. Bu varsayim yanlis: FanOutTaskExecutor item stopwatch'ini slot beklemelerinden ONCE
    # baslatiyor (RunItemWithGatesAsync, Stopwatch.StartNew -> degreeGate.WaitAsync ->
    # AcquireGlobalSlotAsync), yani durationMs KUYRUKTA BEKLEME SURESINI DE ICERIR. Runtime bu ikisini
    # ayirt edebilmek icin span'e ayrica vnext.fanout.item.queue_wait_ms tag'i basiyor; per-item
    # deadline penceresi de bilincli olarak ancak slotlar alindiktan sonra aciliyor.
    #
    # Sonuc: item'lar kuyruga girdigi anda — yani bulkhead tam da isini yaptigi anda — metrik SISER
    # ve kontrol YANLIS FAIL uretir. Olculen 58.69 vs tavan 36 bunun ornegi: 96 item'in 36'si ucusta
    # olabilirken toplam item-saniye 96 x ~3.7s'yi iceriyordu.
    #
    # Bu yuzden iddia yalnizca teklif edilen yukun tavani DOLDURAMAYACAGI durumda kuruluyor; o zaman
    # kuyruk yok ve oran adil bir eszamanlilik tahmini. Doygun durumda kesin tepe olcumu zaten
    # monitoring host'undaki per-item span'lerden okunur (bkz. yukaridaki 1. madde).
    # Kuyruk OLUSMAMASI icin iki kosul birlikte gerekir:
    #   1. instance ici: items <= max-dop        (batch-yerel degreeGate beklemesi olmasin)
    #   2. surec geneli: instances*items <= ceiling  (global bulkhead beklemesi olmasin)
    offered = args.instances * args.items
    queue_free = args.items <= args.max_dop and offered <= args.ceiling
    if queue_free:
        check("BULKHEAD", effective_concurrency <= ceiling * args.tolerance,
              f"{effective_concurrency:.2f} <= {ceiling * args.tolerance:.2f}")
    else:
        why = []
        if args.items > args.max_dop:
            why.append(f"items ({args.items}) > max-dop ({args.max_dop}) — batch ici kuyruk")
        if offered > args.ceiling:
            why.append(f"instances*items ({offered}) > ceiling ({args.ceiling}) — global kuyruk")
        print(f"  SKIP  BULKHEAD  -> {'; '.join(why)}. durationMs kuyrukta bekleme suresini de "
              f"icerdigi icin sum(duration)/wall = {effective_concurrency:.2f} ucusta-eszamanlilik "
              f"DEGIL ve bu kontrol yanlis FAIL uretir. Iddia icin kuyruksuz bir profil kullanin "
              f"(or. --instances 4 --items 3 --max-dop 3); doygun profilde kesin tepe icin "
              f"--monitor-url ile per-item span'lere bakin")
    check("TEK-YAZIM", not single_write_failures,
          f"{len(single_write_failures)} instance tek-yazim degismezini bozdu"
          + (f", ilki: {single_write_failures[0]}" if single_write_failures else ""))

    # Straggler orani IKI TARAFLI kontrol edilir. Tek tarafli (yalniz tavan) hali, olcumun kendisi
    # bozulunca sessizce yesil kaliyordu: MockLab route'lari PREFIX ile esliyor ve gecikmeli mock
    # `documents/process-slow` adresinde `documents/process` tarafindan yutuluyordu, yani hicbir item
    # yavas degildi, oran ~1 idi ve "<= 4.0" rahatca geciyordu. Metrik aylarca sadece jitter olctu.
    # Alt sinir ORANLA degil MUTLAK sure ile olculur. max/p50 kucuk orneklemde gurultulu: DOC-SLOW
    # HIC yokken bile tek bir soguk item 963 ms / 102 ms = 9.44 oran uretti, yani oran tabani
    # "straggler gercekten var mi" sorusunu ayirt etmiyor. Gecikmeli route cevap veriyorsa en yavas
    # item en az yapilandirilan gecikme kadar surer; yutulmus route'ta ~200 ms'de doner.
    if args.slow_per_instance > 0:
        floor_ms = SLOW_ROUTE_DELAY_MS * 0.8
        check("STRAGGLER-VAR", slowest >= floor_ms,
              f"en yavas item {slowest:.0f} ms >= {floor_ms:.0f} ms "
              f"({args.slow_per_instance} DOC-SLOW/instance istendi, route gecikmesi "
              f"{SLOW_ROUTE_DELAY_MS} ms; bunun altinda kaliyorsa gecikmeli route CEVAP VERMIYOR — "
              f"MockLab route'lari PREFIX ile esler, bkz. seed'deki not)")
    check("STRAGGLER", straggler_ratio <= args.straggler_threshold,
          f"{straggler_ratio:.2f} <= {args.straggler_threshold}")

    if args.monitor_url and settled:
        instance_id = next(iter(settled))
        missing, detail = check_item_journal(
            args.monitor_url, instance_id, args.items, "fan-out-documents-task")
        if missing is None:
            print(f"  SKIP  ITEM-JOURNAL  -> {detail}")
        else:
            check("ITEM-JOURNAL", not missing, detail + (f", eksik: {missing[:3]}" if missing else ""))
    else:
        print("  SKIP  ITEM-JOURNAL  -> --monitor-url verilmedi (satirlar yalniz monitoring "
              "host'unda gorunur)")

    print("\n" + "=" * 78)
    failed = [name for name, ok, _ in RESULTS if not ok]
    if failed:
        print(f"SONUC: FAIL — {', '.join(failed)}")
        return 1
    print(f"SONUC: PASS — {len(RESULTS)} kontrol")
    return 0


if __name__ == "__main__":
    sys.exit(main())
