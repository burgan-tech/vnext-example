#!/usr/bin/env python3
"""
script-race-lab bilesenlerini lokal runtime'a publish eder ve cache'i yeniler.

    python3 api-tests/script-race-lab/publish.py

Sira ONEMLI: helper -> child -> parent. Parent'in `scripts.helpers` referansi ile subFlow
`process` referansi publish aninda cozulur; tersi sirada referans bulunamaz.

Integration suite bunu KENDISI yapar (VNextTestEnvironment.EnableDomainPublish). Bu script
JMeter kosulari ve elle dogrulama icindir.
"""

import argparse
import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path

DEFAULT_BASE_URL = os.environ.get("VNEXT_BASE_URL", "http://localhost:4201").rstrip("/")
BASE = DEFAULT_BASE_URL + "/api/v1"  # main() icinde --base-url ile ezilir
REPO = Path(__file__).resolve().parents[2]

COMPONENTS = [
    REPO / "core" / "Mappings" / "script-race-lab" / "race-helper.json",
    REPO / "core" / "Workflows" / "script-race-lab" / "script-race-lab-child.json",
    REPO / "core" / "Workflows" / "script-race-lab" / "script-race-lab-parent.json",
]


def http(method, url, body=None):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(url, data=data, method=method,
                                     headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.status, response.read().decode()
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode()


def main(argv=None, base_url=None):
    """Standalone: sys.argv parse eder. In-process cagiran (race-load/perf-load) argv=[] ve base_url verir."""
    global BASE
    ap = argparse.ArgumentParser(description="bilesenleri publish et")
    ap.add_argument("--base-url", default=DEFAULT_BASE_URL,
                    help="orchestrator base URL (varsayilan: VNEXT_BASE_URL ortam degiskeni ya da http://localhost:4201)")
    args = ap.parse_args(argv)
    BASE = (base_url or args.base_url).rstrip("/") + "/api/v1"
    for path in COMPONENTS:
        document = json.loads(path.read_text())
        status, response = http("POST", "%s/definitions/publish" % BASE, document)
        if status in (200, 201):
            print("  published %s v%s" % (document["key"], document["version"]))
        elif status == 409:
            print("  %s zaten publish edilmis (409)" % document["key"])
        else:
            print("  ! %s publish HTTP %s: %s" % (document["key"], status, response))
            return 1

    http("GET", "%s/definitions/re-initialize" % BASE)
    print("  re-initialize ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
