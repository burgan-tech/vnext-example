#!/usr/bin/env python3
"""Regenerates the data-etag-xd components (vnext#1084, cross-domain) from their .csx sources.

    python3 core/Workflows/data-etag-xd/build-data-etag-xd.py

Writes the core parent next to this script and the partner child + extension under partner/.
Plan: labs/cross-domain/VNEXT-BUILD-PLAN-data-etag-xd.md. Never hand-edit the base64 blobs.
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent.parent.parent
PARTNER_WF = REPO / "partner" / "Workflows" / "data-etag-xd"
PARTNER_EXT = REPO / "partner" / "Extensions" / "data-etag-xd"
VERSION = "1.0.0"


def code(base, name):
    raw = (base / "src" / name).read_bytes()
    return {"location": f"./src/{name}", "code": base64.b64encode(raw).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def ref(key, domain, flow, version=VERSION):
    return {"key": key, "domain": domain, "version": version, "flow": flow}


def state(key, text, state_type, transitions=(), sub_flow=None, sub_type=0):
    return {
        "key": key, "stateType": state_type, "subType": sub_type, "versionStrategy": "Major",
        "labels": label(text), "view": None, "subFlow": sub_flow,
        "onEntries": [], "onExits": [], "transitions": list(transitions),
    }


def manual(key, target, text):
    return {"key": key, "target": target, "triggerType": 0, "versionStrategy": "Minor",
            "labels": label(text), "onExecutionTasks": []}


extension = {
    "key": "de-xd-otp-input",
    "version": VERSION,
    "domain": "partner",
    "flow": "sys-extensions",
    "flowVersion": "1.0.0",
    "tags": ["cross-domain-lab", "data-etag-xd", "partner"],
    "attributes": {
        "type": 3,
        "scope": 1,
        "task": {
            "order": 1,
            "task": ref("xd-script-task", "partner", "sys-tasks"),
            "mapping": code(PARTNER_EXT, "DeXdOtpInputMapping.csx"),
        },
    },
}

child = {
    "key": "de-xd-child",
    "flow": "sys-flows",
    "flowVersion": "1.0.0",
    "domain": "partner",
    "version": VERSION,
    "tags": ["cross-domain-lab", "data-etag-xd", "partner", "child"],
    "attributes": {
        "type": "S",
        "timeout": None,
        "labels": label("Data ETag XD — partner child (OTP screen)"),
        "functions": [],
        "features": [],
        "extensions": [ref("de-xd-otp-input", "partner", "sys-extensions")],
        "sharedTransitions": [],
        "startTransition": {
            "key": "start-de-xd-child", "target": "otp", "triggerType": 0,
            "versionStrategy": "Major", "labels": label("Start child"),
        },
        "states": [
            # `wrong-otp` writes data and lands back in `otp`: the child's state (and the parent's
            # effective state) never moves, only the child's data does.
            state("otp", "OTP entry", 1, [
                manual("wrong-otp", "otp", "Wrong OTP (writes data, same state)"),
                manual("approve-otp", "otp-done", "Approve OTP"),
            ]),
            state("otp-done", "OTP done", 3, sub_type=1),
        ],
    },
}

parent = {
    "key": "de-xd-parent",
    "flow": "sys-flows",
    "flowVersion": "1.0.0",
    "domain": "core",
    "version": VERSION,
    "tags": ["cross-domain-lab", "data-etag-xd", "parent"],
    "attributes": {
        "type": "F",
        "timeout": None,
        "labels": label("Data ETag XD — core parent (waits on the partner OTP child)"),
        "functions": [],
        "features": [],
        "extensions": [],
        "sharedTransitions": [],
        "startTransition": {
            "key": "start-de-xd-parent", "target": "begin", "triggerType": 0,
            "versionStrategy": "Major", "labels": label("Start parent"),
        },
        "states": [
            state("begin", "Begin", 1, [{
                "key": "to-wait-otp", "target": "wait-otp", "triggerType": 1, "triggerKind": 10,
                "versionStrategy": "Minor", "labels": label("Enter the SubFlow state"), "onExecutionTasks": [],
            }]),
            state("wait-otp", "Waits on the partner OTP child", 4, sub_flow={
                "type": "S",
                "process": ref("de-xd-child", "partner", "sys-flows"),
                "mapping": code(ROOT, "DeXdSubFlowMapping.csx"),
            }),
            state("parent-done", "Done", 3, sub_type=1),
        ],
    },
}


def write(path, component):
    path.write_text(json.dumps(component, indent=2, ensure_ascii=False) + "\n")


write(PARTNER_EXT / "de-xd-otp-input.json", extension)
write(PARTNER_WF / "de-xd-child.json", child)
write(ROOT / "de-xd-parent.json", parent)
