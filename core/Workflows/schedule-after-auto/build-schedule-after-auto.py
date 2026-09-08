#!/usr/bin/env python3
"""Regenerates schedule-after-auto.json from the .csx sources in ./src.

The workflow JSON embeds every mapping / rule / timer as base64 in `code` next to its
`location`; edit the .csx files and re-run this script — never hand-edit the base64 blobs.

    python3 core/Workflows/schedule-after-auto/build-schedule-after-auto.py
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


def timer(name):
    raw = (ROOT / "src" / name).read_bytes()
    return {
        "type": "L",
        "location": f"./src/{name}",
        "code": base64.b64encode(raw).decode(),
    }


def script_task():
    # ONE task definition is enough here: no transition runs it twice at the same order, so the
    # task-journal ExecutionKey — derived from (transition, task, order) — cannot collide.
    return {"key": "sched-script-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}


def label(text):
    return [{"language": "en-US", "label": text}]


def hook(mapping_file, order=1):
    return {"order": order, "task": script_task(), "mapping": code(mapping_file)}


workflow = {
    "key": "schedule-after-auto",
    "flow": "sys-flows",
    "flowVersion": "1.0.0",
    "domain": "core",
    "version": "1.0.0",
    "tags": [
        "integration-test",
        "schedule-after-auto",
        "pipeline-epilogue",
        "scheduled-transition",
        "automatic-transition",
    ],
    "attributes": {
        "type": "F",
        "timeout": None,
        "labels": label("Schedule After Auto (epilogue ordering probe)"),
        "functions": [],
        "features": [],
        "extensions": [],
        "startTransition": {
            "key": "start-schedule-after-auto",
            "target": "gate",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start Schedule After Auto"),
            "onExecutionTasks": [hook("SeedMapping.csx")],
        },
        "states": [
            {
                # The whole scenario lives here: ONE state carrying BOTH a conditional automatic
                # transition and a scheduled one. Which of the two the runtime honours is decided
                # by the epilogue ordering (Auto 80 -> Schedule 90).
                "key": "gate",
                "stateType": 1,
                "subType": 0,
                "versionStrategy": "Major",
                "labels": label("Gate (auto + timer race)"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [
                    {
                        "key": "auto-advance",
                        "target": "advanced",
                        "triggerType": 1,
                        "versionStrategy": "Minor",
                        "labels": label("Auto Advance (mode = auto)"),
                        "rule": code("AutoGateRule.csx"),
                        "onExecutionTasks": [hook("AutoAdvanceMapping.csx")],
                    },
                    {
                        "key": "gate-timeout",
                        "target": "gate-timedout",
                        "triggerType": 2,
                        "versionStrategy": "Minor",
                        "labels": label("Gate Timeout (8s)"),
                        "timer": timer("GateTimeoutTimer.csx"),
                        "onExecutionTasks": [hook("TimeoutMapping.csx")],
                    },
                ],
            },
            {
                # Where the auto winner parks. Deliberately NOT a finish state and deliberately
                # carrying no timer: a completed instance would report an empty transition list
                # for reasons unrelated to the behaviour under test.
                "key": "advanced",
                "stateType": 2,
                "subType": 0,
                "versionStrategy": "Minor",
                "labels": label("Advanced (auto winner parked here)"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [
                    {
                        "key": "finish-advanced",
                        "target": "advanced-done",
                        "triggerType": 0,
                        "versionStrategy": "Patch",
                        "labels": label("Finish Advanced"),
                        "onExecutionTasks": [],
                    }
                ],
            },
            {
                "key": "advanced-done",
                "stateType": 3,
                "subType": 1,
                "versionStrategy": "Major",
                "labels": label("Advanced Done"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [],
            },
            {
                "key": "gate-timedout",
                "stateType": 3,
                "subType": 8,
                "versionStrategy": "Major",
                "labels": label("Gate Timed Out (scheduled transition fired)"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [],
            },
            {
                "key": "sched-cancelled",
                "stateType": 3,
                "subType": 3,
                "versionStrategy": "Major",
                "labels": label("Cancelled"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [],
            },
        ],
        "cancel": {
            "key": "cancel-schedule-after-auto",
            "target": "sched-cancelled",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Cancel Schedule After Auto"),
        },
    },
}


def main():
    out = ROOT / "schedule-after-auto.json"
    out.write_text(json.dumps(workflow, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
