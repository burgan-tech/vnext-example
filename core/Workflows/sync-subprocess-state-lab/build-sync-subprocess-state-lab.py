#!/usr/bin/env python3
"""Regenerates the sync-subprocess-state-lab workflow JSON files (vnext#1182).

* ssp-child — born in `ssp-received`, a default automatic transition moves it on to
  `ssp-otp-waiting`, where it rests Active until `approve` takes it to `ssp-approved` (Finish).
  The auto hop makes the rest point differ from the birth state, so a response that reported
  the wrong one would show.
* ssp-parent — its Initial state `spawning` starts ssp-child through a SYNC SubProcess task
  (`ssp-start-child`, type 14) and records what the start response says about the child
  (id, currentState, effectiveState); a default automatic transition moves it to `spawned`.

Edit ./src/*.csx and re-run — never hand-edit the base64 blob.

    python3 core/Workflows/sync-subprocess-state-lab/build-sync-subprocess-state-lab.py
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


def transition(key, target, trigger_type, text):
    t = {
        "key": key,
        "target": target,
        "triggerType": trigger_type,
        "versionStrategy": "Minor",
        "labels": label(text),
    }
    if trigger_type == 1:
        t["triggerKind"] = 10  # default automatic transition: no rule, fires once the state settles
    return t


def state(key, state_type, sub_type, text, transitions=None, on_entries=None):
    return {
        "key": key,
        "stateType": state_type,
        "subType": sub_type,
        "versionStrategy": "Major",
        "labels": label(text),
        "view": None,
        "subFlow": None,
        "onEntries": on_entries or [],
        "onExits": [],
        "transitions": transitions or [],
    }


def envelope(key, tags, text, states):
    return {
        "key": key,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": VERSION,
        "tags": ["integration-test", "sync-subprocess-state-lab", *tags],
        "attributes": {
            "type": "F",
            "timeout": None,
            "labels": label(text),
            "functions": [],
            "features": [],
            "extensions": [],
            "startTransition": {
                "key": "start",
                "target": states[0]["key"],
                "triggerType": 0,
                "versionStrategy": "Major",
                "labels": label("Start"),
            },
            "states": states,
        },
    }


child = envelope(
    "ssp-child",
    ["child"],
    "Sync SubProcess State Lab child",
    [
        state("ssp-received", 1, 0, "Received",
              [transition("auto-otp", "ssp-otp-waiting", 1, "Auto to OTP waiting")]),
        state("ssp-otp-waiting", 2, 0, "OTP waiting",
              [transition("approve", "ssp-approved", 0, "Approve")]),
        state("ssp-approved", 3, 1, "Approved"),
    ],
)

parent = envelope(
    "ssp-parent",
    ["parent", "subprocess"],
    "Sync SubProcess State Lab parent",
    [
        state("spawning", 1, 0, "Spawning (sync SubProcess start)",
              [transition("auto-spawned", "spawned", 1, "Auto to spawned")],
              on_entries=[{
                  "order": 1,
                  "task": {"key": "ssp-start-child", "domain": "core", "flow": "sys-tasks", "version": "1.0.0"},
                  "mapping": code("SspStartChildMapping.csx"),
              }]),
        state("spawned", 2, 0, "Spawned (waits Active)"),
    ],
)

for flow in (child, parent):
    (ROOT / f"{flow['key']}.json").write_text(json.dumps(flow, indent=2, ensure_ascii=False) + "\n")
