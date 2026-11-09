#!/usr/bin/env python3
"""
subflow-data-source-lab — the client's data source while a SubFlow is active (Amorphie 474774 / vnext#1084 / WMP 482207).

Drives `sds-parent` → `sds-child` against a local orchestrator and checks, step by step, what a
polling client sees. The same five steps as `Tests/SubflowDataSourceLab` (C#); this script exists so
the scenario can run without a .NET SDK and so the wire responses can be RECORDED as fixtures for the
client SDK (vnext-client-workflow-manager).

Steps
  T1  parent state function descends to the child; activeCorrelations[0].href is the CHILD's data function
  T2  data.href answers the PARENT's data, the correlation href answers the CHILD's data
  T3  `touch` ($self on the child) changes only the child's data; the parent's data answers 304 to its
      old ETag; the parent STATE ETag's behaviour is recorded (not asserted — undocumented)
  T4  `next` (child state advance) moves the parent state ETag
  T5  `finish` on the child resumes the parent with the child's output merged

Usage
  python3 api-tests/subflow-data-source-lab/probe.py [--base-url http://localhost:4201] [--publish]
                                                     [--record <dir>]
  --publish   publish the lab's task, views and flows first (version-immutable; 409 100002 = already there)
  --record    write every observed response (status, ETag, body) under <dir> as JSON fixtures
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

DEFAULT_BASE_URL = os.environ.get("VNEXT_BASE_URL", "http://localhost:4201").rstrip("/")
BASE = DEFAULT_BASE_URL + "/api/v1"
DOMAIN = "core"
PARENT = "sds-parent"
CHILD = "sds-child"
USER = "11111111-1111-1111-1111-111111111111"
REPO = Path(__file__).resolve().parents[2]
RECORD_DIR: Path | None = None
FAILURES: list[str] = []


def headers():
    return {
        "Content-Type": "application/json",
        "user_reference": USER,
        "x-request-id": str(uuid.uuid4()),
        "x-device-id": "integration-test-device",
        "x-token-id": "integration-test-token",
    }


def http(method, url, body=None, extra=None, timeout=30):
    """-> (status, response headers as dict, parsed body or {})"""
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    for k, v in {**headers(), **(extra or {})}.items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode()
            return resp.status, dict(resp.headers), (json.loads(raw) if raw else {})
    except urllib.error.HTTPError as e:
        raw = e.read().decode()
        try:
            parsed = json.loads(raw) if raw else {}
        except json.JSONDecodeError:
            parsed = {"raw": raw}
        return e.code, dict(e.headers), parsed
    except Exception as e:  # connection errors
        return -1, {}, {"error": str(e)}


def etag_of(hdrs):
    for k, v in hdrs.items():
        if k.lower() == "etag":
            return v
    return None


def record(name, status, hdrs, body):
    if RECORD_DIR is None:
        return
    RECORD_DIR.mkdir(parents=True, exist_ok=True)
    (RECORD_DIR / f"{name}.json").write_text(
        json.dumps({"status": status, "etag": etag_of(hdrs), "body": body}, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8")


def check(cond, what):
    print(("  ✔ " if cond else "  ✘ ") + what)
    if not cond:
        FAILURES.append(what)
    return cond


def note(what):
    print("  · " + what)


def state_fn(instance_id, if_none_match=None):
    extra = {"If-None-Match": if_none_match} if if_none_match else None
    return http("GET", f"{BASE}/{DOMAIN}/workflows/{PARENT}/instances/{instance_id}/functions/state", extra=extra)


def get_href(href, if_none_match=None):
    # hrefs come host-relative with the /api/v1 prefix.
    url = DEFAULT_BASE_URL + href if href.startswith("/") else href
    extra = {"If-None-Match": if_none_match} if if_none_match else None
    return http("GET", url, extra=extra)


def transition(instance_id, key, body=None):
    url = f"{BASE}/{DOMAIN}/workflows/{PARENT}/instances/{instance_id}/transitions/{key}?sync=false"
    for attempt in range(5):
        status, _, resp = http("PATCH", url, body or {})
        if status == 409 and "Failed to acquire lock" in json.dumps(resp) and attempt < 4:
            time.sleep(0.2)
            continue
        return status, resp
    return status, resp


def wait_until(pred, what, timeout_s=45, poll=0.4):
    deadline = time.time() + timeout_s
    last = None
    while time.time() < deadline:
        last = pred()
        if last:
            return last
        time.sleep(poll)
    FAILURES.append(f"timeout: {what}")
    print(f"  ✘ timeout: {what}")
    return None


def active_subflow(state_body):
    for c in state_body.get("activeCorrelations") or []:
        if c.get("subFlowType") == "S" and not c.get("isCompleted"):
            return c
    return None


def publish_all():
    docs = [
        REPO / "core/Tasks/subflow-data-source-lab/sds-script-task.json",
        REPO / "core/Views/subflow-data-source-lab/sds-parent-view.json",
        REPO / "core/Views/subflow-data-source-lab/sds-child-view.json",
        REPO / f"core/Workflows/subflow-data-source-lab/{CHILD}.json",
        REPO / f"core/Workflows/subflow-data-source-lab/{PARENT}.json",
    ]
    for path in docs:
        doc = json.loads(path.read_text())
        status, _, body = http("POST", f"{BASE}/definitions/publish", doc, timeout=60)
        if status in (200, 201):
            print(f"  publish {path.name}: OK (v{doc['version']})")
        elif status == 409 and "100002" in json.dumps(body):
            print(f"  publish {path.name}: SKIP (v{doc['version']} already published)")
        else:
            print(f"  publish {path.name}: HTTP {status}: {json.dumps(body)[:300]}")
            sys.exit(1)
    http("GET", f"{BASE}/definitions/re-initialize")
    time.sleep(2)


def main():
    global BASE, DEFAULT_BASE_URL, RECORD_DIR
    ap = argparse.ArgumentParser()
    ap.add_argument("--base-url", default=DEFAULT_BASE_URL)
    ap.add_argument("--publish", action="store_true")
    ap.add_argument("--record", default=None, help="directory for recorded responses")
    args = ap.parse_args()
    DEFAULT_BASE_URL = args.base_url.rstrip("/")
    BASE = DEFAULT_BASE_URL + "/api/v1"
    RECORD_DIR = Path(args.record).resolve() if args.record else None

    if args.publish:
        print("== publish")
        publish_all()

    lab_ref = f"sds-{uuid.uuid4().hex[:8]}"
    print(f"== start {PARENT} (labRef {lab_ref})")
    status, _, body = http("POST", f"{BASE}/{DOMAIN}/workflows/{PARENT}/instances/start", {"labRef": lab_ref})
    if status not in (200, 201, 202):
        print(f"start failed: HTTP {status} {body}")
        sys.exit(1)
    pid = body.get("id") or body.get("Id")
    print(f"  parent {pid}")

    wait_until(lambda: (lambda r: r[2].get("state") == "parent-wait" and r[2])(state_fn(pid)),
               "parent reaches parent-wait")
    st0, h0, b0 = state_fn(pid)
    record("00-state-parent-wait", st0, h0, b0)
    idle_state_etag = etag_of(h0)
    st, h, b = get_href(b0["data"]["href"])
    record("00-data-parent-wait", st, h, b)
    check(st == 200 and "parentMarker" in json.dumps(b), "parent data at parent-wait carries parentMarker")

    print("== T1 enter-child: the parent descends to the child")
    st, resp = transition(pid, "enter-child")
    check(st < 400, f"enter-child accepted ({st})")
    b1 = wait_until(lambda: (lambda r: r[2].get("state") == "child-wait" and r[2])(state_fn(pid)),
                    "parent state function shows child-wait")
    st1, h1, b1 = state_fn(pid)
    record("01-state-in-child", st1, h1, b1)
    state_etag_in_child = etag_of(h1)
    corr = active_subflow(b1)
    check(b1.get("state") == "child-wait", f"state == child-wait (got {b1.get('state')})")
    check((b1.get("view") or {}).get("hasView") is True, "view.hasView is true (the child's view)")
    check(b1.get("status") == "A", f"status A while the child waits (got {b1.get('status')})")
    check(corr is not None, "an active S correlation is present")
    if corr:
        child_id = corr.get("subFlowInstanceId")
        check(corr.get("subFlowDomain") == DOMAIN and corr.get("subFlowName") == CHILD,
              f"correlation names {DOMAIN}/{CHILD}")
        href = corr.get("href") or ""
        check(href.endswith(f"/{DOMAIN}/workflows/{CHILD}/instances/{child_id}/functions/data") or
              f"/{DOMAIN}/workflows/{CHILD}/instances/{child_id}/functions/data?" in href,
              f"correlation href is the child's data function: {href}")
        note(f"data.href (parent) = {b1['data']['href']}")
    else:
        print(json.dumps(b1, indent=1)[:1500])
        sys.exit(1)

    print("== T2 whose data is it")
    stp, hp, bp = get_href(b1["data"]["href"])
    record("02-data-parent-in-child", stp, hp, bp)
    parent_data_etag = etag_of(hp)
    check(stp == 200 and "parentMarker" in json.dumps(bp) and "childMarker" not in json.dumps(bp),
          "data.href body: parentMarker present, childMarker absent")
    stc, hc, bc = get_href(corr["href"])
    record("02-data-child-in-child", stc, hc, bc)
    child_data_etag = etag_of(hc)
    check(stc == 200 and "childMarker" in json.dumps(bc), "correlation href body: childMarker present")
    check(bool(parent_data_etag) and bool(child_data_etag), f"both data responses carry an ETag ({parent_data_etag} / {child_data_etag})")

    print("== T3 touch: only the child's data changes")
    st, _ = transition(pid, "touch")
    check(st < 400, f"touch forwarded to the child via the parent ({st})")

    def child_counter():
        s, h, b = get_href(corr["href"])
        data = b.get("data") if isinstance(b.get("data"), dict) else b
        return (s, h, b) if s == 200 and str((data or {}).get("childCounter")) == "1" else None

    r = wait_until(child_counter, "child data shows childCounter == 1")
    if r:
        record("03-data-child-after-touch", *r)
        check(etag_of(r[1]) != child_data_etag, "child data ETag moved after touch")
    stp2, hp2, bp2 = get_href(b1["data"]["href"], if_none_match=parent_data_etag)
    record("03-data-parent-after-touch", stp2, hp2, bp2)
    check(stp2 == 304, f"parent data answers 304 to its old ETag after touch (got {stp2}) — the 474774/1084 condition")
    sts, hs, bs = state_fn(pid, if_none_match=state_etag_in_child)
    record("03-state-after-touch", sts, hs, bs)
    note(f"parent STATE after child $self touch: HTTP {sts} (ETag {state_etag_in_child} -> {etag_of(hs)}) — recorded, not asserted")

    print("== T4 next: the child's state advances")
    st, _ = transition(pid, "next")
    check(st < 400, f"next accepted ({st})")
    wait_until(lambda: (lambda r: r[2].get("state") == "child-wait-2" and r[2])(state_fn(pid)),
               "parent state function shows child-wait-2")
    st4, h4, b4 = state_fn(pid)
    record("04-state-child-wait-2", st4, h4, b4)
    check(etag_of(h4) != state_etag_in_child, f"parent state ETag moved on child state advance ({state_etag_in_child} -> {etag_of(h4)})")
    check(active_subflow(b4) is not None and active_subflow(b4).get("subFlowInstanceId") == child_id,
          "the same correlation is still active")

    print("== T5 finish: the parent resumes with the child's output")
    st, _ = transition(pid, "finish")
    check(st < 400, f"finish forwarded to the child ({st})")
    wait_until(lambda: (lambda r: r[2].get("state") == "parent-after" and r[2])(state_fn(pid)),
               "parent reaches parent-after")
    st5, h5, b5 = state_fn(pid)
    record("05-state-parent-after", st5, h5, b5)
    check(active_subflow(b5) is None, "no active S correlation after the child finished")
    stp5, hp5, bp5 = get_href(b5["data"]["href"], if_none_match=parent_data_etag)
    record("05-data-parent-after", stp5, hp5, bp5)
    data5 = bp5.get("data") if isinstance(bp5.get("data"), dict) else bp5
    check(stp5 == 200, f"parent data is fresh again after the child finished (got {stp5})")
    check(bool((data5 or {}).get("childResult")) and str((data5 or {}).get("childCounter")) == "1",
          "parent data carries childResult and the child's counter")

    st, _ = transition(pid, "finish-parent")
    note(f"finish-parent -> {st} (cleanup)")

    print()
    if FAILURES:
        print(f"FAILED ({len(FAILURES)}):")
        for f in FAILURES:
            print("  - " + f)
        sys.exit(1)
    print("ALL CHECKS PASSED" + (f" — recorded under {RECORD_DIR}" if RECORD_DIR else ""))


if __name__ == "__main__":
    main()
