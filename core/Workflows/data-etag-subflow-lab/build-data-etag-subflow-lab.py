#!/usr/bin/env python3
"""Regenerates the data-etag-subflow-lab components from the .csx sources (vnext#1084).

    python3 core/Workflows/data-etag-subflow-lab/build-data-etag-subflow-lab.py

Writes the parent and child workflows next to this script and the extension under
core/Extensions/data-etag-subflow-lab/. Every script is embedded as base64 next to its `location`;
edit the .csx files and re-run — never hand-edit the base64 blobs.
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
EXT_ROOT = ROOT.parent.parent / "Extensions" / "data-etag-subflow-lab"
VERSION = "1.0.0"


def code(base, name):
    raw = (base / "src" / name).read_bytes()
    return {"location": f"./src/{name}", "code": base64.b64encode(raw).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def ref(key, flow, version=VERSION):
    return {"key": key, "domain": "core", "version": version, "flow": flow}


def state(key, text, state_type, transitions=(), sub_flow=None, sub_type=0):
    return {
        "key": key,
        "stateType": state_type,
        "subType": sub_type,
        "versionStrategy": "Major",
        "labels": label(text),
        "view": None,
        "subFlow": sub_flow,
        "onEntries": [],
        "onExits": [],
        "transitions": list(transitions),
    }


def manual(key, target, text):
    return {
        "key": key,
        "target": target,
        "triggerType": 0,
        "versionStrategy": "Minor",
        "labels": label(text),
        "onExecutionTasks": [],
    }


extension = {
    "key": "data-etag-subflow-lab-otp-input",
    "version": VERSION,
    "domain": "core",
    "flow": "sys-extensions",
    "flowVersion": "1.0.0",
    "tags": ["integration-test", "data-etag-subflow-lab"],
    "attributes": {
        "type": 3,
        "scope": 1,
        "task": {
            "order": 1,
            "task": ref("script-task", "sys-tasks"),
            "mapping": code(EXT_ROOT, "OtpInputExtensionMapping.csx"),
        },
    },
}

child = {
    "key": "data-etag-subflow-lab-child",
    "flow": "sys-flows",
    "flowVersion": "1.0.0",
    "domain": "core",
    "version": VERSION,
    "tags": ["integration-test", "data-etag-subflow-lab", "child"],
    "attributes": {
        "type": "S",
        "timeout": None,
        "labels": label("Data ETag SubFlow Lab — child (OTP screen)"),
        "functions": [],
        "features": [],
        "extensions": [ref("data-etag-subflow-lab-otp-input", "sys-extensions")],
        "sharedTransitions": [],
        "startTransition": {
            "key": "start-data-etag-subflow-lab-child",
            "target": "otp",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start child"),
        },
        "states": [
            # The whole scenario: `wrong-otp` writes data and lands back in `otp`. The child's state —
            # and so the parent's effective state — never moves; only the child's data does.
            state("otp", "OTP entry", 1, [
                manual("wrong-otp", "otp", "Wrong OTP (writes data, same state)"),
                manual("approve-otp", "otp-done", "Approve OTP"),
            ]),
            state("otp-done", "OTP done", 3, sub_type=1),
        ],
    },
}

parent = {
    "key": "data-etag-subflow-lab",
    "flow": "sys-flows",
    "flowVersion": "1.0.0",
    "domain": "core",
    "version": VERSION,
    "tags": ["integration-test", "data-etag-subflow-lab", "parent"],
    "attributes": {
        "type": "F",
        "timeout": None,
        "labels": label("Data ETag SubFlow Lab — parent (waits on the OTP child)"),
        "functions": [],
        "features": [],
        "extensions": [],
        "sharedTransitions": [],
        "startTransition": {
            "key": "start-data-etag-subflow-lab",
            "target": "begin",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start parent"),
        },
        "states": [
            state("begin", "Begin", 1, [{
                "key": "to-wait-otp",
                "target": "wait-otp",
                "triggerType": 1,
                "triggerKind": 10,
                "versionStrategy": "Minor",
                "labels": label("Enter the SubFlow state"),
                "onExecutionTasks": [],
            }]),
            state("wait-otp", "Waits on the OTP child", 4, sub_flow={
                "type": "S",
                "process": ref("data-etag-subflow-lab-child", "sys-flows"),
                "mapping": code(ROOT, "PassThroughSubFlowMapping.csx"),
            }),
            state("parent-done", "Done", 3, sub_type=1),
        ],
    },
}


def write(path, component):
    path.write_text(json.dumps(component, indent=2, ensure_ascii=False) + "\n")


write(EXT_ROOT / "data-etag-subflow-lab-otp-input.json", extension)
write(ROOT / "data-etag-subflow-lab-child.json", child)
write(ROOT / "data-etag-subflow-lab.json", parent)
