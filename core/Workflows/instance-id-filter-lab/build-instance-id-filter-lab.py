#!/usr/bin/env python3
"""Regenerates the instance-id-filter-lab workflow JSON files (vnext#1080).

Two flows pin filtering the instance list by the uuid `id` column:

* idf-target — the population. Born in `idle` (Active) and never moves; the test reads it by id.
* idf-reader — `ready` carries a same-state transition `read-list` whose OnExecute GetInstances task takes
  a raw filter from the transition body (IdfReaderListMapping) and records the outcome as `readerList`.
  No error boundary on purpose: a rejected filter is recorded, not faulted.

Edit ./src/*.csx and re-run — never hand-edit the base64 blob.

    python3 core/Workflows/instance-id-filter-lab/build-instance-id-filter-lab.py
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
VERSION = "1.0.0"


def code(name):
    raw = (ROOT / "src" / name).read_bytes()
    return {"location": f"./src/{name}", "code": base64.b64encode(raw).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def state(key, state_type, sub_type, text, transitions=None):
    return {
        "key": key,
        "stateType": state_type,
        "subType": sub_type,
        "versionStrategy": "Major",
        "labels": label(text),
        "view": None,
        "subFlow": None,
        "onEntries": [],
        "onExits": [],
        "transitions": transitions or [],
    }


def envelope(key, tags, text, start_target, states):
    return {
        "key": key,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": VERSION,
        "tags": ["integration-test", "instance-id-filter-lab"] + tags,
        "attributes": {
            "type": "F",
            "timeout": None,
            "labels": label(text),
            "functions": [],
            "features": [],
            "extensions": [],
            "startTransition": {
                "key": "start",
                "target": start_target,
                "triggerType": 0,
                "versionStrategy": "Major",
                "labels": label("Start"),
            },
            "states": states,
        },
    }


target = envelope(
    "idf-target", ["list-target"], "Instance Id Filter Lab target (rests in idle)", "idle",
    [state("idle", 1, 0, "Idle")],
)

read_list = {
    "key": "read-list",
    "target": "ready",
    "triggerType": 0,
    "versionStrategy": "Minor",
    "labels": label("GetInstances with the body's raw filter"),
    "onExecutionTasks": [
        {
            "order": 1,
            "task": {"key": "idf-get-instances", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"},
            "mapping": code("IdfReaderListMapping.csx"),
        }
    ],
}

reader = envelope(
    "idf-reader", ["get-instances"], "Instance Id Filter Lab reader (GetInstances SetFilter)", "ready",
    [state("ready", 1, 0, "Ready", [read_list])],
)

for wf in (target, reader):
    (ROOT / f"{wf['key']}.json").write_text(json.dumps(wf, indent=2, ensure_ascii=False) + "\n")
    print(f"wrote {wf['key']}.json")
