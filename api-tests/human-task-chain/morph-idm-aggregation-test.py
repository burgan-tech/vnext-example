#!/usr/bin/env python3
"""End-to-end check of the human-task path through morph-idm-api.

The integration suite in tests/Core.IntegrationTests proves one domain's answer. This proves the
CLIENT's answer: morph-idm reads discovery's domain-list, calls every domain's human-task function,
and merges. It is the only place the whole chain is exercised together —

    client -> morph-idm -> discovery domain-list -> per-domain human-task -> leaf descent -> leaf

Requires the four-domain lab and morph-idm on the same Docker network (see README.md).

    python3 api-tests/human-task-chain/morph-idm-aggregation-test.py \
        --core http://localhost:4201 --idm http://localhost:5288

Exit code 0 when every check passes, 1 otherwise. No dependencies beyond the standard library.
"""
import argparse
import json
import sys
import time
import subprocess
import urllib.error
import urllib.request
from collections import Counter

ROLE = "ht-approver"
USER = "aggregation-test"

# hops -> the leaf that ends up holding the task. The chain is a -> b -> c (core) -> d (partner)
# -> e -> f (credit); every level writes its own humanTask text, so the title names the leaf.
SCENARIOS = [
    (0, "HT-A step", "root is its own leaf"),
    (2, "HT-C step", "three levels, one domain"),
    (3, "HT-D step", "one domain boundary"),
    (5, "HT-F step", "two boundaries, second crossed by partner"),
]

failures: list[str] = []
checks = 0


def check(condition: bool, label: str, detail: str = "") -> bool:
    global checks
    checks += 1
    if condition:
        print(f"  PASS  {label}")
        return True
    failures.append(f"{label}{(' — ' + detail) if detail else ''}")
    print(f"  FAIL  {label}{(' — ' + detail) if detail else ''}")
    return False


def request(url: str, headers: dict[str, str] | None = None, body: dict | None = None,
            method: str | None = None, timeout: int = 30, with_headers: bool = False):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method or ("POST" if data else "GET"))
    for key, value in (headers or {}).items():
        req.add_header(key, value)
    if data:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=timeout) as response:
        raw = response.read().decode()
        payload = json.loads(raw) if raw.strip() else None
        # with_headers swaps the status for the response headers: the truncation signal travels
        # beside the body, so a check on it needs both halves.
        return (dict(response.headers), payload) if with_headers else (response.status, payload)


def vnext_headers(role: str = ROLE, fresh: bool = False) -> dict[str, str]:
    headers = {
        "x-roles": role,
        "role": role,
        "user_reference": USER,
        "x-device-id": "aggregation-test",
    }
    if fresh:
        # The response cache has a 60 s TTL and no validation query. A test that polls through it
        # waits out the TTL on a pre-change answer, then reports a wrong list rather than a stale
        # one — which is a different and far more misleading failure.
        headers["X-VNext-Cache-Override"] = "true"
    return headers


def start_chain(core: str, hops: int) -> str:
    _, body = request(
        f"{core}/api/v1/core/workflows/ht-a/instances/start?sync=true",
        headers=vnext_headers(),
        body={
            "hops": hops,
            "testId": f"aggregation-{hops}-{int(time.time())}",
            "humanTask": {"title": "HT-A step", "description": "HT-A step description"},
        },
    )
    return body["id"]


def start_spawn(core: str, process_hops: int) -> str:
    """
    Starts a root that spawns a SubProcess (`ht-a-spawn`, subFlow.type = "P") into partner and
    itself rests in its own human state.

    Started ASYNC on purpose. A state-level SubProcess followed by an automatic transition cannot be
    started with `sync=true` today: the post-commit ContinueParent continuation re-enters the
    pipeline with IsPreReserved = false while the instance is still Busy from the stage that
    continuation belongs to, so admission answers 409 conflict.Instance:100031. See
    TEST-SCENARIOS.md § Bilinen Kapsam Açıkları.
    """
    _, body = request(
        f"{core}/api/v1/core/workflows/ht-a/instances/start",
        headers=vnext_headers(),
        body={
            "mode": "process",
            "hops": 0,
            "processHops": process_hops,
            "testId": f"aggregation-spawn-{int(time.time())}",
            "humanTask": {"title": "HT-A step", "description": "HT-A step description"},
        },
    )
    return body["id"]


def wait_for(predicate, describe: str, timeout: int = 120):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if predicate():
            return True
        time.sleep(2)
    print(f"  FAIL  timed out after {timeout}s waiting for {describe}")
    failures.append(f"timeout: {describe}")
    return False


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--core", default="http://localhost:4201")
    parser.add_argument("--partner", default="http://localhost:4211")
    parser.add_argument("--credit", default="http://localhost:4221")
    parser.add_argument("--discovery", default="http://localhost:4231")
    parser.add_argument("--idm", default="http://localhost:5288")
    parser.add_argument("--timeout", type=int, default=120, help="seconds to wait for a chain to settle")
    args = parser.parse_args()

    print("\n[1] discovery domain-list — the list morph-idm fans out over")
    try:
        _, listing = request(f"{args.discovery}/api/v1/discovery/functions/domain-list")
    except urllib.error.URLError as error:
        print(f"  FAIL  discovery unreachable: {error}")
        return 1

    domains = {d["domainName"]: d for d in (listing or {}).get("items", [])}
    print(f"        {len(domains)} domain(s): {', '.join(sorted(domains))}")
    check("core" in domains, "core is registered")
    for name in ("partner", "credit"):
        # Not fatal for the aggregation — core answers for the whole chain — but their absence
        # means the descent's remote hops would fail, so it is worth naming here.
        check(name in domains, f"{name} is registered")

    print("\n[2] start one chain per scenario")
    started: dict[int, str] = {}
    for hops, title, why in SCENARIOS:
        try:
            started[hops] = start_chain(args.core, hops)
            print(f"        hops={hops} ({why}) -> {started[hops][:8]}  expecting '{title}'")
        except urllib.error.HTTPError as error:
            print(f"  FAIL  start(hops={hops}) failed: {error.read().decode()[:200]}")
            failures.append(f"start hops={hops}")

    spawn_root = None
    try:
        spawn_root = start_spawn(args.core, process_hops=2)
        print(f"        spawn  (SubProcess into partner, 2 more levels) -> {spawn_root[:8]}")
    except urllib.error.HTTPError as error:
        print(f"  FAIL  spawn start failed: {error.read().decode()[:200]}")
        failures.append("start spawn")

    def core_rows(role: str = ROLE, fresh: bool = True):
        _, rows = request(f"{args.core}/api/v1/core/functions/human-task",
                          headers=vnext_headers(role, fresh=fresh))
        return rows or []

    print("\n[3] wait until every chain has come to rest on its leaf")
    wait_for(lambda: {r["id"] for r in core_rows()} >= set(started.values()),
             "all started chains to surface", args.timeout)

    print("\n[4] the leaf's text reaches core's own list")
    rows_by_id = {r["id"]: r for r in core_rows()}
    for hops, title, _ in SCENARIOS:
        row = rows_by_id.get(started.get(hops, ""))
        check(row is not None and row.get("title") == title,
              f"hops={hops} -> '{title}'",
              f"got {row.get('title')!r}" if row else "row missing")
        if row:
            check(row.get("workflow") == "ht-a",
                  f"hops={hops} row is addressed by the ROOT",
                  f"workflow={row.get('workflow')!r}")

    print("\n[5] a SubFlow child is represented by its root; a SubProcess is not")
    # An `S` child is the SAME unit of work as its root, so the domain that hosts it must not list
    # it a second time — the root already stands for it. A `P` child is an INDEPENDENT flow: its
    # domain lists it on its own, under its own id, and it descends into its own SubFlows.
    child_rows: dict[str, list] = {}
    for name, base in (("partner", args.partner), ("credit", args.credit)):
        try:
            _, rows = request(f"{base}/api/v1/{name}/functions/human-task",
                              headers=vnext_headers(fresh=True))
            child_rows[name] = rows or []
        except urllib.error.URLError as error:
            print(f"  SKIP  {name} unreachable ({error})")

    started_ids = set(started.values()) | ({spawn_root} if spawn_root else set())
    for name, rows in child_rows.items():
        leaked = [r for r in rows if r.get("id") in started_ids]
        check(not leaked, f"{name} does not list a root of this run",
              f"{len(leaked)} row(s) — a SubFlow child must be represented by its root, not listed twice")
        check(all(r.get("workflow") != "ht-a" for r in rows),
              f"{name} lists no ht-a row of its own")

    if spawn_root:
        spawned = [r for r in child_rows.get("partner", []) if r.get("workflow") == "ht-d"]
        check(bool(spawned), "partner lists the spawned SubProcess on its own",
              "no ht-d row in partner's answer")
        # A SubProcess inherits its parent's business Key, so addressing it by Key would send a
        # client to the ROOT. It must be addressed by its own id.
        check(all(r.get("instanceId") == r.get("id") for r in spawned),
              "the SubProcess is addressed by its own id, not its parent's key",
              f"{[(r.get('instanceId'), r.get('id')) for r in spawned][:3]}")
        check(any(r.get("title") == "HT-F step" for r in spawned),
              "the SubProcess descended its own SubFlows into credit",
              f"titles: {sorted({r.get('title') for r in spawned})}")

    print("\n[6] a caller with another role sees none of it")
    other = {r["id"] for r in core_rows(role="some.other.role")}
    check(not (other & set(started.values())),
          "leaf-side authorization excludes the wrong role")

    print("\n[7] morph-idm aggregation — the client's answer")
    try:
        _, payload = request(f"{args.idm}/api/discovery/human-tasks",
                             headers={"act_sub": USER, "role": ROLE})
    except urllib.error.URLError as error:
        print(f"  FAIL  morph-idm unreachable: {error}")
        failures.append("morph-idm unreachable")
        payload = None

    if payload is not None:
        tasks = payload.get("humanTasks", [])
        titles = Counter(t.get("title") for t in tasks)
        print(f"        {len(tasks)} task(s); titles: {dict(titles)}")
        print(f"        domains: {dict(Counter(t.get('domainName') for t in tasks))}")

        for _, title, why in SCENARIOS:
            check(titles.get(title, 0) > 0, f"aggregation carries '{title}' ({why})")

        check(all(t.get("domainName") for t in tasks),
              "every row names the domain that answered")
        check(all(t.get("vnextTask") for t in tasks),
              "every row is flagged as a vNext task")

    print("\n[8] morph-idm carries the caller's context down and the domains' truncation up")
    try:
        # Cache override: without it a second read of the SAME caller is served from vNext's 60 s
        # cache; with it every read rebuilds. Timing is the only observable difference, so the check
        # is a ratio rather than an absolute.
        import time as _t
        user_cached = f"ctx-cached-{int(_t.time())}"
        _, _ = request(f"{args.idm}/api/discovery/human-tasks",
                       headers={"act_sub": user_cached, "role": ROLE})
        t0 = _t.time()
        request(f"{args.idm}/api/discovery/human-tasks", headers={"act_sub": user_cached, "role": ROLE})
        cached_ms = (_t.time() - t0) * 1000

        user_fresh = f"ctx-fresh-{int(_t.time())}"
        request(f"{args.idm}/api/discovery/human-tasks",
                headers={"act_sub": user_fresh, "role": ROLE, "X-VNext-Cache-Override": "true"})
        t0 = _t.time()
        request(f"{args.idm}/api/discovery/human-tasks",
                headers={"act_sub": user_fresh, "role": ROLE, "X-VNext-Cache-Override": "true"})
        fresh_ms = (_t.time() - t0) * 1000

        print(f"        repeat read: cached {cached_ms:.0f} ms vs override {fresh_ms:.0f} ms")
        check(fresh_ms > cached_ms * 1.5,
              "X-VNext-Cache-Override reaches the domains (a repeat read rebuilds)",
              f"cached={cached_ms:.0f}ms override={fresh_ms:.0f}ms - the header looks dropped")

        # Correlation: the id must reach the domain runtime, which is what makes one client request
        # followable across morph-idm and every domain it fanned out to.
        marker = f"e2e-corr-{int(_t.time())}"
        request(f"{args.idm}/api/discovery/human-tasks",
                headers={"act_sub": USER, "role": ROLE, "x-request-id": marker})
        _t.sleep(2)
        found = subprocess.run(
            ["docker", "logs", "vnext-app-core", "--since", "60s"],
            capture_output=True, text=True).stdout.count(marker)
        check(found > 0, "x-request-id reaches the domain runtime",
              f"marker {marker} not found in vnext-app-core logs")

        # Truncation: vNext answers X-VNext-HumanTask-Truncated per domain; the aggregate has to say
        # so too, or a cut list is indistinguishable from a complete one.
        headers, payload = request(f"{args.idm}/api/discovery/human-tasks",
                                   headers={"act_sub": USER, "role": ROLE}, with_headers=True)
        body_flag = payload.get("truncated")
        header_flag = str(headers.get("X-VNext-HumanTask-Truncated", "")).lower() == "true"
        print(f"        truncated={body_flag} domains={payload.get('truncatedDomains')} header={header_flag}")
        check(body_flag is not None, "the aggregate reports its truncation state")
        check(body_flag == header_flag,
              "body and response header agree on truncation",
              f"body={body_flag} header={header_flag}")
        if body_flag:
            check(bool(payload.get("truncatedDomains")),
                  "a truncated aggregate names the domains that cut their list")
    except urllib.error.URLError as error:
        print(f"  FAIL  context round trip failed: {error}")
        failures.append("context round trip")

    print("\n" + "=" * 70)
    if failures:
        print(f"FAILED — {len(failures)} of {checks} checks")
        for failure in failures:
            print(f"  - {failure}")
        return 1
    print(f"PASSED — {checks} checks")
    return 0


if __name__ == "__main__":
    sys.exit(main())
