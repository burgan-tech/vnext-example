#!/usr/bin/env python3
"""Regenerates data-integrity-lab.json from the .csx sources in ./src.

The workflow JSON embeds every mapping/rule as base64 in `code` next to its
`location`; edit the .csx files and re-run this script — never hand-edit the
base64 blobs.
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent


def code(name):
    raw = (ROOT / "src" / name).read_bytes()
    return {
        "location": f"./src/{name}",
        "code": base64.b64encode(raw).decode(),
    }


def script_task():
    return {"key": "lab-script-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}


def probe_task(k):
    # One task definition PER parallel branch: the task-journal ExecutionKey is derived from
    # (transition, task, order) — same-order branches sharing one task key collide on
    # UX_InstanceTasks_ExecutionKey and fault the transition.
    return {"key": f"lab-probe-task-{k}", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}


def label(text):
    return [{"language": "en-US", "label": text}]


workflow = {
    "key": "data-integrity-lab",
    "flow": "sys-flows",
    "flowVersion": "1.0.0",
    "domain": "core",
    "version": "1.0.2",
    "tags": [
        "integration-test",
        "data-integrity-lab",
        "sequential-tasks",
        "parallel-tasks",
        "datahash-dedup",
        "lock-contention",
    ],
    "attributes": {
        "type": "F",
        "timeout": None,
        "labels": label("Data Integrity Lab"),
        "functions": [],
        "features": [],
        "extensions": [],
        "startTransition": {
            "key": "start-data-integrity-lab",
            "target": "lab-sequential",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start Data Integrity Lab"),
            "onExecutionTasks": [
                {"order": 1, "task": script_task(), "mapping": code("LabStartMapping.csx")}
            ],
        },
        "states": [
            {
                "key": "lab-sequential",
                "stateType": 1,
                "subType": 0,
                "versionStrategy": "Major",
                "labels": label("Lab Sequential (ordered task chain)"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [
                    {
                        "key": "run-sequential",
                        "target": "lab-parallel",
                        "triggerType": 0,
                        "versionStrategy": "Patch",
                        "labels": label("Run Sequential Chain (seq1..3 + dedup echo)"),
                        "onExecutionTasks": [
                            {"order": 1, "task": script_task(), "mapping": code("LabSeqStep1Mapping.csx")},
                            {"order": 2, "task": script_task(), "mapping": code("LabSeqStep2Mapping.csx")},
                            {"order": 3, "task": script_task(), "mapping": code("LabSeqStep3Mapping.csx")},
                            {"order": 4, "task": script_task(), "mapping": code("LabDupEchoMapping.csx")},
                        ],
                    }
                ],
            },
            {
                "key": "lab-parallel",
                "stateType": 2,
                "subType": 0,
                "versionStrategy": "Minor",
                "labels": label("Lab Parallel (4 concurrent branches)"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [
                    {
                        "key": "run-parallel",
                        "target": "lab-collect",
                        "triggerType": 0,
                        "versionStrategy": "Patch",
                        "labels": label("Run Parallel Branches (par1..4, same order)"),
                        "onExecutionTasks": [
                            {"order": 1, "task": probe_task(1), "mapping": code("LabParStep1Mapping.csx")},
                            {"order": 1, "task": probe_task(2), "mapping": code("LabParStep2Mapping.csx")},
                            {"order": 1, "task": probe_task(3), "mapping": code("LabParStep3Mapping.csx")},
                            {"order": 1, "task": probe_task(4), "mapping": code("LabParStep4Mapping.csx")},
                        ],
                    }
                ],
            },
            {
                "key": "lab-collect",
                "stateType": 2,
                "subType": 0,
                "versionStrategy": "Minor",
                "labels": label("Lab Collect (updateData fan-in)"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [
                    {
                        "key": "auto-lab-complete",
                        "target": "lab-completed",
                        "triggerType": 1,
                        "versionStrategy": "Minor",
                        "labels": label("Lab Threshold Reached"),
                        "rule": code("LabThresholdRule.csx"),
                        "onExecutionTasks": [],
                    }
                ],
            },
            {
                "key": "lab-completed",
                "stateType": 3,
                "subType": 1,
                "versionStrategy": "Major",
                "labels": label("Lab Completed"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [],
            },
            {
                "key": "lab-cancelled",
                "stateType": 3,
                "subType": 3,
                "versionStrategy": "Major",
                "labels": label("Lab Cancelled"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [],
            },
        ],
        "cancel": {
            "key": "cancel-lab",
            "target": "lab-cancelled",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Cancel Lab"),
        },
        "updateData": {
            "key": "update-lab-progress",
            "target": "$self",
            "triggerType": 0,
            "versionStrategy": "Minor",
            "labels": label("Update Lab Progress (counter + noop dedup probe)"),
            "onExecutionTasks": [
                {"order": 1, "task": script_task(), "mapping": code("LabUpdateCounterMapping.csx")}
            ],
        },
    },
}

out = ROOT / "data-integrity-lab.json"
out.write_text(json.dumps(workflow, ensure_ascii=False, indent=2) + "\n")
print(f"wrote {out}")
