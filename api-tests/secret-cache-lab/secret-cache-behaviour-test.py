#!/usr/bin/env python3
"""
ScriptBase secret cache davranis testi — in-process okuma + TTL sonrasi tazelenme.

    python3 api-tests/secret-cache-lab/secret-cache-behaviour-test.py
    python3 api-tests/secret-cache-lab/secret-cache-behaviour-test.py --ttl 30 --publish

## Dogruladigi davranis

Runtime'daki `ScriptSecretCache` (in-process, TTL'li secret bundle cache) uctan uca olculur.
Script tarafi `ScriptBase.GetSecretAsync` cagirir; olculen sey Vault'a gercekten kac kez
gidildigidir — Vault audit device'i (`/tmp/vault-audit.log`) yer gercegi olarak okunur.

  Faz 1 — ayni transition icinde 3 ardisik okuma:
     - ilk okuma Dapr -> Vault gider (yuzlerce/binlerce mikrosaniye),
     - 2. ve 3. okuma in-process bundle'dan gelir (tek haneli mikrosaniye),
     - Vault audit sayaci SADECE 1 artar.
  Faz 2 — TTL icinde ikinci bir transition:
     - uc okumanin ucu de hizli, Vault audit sayaci HIC artmaz
       (cache request'ler arasi yasiyor; per-request degil, process-wide).
  Faz 3 — Vault'taki deger degistirilir, TTL dolmadan tekrar okunur:
     - ESKI deger doner (bilincli staleness penceresi), audit sayaci artmaz.
  Faz 4 — TTL dolduktan sonra tekrar okunur:
     - YENI deger doner, audit sayaci artar, ilk okuma yine yavastir.

Faz 3 "eski deger" bekler: bu bir bug degil, tasarimin sozlesmesidir —
rotasyon sonrasi bayatlik penceresi en fazla `Scripting:SecretCache:TtlSeconds` kadardir.

## On kosullar

  - docker altyapisi ayakta (`cd etc/docker && ./run-docker.sh`) — ozellikle `vnext-vault`
  - orchestration (4201) + execution (4202) lokal calisiyor (`--launch-profile http`)
  - Vault audit device acik olmali; script yoksa kendisi acar (`-path=probe`).

## Basarisizlik esigi

  - Faz 1'de 2./3. okuma > 500 mikrosaniye  -> cache devrede degil.
  - Faz 1'de audit delta != 1               -> bundle basina tek fetch (single-flight) bozulmus.
  - Faz 2'de audit delta != 0               -> cache request'ler arasi yasamiyor.
  - Faz 3'te deger yeni                     -> TTL beklenenden kisa / cache bypass.
  - Faz 4'te deger eski                     -> TTL dolmasina ragmen tazelenme yok (kalici bayatlik).
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
WF = "secret-cache-lab"
USER = "11111111-1111-1111-1111-111111111111"

VAULT_CONTAINER = "vnext-vault"
VAULT_TOKEN = "admin"
VAULT_PATH = "secret/data/workflow-secret"
SECRET_KEY = "ApiSecret"
AUDIT_FILE = "/tmp/vault-audit.log"

REPO = Path(__file__).resolve().parents[2]
COMPONENTS = [
    REPO / "core/Tasks/secret-cache-lab/secret-probe-script-task.json",
    REPO / "core/Workflows/secret-cache-lab/secret-cache-lab.json",
]

# Faz 1'de "cache hit" sayilan ust sinir (mikrosaniye). Vault round-trip'i lokalde bile
# yuzlerce mikrosaniyedir; sozluk aramasi tek hanelidir. 500 us ikisinin ortasinda genis bir esik; asil kanit Vault audit sayacidir.
HIT_MAX_MICROS = 500.0

RESULTS = []


def check(name, ok, detail=""):
    RESULTS.append((name, ok, detail))
    print(("  PASS  " if ok else "  FAIL  ") + name + (f"  -> {detail}" if detail and not ok else ""))
    return ok


def http(method, url, body=None, timeout=60):
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
    except Exception as e:  # noqa: BLE001 - test harness, hata mesaji yeter
        return -1, {"error": str(e)}


# --------------------------------------------------------------------------- Vault


def vault(*args, timeout=20):
    """Vault CLI'yi container icinde calistirir."""
    cmd = ["docker", "exec", VAULT_CONTAINER, "sh", "-c",
           f"VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN={VAULT_TOKEN} vault " + " ".join(args)]
    out = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    return out.returncode, out.stdout.strip(), out.stderr.strip()


def ensure_audit():
    rc, out, _ = vault("audit", "list")
    if rc == 0 and "probe/" in out:
        return True
    rc, out, err = vault("audit", "enable", "-path=probe", "file", f"file_path={AUDIT_FILE}")
    if rc != 0 and "already in use" not in err:
        print(f"  ! vault audit acilamadi: {err or out}")
        return False
    return True


def audit_reads():
    """workflow-secret bundle'ina yapilan Vault READ istegi sayisi (yer gercegi)."""
    cmd = ["docker", "exec", VAULT_CONTAINER, "sh", "-c",
           f"grep -c '\"type\":\"request\".*\"operation\":\"read\".*\"path\":\"{VAULT_PATH}\"' {AUDIT_FILE} || true"]
    out = subprocess.run(cmd, capture_output=True, text=True, timeout=20)
    try:
        return int(out.stdout.strip() or 0)
    except ValueError:
        return 0


def vault_read():
    rc, out, _ = vault("kv", "get", "-format=json", "-mount=secret", "workflow-secret")
    if rc != 0:
        return None
    try:
        return json.loads(out)["data"]["data"].get(SECRET_KEY)
    except (KeyError, json.JSONDecodeError):
        return None


def vault_write(value):
    rc, out, err = vault("kv", "put", "-mount=secret", "workflow-secret", f"{SECRET_KEY}={value}")
    if rc != 0:
        print(f"  ! vault write basarisiz: {err or out}")
        return False
    return True


# --------------------------------------------------------------------------- Runtime


def publish():
    for path in COMPONENTS:
        body = json.loads(path.read_text())
        st, resp = http("POST", f"{BASE}/definitions/publish", body)
        if st in (200, 201):
            print(f"  published {path.name}")
        elif st == 409:
            print(f"  {path.name} zaten publish edilmis (409)")
        else:
            print(f"  ! {path.name} publish HTTP {st}: {str(resp)[:300]}")
            return False
    return True


def start_instance():
    st, body = http("POST", f"{BASE}/{DOMAIN}/workflows/{WF}/instances/start",
                    {"testId": f"secretlab-{uuid.uuid4().hex[:8]}"})
    if st not in (200, 201, 202):
        print(f"  ! start HTTP {st}: {str(body)[:300]}")
        return None
    return body.get("id") or body.get("Id")


def probe(instance_id):
    """probe-secret transition'ini sync kosturur; instance data'daki olcumu dondurur.

    sync=true cevabi instance'in guncel `attributes`'ini tasir — ayri bir data function
    cagrisina gerek yok (ve boylece olcum ile okuma arasina baska bir istek girmez).
    """
    st, body = http("PATCH",
                    f"{BASE}/{DOMAIN}/workflows/{WF}/instances/{instance_id}/transitions/probe-secret?sync=true",
                    {})
    if st not in (200, 201, 202):
        return None, f"transition HTTP {st}: {str(body)[:300]}"

    data = body.get("attributes") or body.get("Attributes") or {}
    if not isinstance(data, dict) or "microsPerRead" not in data:
        return None, f"instance data beklenen alanlari tasimiyor: {str(body)[:300]}"
    return data, None


def summarize(tag, data, audit_delta):
    micros = [float(x) for x in data.get("microsPerRead", [])]
    print(f"  [{tag}] value={data.get('secretValue')!r} "
          f"microsPerRead={micros} vaultReadDelta={audit_delta}")
    return micros


# --------------------------------------------------------------------------- Test


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ttl", type=int, default=30,
                    help="Runtime'daki Scripting:SecretCache:TtlSeconds degeri (varsayilan 30)")
    ap.add_argument("--publish", action="store_true", help="Bilesenleri once publish et")
    ap.add_argument("--keep-secret", action="store_true",
                    help="Test sonunda Vault'taki orijinal degeri geri yazma")
    args = ap.parse_args()

    print("== On kosullar")
    if not ensure_audit():
        return 1
    original = vault_read()
    if original is None:
        print("  ! Vault'tan workflow-secret okunamadi")
        return 1
    print(f"  vault audit acik, mevcut {SECRET_KEY}={original!r}")

    if args.publish and not publish():
        return 1

    v1 = f"SECRET-CACHE-LAB-V1-{uuid.uuid4().hex[:6]}"
    v2 = f"SECRET-CACHE-LAB-V2-{uuid.uuid4().hex[:6]}"

    if not vault_write(v1):
        return 1
    print(f"  baslangic degeri yazildi: {v1}")

    instance = start_instance()
    if not instance:
        return 1
    print(f"  instance: {instance}")

    # TTL'i sifirla: baslangicta cache'te onceki testten kalan bir bundle olabilir.
    print(f"\n== TTL penceresi temizleniyor ({args.ttl + 2} sn bekleniyor)")
    time.sleep(args.ttl + 2)

    # --- Faz 1: soguk okuma + ayni transition icinde iki sicak okuma
    print("\n== Faz 1: soguk okuma + ayni transition icinde iki sicak okuma")
    before = audit_reads()
    fetch_started = time.time()
    data, err = probe(instance)
    if err:
        print(f"  ! probe basarisiz: {err}")
        return 1
    delta1 = audit_reads() - before
    micros = summarize("faz1", data, delta1)

    check("Faz 1: Vault'a tam olarak 1 kez gidildi (bundle basina tek fetch)",
          delta1 == 1, f"delta={delta1}")
    check("Faz 1: ilk okuma Vault round-trip'i (>= 500 us)",
          len(micros) == 3 and micros[0] >= HIT_MAX_MICROS, f"micros[0]={micros[0] if micros else '?'}")
    check(f"Faz 1: 2. ve 3. okuma in-memory (< {HIT_MAX_MICROS:.0f} us)",
          len(micros) == 3 and max(micros[1:]) < HIT_MAX_MICROS, f"micros[1:]={micros[1:]}")
    check("Faz 1: sicak okuma soguk okumadan en az 5x hizli",
          len(micros) == 3 and micros[0] > 5 * max(micros[1:]),
          f"{micros[0]} vs {max(micros[1:]) if len(micros) == 3 else '?'}")
    check("Faz 1: okunan deger Vault'taki deger", data.get("secretValue") == v1,
          f"{data.get('secretValue')!r} != {v1!r}")

    # --- Faz 2: TTL icinde ikinci transition — cache request'ler arasi yasiyor mu
    print("\n== Faz 2: TTL icinde ikinci transition (process-wide cache)")
    before = audit_reads()
    data, err = probe(instance)
    if err:
        print(f"  ! probe basarisiz: {err}")
        return 1
    delta2 = audit_reads() - before
    micros = summarize("faz2", data, delta2)

    check("Faz 2: Vault'a HIC gidilmedi (cache request'ler arasi yasiyor)",
          delta2 == 0, f"delta={delta2}")
    check(f"Faz 2: uc okuma da in-memory (< {HIT_MAX_MICROS:.0f} us)",
          len(micros) == 3 and max(micros) < HIT_MAX_MICROS, f"micros={micros}")

    # --- Faz 3: Vault'ta rotasyon, TTL dolmadan okuma -> bilincli staleness
    print("\n== Faz 3: Vault'ta rotasyon, TTL dolmadan okuma (bilincli staleness)")
    if not vault_write(v2):
        return 1
    print(f"  vault degeri degistirildi: {v1} -> {v2}")
    before = audit_reads()
    data, err = probe(instance)
    if err:
        print(f"  ! probe basarisiz: {err}")
        return 1
    delta3 = audit_reads() - before
    summarize("faz3", data, delta3)

    check("Faz 3: TTL icinde ESKI deger donuyor (staleness penceresi)",
          data.get("secretValue") == v1, f"{data.get('secretValue')!r} != {v1!r}")
    check("Faz 3: Vault'a gidilmedi", delta3 == 0, f"delta={delta3}")

    # --- Faz 4: TTL dolduktan sonra -> canli deger
    wait_s = max(0.0, (fetch_started + args.ttl + 3) - time.time())
    print(f"\n== Faz 4: TTL doluyor ({wait_s:.1f} sn bekleniyor), sonra tekrar okuma")
    time.sleep(wait_s)
    before = audit_reads()
    data, err = probe(instance)
    if err:
        print(f"  ! probe basarisiz: {err}")
        return 1
    delta4 = audit_reads() - before
    micros = summarize("faz4", data, delta4)

    check("Faz 4: TTL dolunca YENI (canli) deger geliyor",
          data.get("secretValue") == v2, f"{data.get('secretValue')!r} != {v2!r}")
    check("Faz 4: Vault'a tekrar 1 kez gidildi", delta4 == 1, f"delta={delta4}")
    check("Faz 4: ilk okuma yine Vault round-trip'i",
          len(micros) == 3 and micros[0] >= HIT_MAX_MICROS, f"micros[0]={micros[0] if micros else '?'}")

    if not args.keep_secret:
        vault_write(original)
        print(f"\n  Vault orijinal degere geri alindi: {original!r}")

    passed = sum(1 for _, ok, _ in RESULTS if ok)
    print(f"\n== Sonuc: {passed}/{len(RESULTS)} gecti")
    for name, ok, detail in RESULTS:
        if not ok:
            print(f"   FAIL {name} -> {detail}")
    return 0 if passed == len(RESULTS) else 1


if __name__ == "__main__":
    sys.exit(main())
