#!/usr/bin/env python3
"""
script-race-lab bilesenlerini lokal runtime'a publish eder ve cache'i yeniler.

    python3 api-tests/script-race-lab/publish.py

Sira ONEMLI: helper -> child -> parent. Parent'in `scripts.helpers` referansi ile subFlow
`process` referansi publish aninda cozulur; tersi sirada referans bulunamaz.

Integration suite bunu KENDISI yapar (VNextTestEnvironment.EnableDomainPublish). Bu script
JMeter kosulari ve elle dogrulama icindir.
"""

import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

BASE = "http://localhost:4201/api/v1"
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


def main():
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
