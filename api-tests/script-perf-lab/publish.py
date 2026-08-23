#!/usr/bin/env python3
"""
script-perf-lab bilesenlerini lokal runtime'a publish eder ve cache'i yeniler.

    python3 api-tests/script-perf-lab/publish.py

Sira ONEMLI: helper -> task -> workflow. Workflow'un `scripts.helpers` referanslari ve
`perf-fanout` state'inin `script-perf-fanout-task` / `perf-item-http-task` referanslari
publish aninda cozulur; tersi sirada referans bulunamaz.

`definitions/publish` ayni key+version'i icerik degisse de 409 ile reddeder — script-perf-lab
JSON'lari `build-script-perf-lab.py --nonce N` ile taze bir versiyona (`1.0.<nonce>`) uretilmedikce
bu script mevcut publish edilmis bilesenleri 409 ile atlar (soguk olcum icin bkz. asagisi).

Integration suite bunu KENDISI yapar (VNextTestEnvironment.EnableDomainPublish). Bu script
elle calistirma ve perf-load.py'nin --publish bayragi icindir.
"""

import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

BASE = "http://localhost:4201/api/v1"
REPO = Path(__file__).resolve().parents[2]

COMPONENTS = [
    REPO / "core" / "Mappings" / "script-perf-lab" / "perf-chunk-helper.json",
    REPO / "core" / "Mappings" / "script-perf-lab" / "perf-stamp-helper.json",
    REPO / "core" / "Tasks" / "script-perf-lab" / "script-perf-task.json",
    REPO / "core" / "Tasks" / "script-perf-lab" / "perf-item-http-task.json",
    REPO / "core" / "Tasks" / "script-perf-lab" / "script-perf-fanout-task.json",
    REPO / "core" / "Workflows" / "script-perf-lab" / "script-perf-lab.json",
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
