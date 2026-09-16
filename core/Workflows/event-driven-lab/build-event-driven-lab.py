#!/usr/bin/env python3
"""Regenerates event-driven-lab.json from the .csx sources in ./src.

Every mapping is embedded as base64 in `code` next to its `location`; edit the .csx files and
re-run this script — never hand-edit the base64 blobs.

    python3 core/Workflows/event-driven-lab/build-event-driven-lab.py
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent


def code(name):
    raw = (ROOT / "src" / name).read_bytes()
    return {"location": f"./src/{name}", "code": base64.b64encode(raw).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


SCRIPT_TASK = {
    "key": "event-lab-script-task",
    "domain": "core",
    "version": "1.0.0",
    "flow": "sys-tasks",
}

workflow = {
    "key": "event-driven-lab",
    "flow": "sys-flows",
    "flowVersion": "1.0.0",
    "domain": "core",
    # Publishing is version-immutable: the same version with different content is refused with
    # Instance:100002. Every content change here needs a patch bump.
    "version": "1.0.1",
    "tags": ["integration-test", "event-driven-lab", "event", "pubsub", "tracing"],
    "attributes": {
        "type": "F",
        "labels": label("Event Driven Lab"),
        "functions": [],
        "extensions": [],
        # Workflow-level event mapping = the action=start subscription's entry point.
        "event": {"mapping": code("StartEventMapping.csx")},
        "startTransition": {
            "key": "start-event-driven-lab",
            "target": "awaiting-approval",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start Event Driven Lab"),
        },
        "states": [
            {
                "key": "awaiting-approval",
                "stateType": 1,
                "subType": 0,
                "versionStrategy": "Major",
                "labels": label("Awaiting Approval"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [
                    {
                        "key": "approve-by-event",
                        "target": "approved",
                        # 3 = Event. Only an event delivery may drive this transition.
                        "triggerType": 3,
                        "versionStrategy": "Minor",
                        "labels": label("Approve By Event"),
                        "event": {"mapping": code("ApproveEventMapping.csx")},
                        "onExecutionTasks": [
                            {
                                "order": 1,
                                "task": SCRIPT_TASK,
                                "mapping": code("RecordApprovalMapping.csx"),
                            }
                        ],
                    }
                ],
            },
            {
                "key": "approved",
                "stateType": 3,
                "subType": 1,
                "versionStrategy": "Major",
                "labels": label("Approved"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [],
            },
        ],
    },
}

out = ROOT / "event-driven-lab.json"
out.write_text(json.dumps(workflow, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print(f"wrote {out}")
