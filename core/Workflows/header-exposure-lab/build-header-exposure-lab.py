#!/usr/bin/env python3
"""Regenerates the header-exposure-lab workflow JSON (vnext#1260, Workflow:HeaderExposure:Deny).

One flow, one state. The start transition and the `echo-headers` same-state transition both run EchoHeadersMapping,
which records the request header names the mapping can see (`startHeaderNames` / `echoHeaderNames`) and whether the
probe header `x-exposure-probe` and the `role` header were among them. `echo-headers` is role-gated (allow maker) so
`authorize?transitionKey=echo-headers` shows the runtime still resolves roles from a header the mapping cannot see.

Edit ./src/*.csx and re-run — never hand-edit the base64 blob.

    python3 core/Workflows/header-exposure-lab/build-header-exposure-lab.py
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
VERSION = "1.0.0"
TASK = {"key": "role-matrix-exec-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}


def code(name):
    raw = (ROOT / "src" / name).read_bytes()
    return {"location": f"./src/{name}", "code": base64.b64encode(raw).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def echo_task():
    return [{"order": 1, "task": TASK, "mapping": code("EchoHeadersMapping.csx")}]


echo = {
    "key": "echo-headers",
    "target": "open",
    "triggerType": 0,
    "versionStrategy": "Minor",
    "labels": label("Record the header names the mapping sees"),
    "roles": [{"role": "morph-idm.maker", "grant": "allow"}],
    "onExecutionTasks": echo_task(),
}

workflow = {
    "key": "header-exposure-lab",
    "flow": "sys-flows",
    "flowVersion": "1.0.0",
    "domain": "core",
    "version": VERSION,
    "tags": ["integration-test", "header-exposure-lab"],
    "attributes": {
        "type": "F",
        "timeout": None,
        "labels": label("Header Exposure Lab (Workflow:HeaderExposure:Deny)"),
        "functions": [],
        "features": [],
        "extensions": [],
        "startTransition": {
            "key": "start",
            "target": "open",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start"),
            "onExecutionTasks": echo_task(),
        },
        "states": [
            {
                "key": "open",
                "stateType": 1,
                "subType": 0,
                "versionStrategy": "Major",
                "labels": label("Open"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [echo],
            }
        ],
    },
}

(ROOT / "header-exposure-lab.json").write_text(json.dumps(workflow, indent=2, ensure_ascii=False) + "\n")
print("wrote header-exposure-lab.json")
