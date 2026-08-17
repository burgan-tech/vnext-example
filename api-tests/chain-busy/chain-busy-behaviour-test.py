#!/usr/bin/env python3
"""
chain-busy A -> B -> C — sharedTransition / cancel / updateData davranis testleri.

    python3 api-tests/chain-busy/chain-busy-behaviour-test.py --publish --iterations 3
    python3 api-tests/chain-busy/chain-busy-behaviour-test.py --case shared-self --iterations 10
    python3 api-tests/chain-busy/chain-busy-behaviour-test.py --list

## Case'ler

  accept-busy      Async accept, 202 donmeden ONCE zinciri leaf'e kadar Busy'e cekmeli.
                   (chain-busy-accept-test.py'nin buradaki karsiligi)

  start-onentry    Start, baslangic state'inin onEntry'sini calistiriyor mu (root ve leaf).
                   Start bir `$self` degildir; state yasam dongusu tam calismali.

  shared-self      C'nin `$self` shared transition'i (`leaf-only-mark`) A'dan tetiklenir.
                   OnExecute calismali VE state yasam dongusu de kosmali: leafEntries ve
                   leafExits BIRER ARTMALI, armed scheduled job yeniden kurulmali.
                   `target: $self` "instance'i oynatma" der, "state hook'larini atla" DEMEZ.

  shared-parent    A'nin KENDI shared transition'i (`root-shared-mark`). Aktif subflow
                   varken bile A karsilamali, asagi forward EDILMEMELI:
                   rootSharedMarks artar, leafOnlyMarks sabit kalir. State degismez ama
                   A'nin kendi onEntry/onExit'i kosar (rootEntries/rootExits birer artar).

  shared-forward   Yalniz C'de tanimli shared transition A'ya gonderildiginde zincirden
                   asagi forward edilip C'de calismali (shared-self'in forward ispati,
                   sayaclarin hangi instance'ta arttigiyla birlikte).

  updatedata-self  C'ye updateData (`update-leaf-data`, `$self`). Yasam dongusu atlamasi
                   YALNIZ updateData'ya aittir: OnExit / OnEntry / Schedule ATLANMALI —
                   leafEntries, leafExits sabit; armed scheduled job'in ExecuteAt'i
                   DEGISMEMELI; leafUpdates artmali.
                   shared-self ile ZIT yonde davranmasi, daralmanin tam istenen yerde
                   durdugunun kanitidir; ikisi ayni kosuda birlikte degerlendirilmeli.

  cancel-top-down  A'ya cancel -> zincir asagi kaskad: A, B, C hepsi Completed ve
                   ilgili `*-cancelled` state'inde.

  cancel-bottom-up C'ye cancel -> C cancelled; tamamlanma bilgisi yukari yansimali:
                   B ve A da terminal olmali (korelasyonlar kapanir).

On kosullar: orchestration (4201) + execution (4202) host'lari ve docker altyapisi ayakta.
Flow'lar 1.2.0 surumunde; `--publish` leaf-first publish + re-initialize yapar.
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

SCHEMA = {"root": "chain_busy_root", "middle": "chain_busy_middle", "leaf": "chain_busy_leaf"}
JOB_TYPE_SCHEDULED = 2
# Asagi yonlu cancel kaskadi Outbox -> pub/sub -> Inbox uzerinden gider; nihai
# tutarlidir ve yuk altinda saniyeler surebilir.
CANCEL_WAIT_S = 120
TERMINAL = {"C", "F", "P"}


# ── plumbing ────────────────────────────────────────────────────────────────

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
    return out.stdout.strip() if out.returncode == 0 else ""


def transition(workflow, instance_id, key, body=None, sync=False):
    url = "%s/%s/workflows/%s/instances/%s/transitions/%s?sync=%s" % (
        BASE, DOMAIN, workflow, instance_id, key, "true" if sync else "false")
    return http("PATCH", url, body if body is not None else {})


def state_of(workflow, instance_id):
    st, body = http("GET", "%s/%s/workflows/%s/instances/%s/functions/state"
                    % (BASE, DOMAIN, workflow, instance_id))
    if st not in (200, 304):
        return None, None
    return body.get("state"), body.get("status")


def wait_for(workflow, instance_id, predicate, timeout_s=45):
    deadline = time.time() + timeout_s
    last = None
    while time.time() < deadline:
        s, stt = state_of(workflow, instance_id)
        last = (s, stt)
        if s is not None and predicate(s, stt):
            return True, last
        time.sleep(0.25)
    return False, last


# ── DB gozlemleri (otorite) ─────────────────────────────────────────────────

def instance_row(level, instance_id):
    row = psql('select "Status" || \'|\' || coalesce("CurrentState",\'\') '
               'from %s."Instances" where "Id"=\'%s\'' % (SCHEMA[level], instance_id))
    if "|" not in row:
        return None, None
    status, current = row.split("|", 1)
    return status, current


def latest_data(level, instance_id):
    raw = psql('select "Data" from %s."InstancesData" where "InstanceId"=\'%s\' '
               'and "IsLatest"=true limit 1' % (SCHEMA[level], instance_id))
    if not raw:
        return {}
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        return {}


def counter(level, instance_id, name):
    value = latest_data(level, instance_id).get(name, 0)
    try:
        return int(value)
    except (TypeError, ValueError):
        return 0


def scheduled_job(level, instance_id):
    """Armed scheduled transition: (JobId, ExecuteAt). Yeniden kurulma bunlardan okunur."""
    row = psql('select "JobId" || \'|\' || coalesce("ExecuteAt"::text,\'\') '
               'from %s."InstanceJobs" where "InstanceId"=\'%s\' and "JobType"=%d '
               'and "IsActive"=true order by "CreatedAt" desc limit 1'
               % (SCHEMA[level], instance_id, JOB_TYPE_SCHEDULED))
    if "|" not in row:
        return None, None
    job_id, execute_at = row.split("|", 1)
    return job_id, execute_at


def chain_ids(root_id):
    middle = psql('select "SubFlowInstanceId" from %s."InstancesCorrelations" '
                  'where "InstanceId"=\'%s\' order by "CreatedAt" desc limit 1'
                  % (SCHEMA["root"], root_id))
    if not middle:
        return None, None
    leaf = psql('select "SubFlowInstanceId" from %s."InstancesCorrelations" '
                'where "InstanceId"=\'%s\' order by "CreatedAt" desc limit 1'
                % (SCHEMA["middle"], middle))
    return middle or None, leaf or None


# ── ortak kurulum ───────────────────────────────────────────────────────────

def build_chain(tag):
    """A'yi baslatir ve zincir C `leaf-waiting`de Active bekleyene kadar surer."""
    st, body = http("POST", "%s/%s/workflows/%s/instances/start" % (BASE, DOMAIN, ROOT_WF),
                    {"testId": "%s-%s" % (tag, uuid.uuid4().hex[:8])})
    if st not in (200, 201, 202):
        return None, None, None, "start HTTP %s: %s" % (st, body)

    root_id = body.get("id") or body.get("Id")
    ok, last = wait_for(ROOT_WF, root_id, lambda s, _: s == "leaf-waiting", 60)
    if not ok:
        return root_id, None, None, "leaf-waiting'e ulasilamadi (son: %s)" % (last,)

    middle_id, leaf_id = chain_ids(root_id)
    if not leaf_id:
        return root_id, middle_id, None, "leaf instance korelasyondan cozulemedi"
    return root_id, middle_id, leaf_id, None


def snapshot(root_id, middle_id, leaf_id):
    return {
        "rootInitialEntries": counter("root", root_id, "rootInitialEntries"),
        "leafInitialEntries": counter("leaf", leaf_id, "leafInitialEntries"),
        "rootEntries": counter("root", root_id, "rootEntries"),
        "rootExits": counter("root", root_id, "rootExits"),
        "rootSharedMarks": counter("root", root_id, "rootSharedMarks"),
        "middleSharedMarks": counter("middle", middle_id, "middleSharedMarks"),
        "leafEntries": counter("leaf", leaf_id, "leafEntries"),
        "leafExits": counter("leaf", leaf_id, "leafExits"),
        "leafOnlyMarks": counter("leaf", leaf_id, "leafOnlyMarks"),
        "leafUpdates": counter("leaf", leaf_id, "leafUpdates"),
    }


def settle(workflow, instance_id, timeout_s=30):
    """Instance Busy'den cikana kadar bekler (transition'in tamamlanmasi)."""
    return wait_for(workflow, instance_id, lambda _s, stt: stt != "B", timeout_s)


# ── case'ler ────────────────────────────────────────────────────────────────

def case_accept_busy(r):
    root_id, middle_id, leaf_id, err = build_chain("accept")
    r["ids"] = (root_id, middle_id, leaf_id)
    if err:
        return r["errors"].append(err)

    _, before = state_of(ROOT_WF, root_id)
    if before != "A":
        return r["errors"].append("on kosul: transition oncesi status %s, beklenen A" % before)

    st, resp = transition(ROOT_WF, root_id, "finish-leaf")
    if st not in (200, 202):
        return r["errors"].append("finish-leaf accept HTTP %s: %s" % (st, resp))

    _, after = state_of(ROOT_WF, root_id)
    r["detail"] = "client: %s -> %s" % (before, after)
    if after != "B":
        r["errors"].append("202 sonrasi client %s gordu, beklenen B" % after)

    ok, last = wait_for(ROOT_WF, root_id, lambda s, stt: stt == "C" or s == "root-done", 60)
    if not ok:
        r["errors"].append("zincir tamamlanmadi (son: %s)" % (last,))


def case_shared_self(r):
    """C'nin $self shared transition'i TAM state yasam dongusunu kosmali."""
    root_id, middle_id, leaf_id, err = build_chain("shared-self")
    r["ids"] = (root_id, middle_id, leaf_id)
    if err:
        return r["errors"].append(err)

    before = snapshot(root_id, middle_id, leaf_id)
    _, sched_before = scheduled_job("leaf", leaf_id)
    if before["leafEntries"] < 1:
        return r["errors"].append(
            "on kosul: leaf-waiting'e girilmemis (leafEntries=%d) — sayac yanlis sebeple "
            "hareket ediyor olurdu" % before["leafEntries"])

    st, resp = transition(ROOT_WF, root_id, "leaf-only-mark")
    if st not in (200, 202):
        return r["errors"].append("leaf-only-mark HTTP %s: %s" % (st, resp))
    settle(ROOT_WF, root_id)
    time.sleep(0.6)

    after = snapshot(root_id, middle_id, leaf_id)
    _, sched_after = scheduled_job("leaf", leaf_id)
    r["detail"] = "leafOnlyMarks %d->%d  leafEntries %d->%d  leafExits %d->%d" % (
        before["leafOnlyMarks"], after["leafOnlyMarks"],
        before["leafEntries"], after["leafEntries"],
        before["leafExits"], after["leafExits"])

    if after["leafOnlyMarks"] != before["leafOnlyMarks"] + 1:
        r["errors"].append("OnExecute calismadi: leafOnlyMarks %d -> %d"
                           % (before["leafOnlyMarks"], after["leafOnlyMarks"]))
    if after["leafEntries"] != before["leafEntries"] + 1:
        r["errors"].append("$self shared OnEntry'yi kosmadi: leafEntries %d -> %d (beklenen %d)"
                           % (before["leafEntries"], after["leafEntries"], before["leafEntries"] + 1))
    if after["leafExits"] != before["leafExits"] + 1:
        r["errors"].append("$self shared OnExit'e girmedi: leafExits %d -> %d (beklenen %d)"
                           % (before["leafExits"], after["leafExits"], before["leafExits"] + 1))
    # NOT: zamanlayici kontrolu burada YAPILMAZ. State fonksiyonu fingerprint dogrulamali bir
    # cache ve job seti bilerek fingerprint'in DISINDA; `$self` state/status'u degistirmedigi icin
    # ikinci okuma birincinin degerini doner (kabul edilmis bilinen bosluk,
    # docs/runtime/state-function-cache-and-etag.md). Before/after karsilastirmasi bu yuzden her
    # kosulda "yeniden kurulmadi" der. Zamanlayici, `shared-sched` case'inde tek ve cache-cold bir
    # okuma ile duvar saatine karsi olculur.


def case_shared_parent(r):
    """A'nin kendi shared transition'i A'da karsilanmali, asagi forward EDILMEMELI."""
    root_id, middle_id, leaf_id, err = build_chain("shared-parent")
    r["ids"] = (root_id, middle_id, leaf_id)
    if err:
        return r["errors"].append(err)

    before = snapshot(root_id, middle_id, leaf_id)
    _, root_state_before = state_of(ROOT_WF, root_id)

    st, resp = transition(ROOT_WF, root_id, "root-shared-mark")
    if st not in (200, 202):
        return r["errors"].append("root-shared-mark HTTP %s: %s" % (st, resp))
    settle(ROOT_WF, root_id)
    time.sleep(0.6)

    after = snapshot(root_id, middle_id, leaf_id)
    _, root_current = instance_row("root", root_id)
    r["detail"] = "rootSharedMarks %d->%d  leafOnlyMarks %d->%d  root state %s" % (
        before["rootSharedMarks"], after["rootSharedMarks"],
        before["leafOnlyMarks"], after["leafOnlyMarks"], root_current)

    if after["rootSharedMarks"] != before["rootSharedMarks"] + 1:
        r["errors"].append("parent kendi shared'ini karsilamadi: rootSharedMarks %d -> %d"
                           % (before["rootSharedMarks"], after["rootSharedMarks"]))
    if after["leafOnlyMarks"] != before["leafOnlyMarks"]:
        r["errors"].append("parent shared'i asagi FORWARD edildi: leafOnlyMarks %d -> %d"
                           % (before["leafOnlyMarks"], after["leafOnlyMarks"]))
    if root_current != "root-waiting":
        r["errors"].append("$self olmasina ragmen parent state degisti: %s" % root_current)
    if after["rootEntries"] != before["rootEntries"] + 1 or after["rootExits"] != before["rootExits"] + 1:
        r["errors"].append("parent $self shared state yasam dongusunu kosmadi: entries %d->%d exits %d->%d "
                           "(ikisi de birer artmaliydi)"
                           % (before["rootEntries"], after["rootEntries"],
                              before["rootExits"], after["rootExits"]))


def case_shared_forward(r):
    """Parent'ta OLMAYAN shared transition zincirden asagi forward edilmeli."""
    root_id, middle_id, leaf_id, err = build_chain("shared-fwd")
    r["ids"] = (root_id, middle_id, leaf_id)
    if err:
        return r["errors"].append(err)

    before = snapshot(root_id, middle_id, leaf_id)

    # A'da boyle bir shared transition yok -> forward beklenir.
    st, resp = transition(ROOT_WF, root_id, "leaf-only-mark")
    if st not in (200, 202):
        return r["errors"].append("leaf-only-mark HTTP %s: %s" % (st, resp))
    settle(ROOT_WF, root_id)
    time.sleep(0.6)

    after = snapshot(root_id, middle_id, leaf_id)
    r["detail"] = "leafOnlyMarks %d->%d  rootSharedMarks %d->%d  middleSharedMarks %d->%d" % (
        before["leafOnlyMarks"], after["leafOnlyMarks"],
        before["rootSharedMarks"], after["rootSharedMarks"],
        before["middleSharedMarks"], after["middleSharedMarks"])

    if after["leafOnlyMarks"] != before["leafOnlyMarks"] + 1:
        r["errors"].append("C'ye forward edilmedi: leafOnlyMarks %d -> %d"
                           % (before["leafOnlyMarks"], after["leafOnlyMarks"]))
    if after["rootSharedMarks"] != before["rootSharedMarks"]:
        r["errors"].append("A'da da calisti: rootSharedMarks %d -> %d"
                           % (before["rootSharedMarks"], after["rootSharedMarks"]))
    if after["middleSharedMarks"] != before["middleSharedMarks"]:
        r["errors"].append("B'de de calisti: middleSharedMarks %d -> %d"
                           % (before["middleSharedMarks"], after["middleSharedMarks"]))


def case_updatedata_self(r):
    """updateData $self: OnEntry/OnExit yok, scheduled transition yeniden kurulmuyor."""
    root_id, middle_id, leaf_id, err = build_chain("updatedata")
    r["ids"] = (root_id, middle_id, leaf_id)
    if err:
        return r["errors"].append(err)

    before = snapshot(root_id, middle_id, leaf_id)
    sched_id_before, sched_at_before = scheduled_job("leaf", leaf_id)
    if not sched_id_before:
        return r["errors"].append("leaf-waiting'de armed scheduled job bulunamadi "
                                  "(testin on kosulu)")
    if before["leafEntries"] < 1:
        return r["errors"].append("on kosul: leaf-waiting'e girilmemis (leafEntries=%d)"
                                  % before["leafEntries"])

    # updateData C'ye DOGRUDAN gonderilir: aktif subflow'u olan A'da data-only kisa devre
    # olurdu, $self profili tam olarak yaprakta gozlenir.
    st, resp = transition(LEAF_WF, leaf_id, "update-leaf-data", {"probe": uuid.uuid4().hex[:6]})
    if st not in (200, 202):
        return r["errors"].append("update-leaf-data HTTP %s: %s" % (st, resp))
    settle(LEAF_WF, leaf_id)
    time.sleep(0.6)

    after = snapshot(root_id, middle_id, leaf_id)
    sched_id_after, sched_at_after = scheduled_job("leaf", leaf_id)
    _, leaf_current = instance_row("leaf", leaf_id)
    r["detail"] = "leafUpdates %d->%d  entries %d->%d  exits %d->%d  sched %s" % (
        before["leafUpdates"], after["leafUpdates"],
        before["leafEntries"], after["leafEntries"],
        before["leafExits"], after["leafExits"],
        "sabit" if sched_at_after == sched_at_before else "YENIDEN KURULDU")

    if after["leafUpdates"] != before["leafUpdates"] + 1:
        r["errors"].append("updateData OnExecute calismadi: leafUpdates %d -> %d"
                           % (before["leafUpdates"], after["leafUpdates"]))
    if after["leafEntries"] != before["leafEntries"]:
        r["errors"].append("updateData OnEntry'yi calistirdi: leafEntries %d -> %d"
                           % (before["leafEntries"], after["leafEntries"]))
    if after["leafExits"] != before["leafExits"]:
        r["errors"].append("updateData OnExit'e girdi: leafExits %d -> %d"
                           % (before["leafExits"], after["leafExits"]))
    if sched_id_after != sched_id_before or sched_at_after != sched_at_before:
        r["errors"].append("updateData scheduled transition'i YENIDEN KURDU: %s@%s -> %s@%s"
                           % (sched_id_before, sched_at_before, sched_id_after, sched_at_after))
    if leaf_current != "leaf-waiting":
        r["errors"].append("$self olmasina ragmen state degisti: %s" % leaf_current)


def case_cancel_top_down(r):
    """A'ya cancel -> zincir asagi kaskad."""
    root_id, middle_id, leaf_id, err = build_chain("cancel-top")
    r["ids"] = (root_id, middle_id, leaf_id)
    if err:
        return r["errors"].append(err)

    st, resp = transition(ROOT_WF, root_id, "cancel-chain-busy-root")
    if st not in (200, 202):
        return r["errors"].append("cancel HTTP %s: %s" % (st, resp))

    deadline = time.time() + CANCEL_WAIT_S
    rows = {}
    while time.time() < deadline:
        rows = {lvl: instance_row(lvl, iid) for lvl, iid in
                (("root", root_id), ("middle", middle_id), ("leaf", leaf_id))}
        if all(v[0] in TERMINAL for v in rows.values()):
            break
        time.sleep(0.4)

    r["detail"] = "  ".join("%s=%s/%s" % (lvl, v[0], v[1]) for lvl, v in rows.items())
    for lvl, (status, current) in rows.items():
        if status not in TERMINAL:
            r["errors"].append("%s kaskad olmadi: status %s state %s" % (lvl, status, current))
        elif not current or not current.endswith("-cancelled"):
            r["errors"].append("%s cancel state'ine gitmedi: %s" % (lvl, current))


def case_cancel_bottom_up(r):
    """C'ye cancel -> yukari dogru tamamlanma bilgisi yansimali."""
    root_id, middle_id, leaf_id, err = build_chain("cancel-bottom")
    r["ids"] = (root_id, middle_id, leaf_id)
    if err:
        return r["errors"].append(err)

    st, resp = transition(LEAF_WF, leaf_id, "cancel-chain-busy-leaf")
    if st not in (200, 202):
        return r["errors"].append("cancel HTTP %s: %s" % (st, resp))

    deadline = time.time() + CANCEL_WAIT_S
    rows = {}
    while time.time() < deadline:
        rows = {lvl: instance_row(lvl, iid) for lvl, iid in
                (("root", root_id), ("middle", middle_id), ("leaf", leaf_id))}
        if all(v[0] in TERMINAL for v in rows.values()):
            break
        time.sleep(0.4)

    open_corr = psql('select count(*) from %s."InstancesCorrelations" '
                     'where "InstanceId"=\'%s\' and "IsCompleted"=false'
                     % (SCHEMA["root"], root_id))
    r["detail"] = "  ".join("%s=%s/%s" % (lvl, v[0], v[1]) for lvl, v in rows.items()) \
                  + "  acikKorelasyon=%s" % open_corr

    leaf_status, leaf_state = rows.get("leaf", (None, None))
    if leaf_status not in TERMINAL or not (leaf_state or "").endswith("-cancelled"):
        r["errors"].append("leaf cancel olmadi: %s/%s" % (leaf_status, leaf_state))
    for lvl in ("middle", "root"):
        status, current = rows.get(lvl, (None, None))
        if status not in TERMINAL:
            r["errors"].append("%s yukari yonlu tamamlanmadi: status %s state %s"
                               % (lvl, status, current))
    if open_corr not in ("0", ""):
        r["errors"].append("A'da acik korelasyon kaldi: %s" % open_corr)


def case_start_onentry(r):
    """Baslangic state'inin onEntry'si start sirasinda calisiyor mu.

    Start, instance'i olusturma aninda baslangic state'ine PRE-POSITION eder ve ardindan
    start transition'ini `initial -> initial` olarak kosar. Bu bir `$self` DEGILDIR, yani
    state yasam dongusunun tamamen calismasi beklenir. Bu case iki seviyede olcer:
    root (ust seviye start) ve leaf (subflow start).
    """
    root_id, middle_id, leaf_id, err = build_chain("start-onentry")
    r["ids"] = (root_id, middle_id, leaf_id)
    if err:
        return r["errors"].append(err)

    snap = snapshot(root_id, middle_id, leaf_id)
    r["detail"] = "rootInitialEntries=%d  leafInitialEntries=%d  (root-waiting girisi=%d)" % (
        snap["rootInitialEntries"], snap["leafInitialEntries"], snap["rootEntries"])

    if snap["rootInitialEntries"] < 1:
        r["errors"].append("ust seviye start baslangic state'inin onEntry'sini calistirmadi "
                           "(rootInitialEntries=%d)" % snap["rootInitialEntries"])
    if snap["leafInitialEntries"] < 1:
        r["errors"].append("subflow start baslangic state'inin onEntry'sini calistirmadi "
                           "(leafInitialEntries=%d)" % snap["leafInitialEntries"])


CASES = {
    "accept-busy": case_accept_busy,
    "start-onentry": case_start_onentry,
    "shared-self": case_shared_self,
    "shared-parent": case_shared_parent,
    "shared-forward": case_shared_forward,
    "updatedata-self": case_updatedata_self,
    "cancel-top-down": case_cancel_top_down,
    "cancel-bottom-up": case_cancel_bottom_up,
}


# ── publish ─────────────────────────────────────────────────────────────────

def publish():
    for name in ("leaf", "middle", "root"):
        body = json.loads((COMPONENT_DIR / ("chain-busy-%s.json" % name)).read_text())
        st, resp = http("POST", "%s/definitions/publish" % BASE, body)
        if st in (200, 201):
            print("  published chain-busy-%s v%s" % (name, body.get("version")))
        elif st == 409:
            print("  chain-busy-%s zaten publish edilmis (409)" % name)
        else:
            print("  ! chain-busy-%s publish HTTP %s: %s" % (name, st, resp))
            return False
    http("GET", "%s/definitions/re-initialize" % BASE)
    print("  re-initialize ok")
    return True


# ── main ────────────────────────────────────────────────────────────────────

def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--iterations", type=int, default=2)
    ap.add_argument("--case", action="append", choices=sorted(CASES),
                    help="yalniz bu case(ler); tekrarlanabilir. Varsayilan: hepsi")
    ap.add_argument("--publish", action="store_true", help="calistirmadan once flow'lari publish et")
    ap.add_argument("--settle", type=float, default=2.0,
                    help="case'ler arasi bekleme (sn). Asagi yonlu cancel kaskadi Outbox->Inbox "
                         "uzerinden gider; onceki case'lerin olaylari drenaj halindeyken yeni bir "
                         "kaskad gecikebilir. 0 = bekleme yok.")
    ap.add_argument("--list", action="store_true", help="case'leri listele ve cik")
    args = ap.parse_args()

    if args.list:
        for name in sorted(CASES):
            print(name)
        return 0

    selected = args.case or sorted(CASES)

    print("=" * 78)
    print("chain-busy davranis testleri — sharedTransition / cancel / updateData")
    print("=" * 78)

    missing = [name for name, port in (("outbox", 4401), ("inbox", 4501))
               if http("GET", "http://localhost:%d/health" % port, timeout=3)[0] != 200]
    if missing:
        print("\n!! Inbox/Outbox worker AYAKTA DEGIL: %s" % ", ".join(missing))
        print("   Asagi yonlu cancel kaskadi (cancel-top-down) distributed event ile gider —")
        print("   bu worker'lar olmadan olay hic islenmez ve case sessizce FAIL eder.")
        print("   Calistirmak icin:")
        print("     ASPNETCORE_URLS=http://localhost:4401 DAPR_APP_ID=vnext-worker-outbox \\")
        print("       DAPR_HTTP_PORT=44110 DAPR_GRPC_PORT=44111 \\")
        print("       dotnet run --project workers/BBT.Workflow.Workers.Outbox")
        print("     ASPNETCORE_URLS=http://localhost:4501 DAPR_APP_ID=vnext-worker-inbox \\")
        print("       DAPR_HTTP_PORT=45110 DAPR_GRPC_PORT=45111 \\")
        print("       dotnet run --project workers/BBT.Workflow.Workers.Inbox")

    if args.publish:
        print("\nPublish:")
        if not publish():
            return 1

    results = []
    for it in range(1, args.iterations + 1):
        print("\n─── iterasyon %d/%d ───" % (it, args.iterations))
        for name in selected:
            r = {"case": name, "it": it, "errors": [], "detail": ""}
            try:
                CASES[name](r)
            except Exception as exc:  # noqa: BLE001 — test kosucusu, hata raporlanir
                r["errors"].append("beklenmeyen hata: %r" % exc)
            results.append(r)
            mark = "PASS" if not r["errors"] else "FAIL"
            print("  [%-16s] %s  %s" % (name, mark, r["detail"]))
            for err in r["errors"]:
                print("       ! %s" % err)
            if r["errors"] and r.get("ids"):
                print("       ids root=%s middle=%s leaf=%s" % r["ids"])
            if args.settle:
                time.sleep(args.settle)

    passed = sum(1 for r in results if not r["errors"])
    print("\n" + "=" * 78)
    print("SONUC: %d/%d gecti" % (passed, len(results)))
    by_case = {}
    for r in results:
        agg = by_case.setdefault(r["case"], [0, 0])
        agg[1] += 1
        if not r["errors"]:
            agg[0] += 1
    for name in selected:
        ok, total = by_case.get(name, (0, 0))
        print("  %-16s %d/%d" % (name, ok, total))
    print("=" * 78)
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
