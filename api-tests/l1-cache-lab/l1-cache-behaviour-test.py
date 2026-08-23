#!/usr/bin/env python3
"""
L1 component cache davranis testi — publish sonrasi dogru versiyon cozumlemesi.

    python3 api-tests/l1-cache-lab/l1-cache-behaviour-test.py

## Dogruladigi davranis

Runtime'daki generation-anahtarli L1 (in-process) component cache'inin, publish
gorunurlugunu BOZMADIGINI ucdan uca olcer:

  1. task/view/flow v1.0.0 publish edilir; instance A baslatilir.
     - A'nin flowVersion'i 1.0.0 olmali (latest cozumu).
     - `probe` transition'i kosturulur; HTTP task'in DB'ye persist edilen Request'i
       task v1.0.0'in config body'sini tasimali (flow, task'i "1" MAJOR range ile
       referanslar -> en iyi eslesme 1.0.0).
     - view function marker'i L1-LAB-VIEW-1.0.0 olmali. Ayni okuma birkac kez
       tekrarlanir ki L1 SICAK olsun — asil test bayat L1'i yakalamaktir.
  2. task/view/flow v1.1.0 publish edilir (re-initialize YOK — CD sozlesmesi publish
     tek basina yeterli olmali).
  3. HEMEN ardindan:
     - Yeni instance B -> flowVersion 1.1.0 olmali (latest L1'den bayat donmemeli).
     - A hala flowVersion 1.0.0 olmali (pinned; instance surumu publish ile OYNAMAZ).
     - A'nin view'i artik L1-LAB-VIEW-1.1.0 olmali ("1" range yeni versiyona cozulur).
     - A'da `back` + `probe` tekrar kosturulur; yeni task Request'i taskVersion 1.1.0
       tasimali ("1" range yeni task'a cozulur).

L1 bayat servis ederse 3. adimdaki asertler kirilir; L1 dogru calisiyorsa hepsi geser.

On kosullar: orchestration (4201) + execution (4202) + docker altyapisi (mocklab dahil) ayakta.
Component dosyalari: ./components/*.{surum}.json — her surum ayri dosyadir.
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
WF = "l1-cache-lab"
USER = "11111111-1111-1111-1111-111111111111"
COMPONENT_DIR = Path(__file__).resolve().parent / "components"
PG = ["docker", "exec", "vnext-postgres", "psql", "-U", "postgres",
      "-d", "Aether_WorkflowDb", "-tA", "-c"]

RESULTS = []


def check(name, ok, detail=""):
    RESULTS.append((name, ok, detail))
    print(("  PASS  " if ok else "  FAIL  ") + name + (f"  -> {detail}" if detail and not ok else ""))
    return ok


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


def load_component(name, file_version, effective_version):
    """Dosyadaki kanonik surumu (1.0.0/1.1.0) calisma surumune cevirir.

    Dosyalar kanonik ornek olarak sabit kalir; --minor ile ayni runtime'a tekrar
    kosulabilsin diye surum ve surum-marker'lari calisma aninda turetilir.
    flowVersion ("1.0.0" sabiti) BILEREK degistirilmez — o, component semasinin surumudur.
    """
    d = json.loads((COMPONENT_DIR / name).read_text())
    d["version"] = effective_version
    attrs = d["attributes"]
    if "config" in attrs and isinstance(attrs["config"].get("body"), dict):
        attrs["config"]["body"]["taskVersion"] = effective_version
    if "content" in attrs:
        text = json.dumps(attrs["content"])
        attrs["content"] = json.loads(text.replace(f"L1-LAB-VIEW-{file_version}",
                                                   f"L1-LAB-VIEW-{effective_version}"))
    if "labels" in attrs:
        for lbl in attrs["labels"]:
            lbl["label"] = lbl["label"].replace(file_version, effective_version)
    return d


def publish(file_version, effective_version):
    """Leaf-first publish: task -> view -> flow. re-initialize BILEREK cagirilmiyor."""
    for name in (f"l1-lab-task.{file_version}.json",
                 f"l1-lab-view.{file_version}.json",
                 f"l1-cache-lab.{file_version}.json"):
        body = load_component(name, file_version, effective_version)
        st, resp = http("POST", f"{BASE}/definitions/publish", body)
        if st in (200, 201):
            print(f"  published {name} as {effective_version}")
        elif st == 409:
            print(f"  {name} ({effective_version}) zaten publish edilmis (409)")
        else:
            print(f"  ! {name} publish HTTP {st}: {resp}")
            return False
    return True


def start_instance(tag):
    st, body = http("POST", f"{BASE}/{DOMAIN}/workflows/{WF}/instances/start",
                    {"testId": f"l1lab-{tag}-{uuid.uuid4().hex[:8]}"})
    if st not in (200, 201, 202):
        print(f"  ! start HTTP {st}: {body}")
        return None
    return body.get("id") or body.get("Id")


def instance_meta(instance_id):
    st, body = http("GET", f"{BASE}/{DOMAIN}/workflows/{WF}/instances/{instance_id}")
    if st != 200:
        return {}
    return body


def state_of(instance_id):
    st, body = http("GET", f"{BASE}/{DOMAIN}/workflows/{WF}/instances/{instance_id}/functions/state")
    if st not in (200, 304):
        return None, None
    return body.get("state"), body.get("status")


def wait_state(instance_id, want, timeout_s=30):
    deadline = time.time() + timeout_s
    last = None
    while time.time() < deadline:
        s, st = state_of(instance_id)
        last = (s, st)
        if s == want and st == "A":
            return True, last
        time.sleep(0.25)
    return False, last


def view_marker(instance_id, candidates):
    """View function cevabinin icinden L1-LAB-VIEW-x marker'ini cikarir."""
    st, body = http("GET", f"{BASE}/{DOMAIN}/workflows/{WF}/instances/{instance_id}/functions/view")
    raw = json.dumps(body)
    for version in candidates:
        if f"L1-LAB-VIEW-{version}" in raw:
            return f"L1-LAB-VIEW-{version}", st
    return f"<marker yok; HTTP {st}: {raw[:200]}>", st


def trigger(instance_id, key):
    st, body = http("PATCH",
                    f"{BASE}/{DOMAIN}/workflows/{WF}/instances/{instance_id}/transitions/{key}?sync=true",
                    {})
    return st, body


def last_task_request(instance_id):
    """Son kosulan task'in persist edilen Request'i — hangi task SURUMUNUN kostugunun kaniti."""
    return psql(
        'select it."Request"::text from l1_cache_lab."InstanceTasks" it '
        'join l1_cache_lab."InstanceTransitions" tr on it."TransitionId"=tr."Id" '
        f'where tr."InstanceId"=\'{instance_id}\' '
        'order by it."CreatedAt" desc limit 1')


def task_version_of(request_text, candidates):
    for version in candidates:
        if f'"taskVersion": "{version}"' in request_text or f'"taskVersion":"{version}"' in request_text:
            return version
    return f"<bilinmiyor: {request_text[:160]}>"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--minor", type=int, default=0,
                    help="calisma surumleri 1.{2N}.0 / 1.{2N+1}.0 olur; ayni runtime'a "
                         "tekrar kosum icin N'i artir (latest asserti ancak taze surumle anlamli)")
    args = ap.parse_args()

    v_old = f"1.{2 * args.minor}.0"
    v_new = f"1.{2 * args.minor + 1}.0"
    both = (v_new, v_old)
    print(f"calisma surumleri: eski={v_old} yeni={v_new}")

    print(f"== Faz 1: v{v_old} publish")
    if not publish("1.0.0", v_old):
        sys.exit(1)

    print("== Faz 2: instance A (surumsuz start -> latest)")
    a = start_instance("a")
    if not a:
        sys.exit(1)
    ok, last = wait_state(a, "ready")
    if not check("A ready state'ine ulasti", ok, str(last)):
        sys.exit(1)

    meta = instance_meta(a)
    check(f"A.flowVersion == {v_old} (latest cozumu)",
          meta.get("flowVersion") == v_old, str(meta.get("flowVersion")))

    # L1'i isit: ayni view'i uc kez oku — asil test SICAK L1'i yakalamaktir.
    for _ in range(3):
        marker, _ = view_marker(a, both)
    check(f"A view marker == v{v_old}", marker == f"L1-LAB-VIEW-{v_old}", marker)

    st, _ = trigger(a, "probe")
    check("A probe transition kabul edildi", st in (200, 201, 202), f"HTTP {st}")
    ok, last = wait_state(a, "probed")
    check("A probed state'ine ulasti", ok, str(last))
    req = last_task_request(a)
    check(f"A probe task surumu == {v_old}",
          task_version_of(req, both) == v_old, task_version_of(req, both))

    # probed'in view'ini de isit
    for _ in range(3):
        marker, _ = view_marker(a, both)
    check(f"A (probed) view marker == v{v_old}", marker == f"L1-LAB-VIEW-{v_old}", marker)

    print(f"== Faz 3: v{v_new} publish (re-initialize YOK)")
    if not publish("1.1.0", v_new):
        sys.exit(1)

    print("== Faz 4: publish'in HEMEN ardindan gorunurluk")
    b = start_instance("b")
    if not b:
        sys.exit(1)
    ok, last = wait_state(b, "ready")
    check("B ready state'ine ulasti", ok, str(last))
    meta_b = instance_meta(b)
    check(f"B.flowVersion == {v_new} (latest, L1'den bayat DEGIL)",
          meta_b.get("flowVersion") == v_new, str(meta_b.get("flowVersion")))

    meta_a = instance_meta(a)
    check(f"A.flowVersion hala {v_old} (pinned; publish oynatmaz)",
          meta_a.get("flowVersion") == v_old, str(meta_a.get("flowVersion")))

    marker, _ = view_marker(a, both)
    check(f"A view marker artik v{v_new} ('1' range yeni surume cozulur)",
          marker == f"L1-LAB-VIEW-{v_new}", marker)

    st, _ = trigger(a, "back")
    check("A back kabul edildi", st in (200, 201, 202), f"HTTP {st}")
    ok, _ = wait_state(a, "ready")
    check("A ready'e dondu", ok)
    st, _ = trigger(a, "probe")
    check("A probe (2.) kabul edildi", st in (200, 201, 202), f"HTTP {st}")
    ok, _ = wait_state(a, "probed")
    check("A tekrar probed'a ulasti", ok)
    req = last_task_request(a)
    check(f"A probe task surumu artik {v_new} ('1' range yeni task'a cozulur)",
          task_version_of(req, both) == v_new, task_version_of(req, both))

    st, _ = trigger(b, "probe")
    ok, _ = wait_state(b, "probed")
    req_b = last_task_request(b)
    check(f"B probe task surumu == {v_new}",
          task_version_of(req_b, both) == v_new, task_version_of(req_b, both))
    marker_b, _ = view_marker(b, both)
    check(f"B view marker == v{v_new}", marker_b == f"L1-LAB-VIEW-{v_new}", marker_b)

    print("\n== SONUC")
    failed = [r for r in RESULTS if not r[1]]
    print(f"  {len(RESULTS) - len(failed)}/{len(RESULTS)} PASS")
    if failed:
        print("  Kirik assertler:")
        for name, _, detail in failed:
            print(f"    - {name}: {detail}")
        sys.exit(1)
    print("  L1 publish gorunurlugunu bozmuyor; dogru versiyonlar cozuluyor.")


if __name__ == "__main__":
    main()
