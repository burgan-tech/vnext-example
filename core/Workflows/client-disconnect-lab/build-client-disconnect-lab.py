#!/usr/bin/env python3
"""Regenerates the client-disconnect-lab workflows (vnext#1258) from the .csx sources in ./src.

The workflow JSON embeds every mapping as base64 in `code` next to its `location`; edit the .csx
files and re-run this script — never hand-edit the base64 blobs.

    python3 core/Workflows/client-disconnect-lab/build-client-disconnect-lab.py

Four flows, one slow task (`cdl-slow-http`, MockLab answers after 8 s):

- client-disconnect-lab        manual `cdl-slow-commit` runs the slow task -> finish (scenarios a, c, e)
- client-disconnect-lab-chain  start -> automatic hop running the slow task -> rest state (scenario b)
- client-disconnect-lab-parent blocking S SubFlow on client-disconnect-lab-child (scenario d)
- client-disconnect-lab-child  manual `cdl-child-slow-commit` runs the slow task -> finish
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
VERSION = "1.0.0"
TAGS = ["integration-test", "client-disconnect-lab", "vnext-1258"]


def code(name):
    raw = (ROOT / "src" / name).read_bytes()
    return {"location": f"./src/{name}", "code": base64.b64encode(raw).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def slow_task():
    return {
        "order": 1,
        "task": {"key": "cdl-slow-http", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"},
        "mapping": code("CdlSlowTaskMapping.csx"),
    }


def state(key, state_type, sub_type, text, transitions=(), sub_flow=None):
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


def manual(key, target, text, tasks=()):
    return {
        "key": key,
        "target": target,
        "triggerType": 0,
        "versionStrategy": "Minor",
        "labels": label(text),
        "onExecutionTasks": list(tasks),
    }


def auto(key, target, text, tasks=()):
    # triggerKind 10 = the default automatic transition: fires without a rule.
    return {
        "key": key,
        "target": target,
        "triggerType": 1,
        "versionStrategy": "Minor",
        "labels": label(text),
        "triggerKind": 10,
        "onExecutionTasks": list(tasks),
    }


def workflow(key, flow_type, text, comment, start_target, states, extra_tags=()):
    return {
        "key": key,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": VERSION,
        "tags": TAGS + list(extra_tags),
        "attributes": {
            "_comment": comment,
            "type": flow_type,
            "timeout": None,
            "labels": label(text),
            "functions": [],
            "features": [],
            "extensions": [],
            "startTransition": {
                "key": f"start-{key}",
                "target": start_target,
                "triggerType": 0,
                "versionStrategy": "Major",
                "labels": label(f"Start {text}"),
                "onExecutionTasks": [],
            },
            "states": states,
        },
    }


flows = {
    "client-disconnect-lab": workflow(
        "client-disconnect-lab",
        "F",
        "Client Disconnect Lab",
        "vnext#1258. `cdl-slow-commit` runs one HTTP task that MockLab answers after 8 s. The tests "
        "call it with sync=true and abort the HTTP request after 1 s: the run has already committed "
        "its Busy reservation, so it must continue to cdl-done (C) instead of being cancelled with "
        "the request and leaving the instance Busy with no owner.",
        "cdl-ready",
        [
            state(
                "cdl-ready",
                1,
                0,
                "Ready",
                [manual("cdl-slow-commit", "cdl-done", "Run the slow task, then finish", [slow_task()])],
            ),
            state("cdl-done", 3, 1, "Done"),
        ],
        ["root"],
    ),
    "client-disconnect-lab-chain": workflow(
        "client-disconnect-lab-chain",
        "F",
        "Client Disconnect Lab Chain",
        "vnext#1258. A sync START whose inline automatic chain runs the 8 s task. The client aborts "
        "after 1 s; the start has already committed the instance, so the chain must still reach "
        "the rest state cdl-chain-rest (Active).",
        "cdl-chain-entry",
        [
            state(
                "cdl-chain-entry",
                1,
                0,
                "Entry (moves on automatically)",
                [auto("cdl-chain-slow-hop", "cdl-chain-rest", "Automatic hop running the slow task", [slow_task()])],
            ),
            state(
                "cdl-chain-rest",
                2,
                0,
                "Rest point (waits for a manual transition)",
                [manual("cdl-chain-finish", "cdl-chain-done", "Finish")],
            ),
            state("cdl-chain-done", 3, 1, "Done"),
        ],
        ["auto-chain"],
    ),
    "client-disconnect-lab-parent": workflow(
        "client-disconnect-lab-parent",
        "F",
        "Client Disconnect Lab Parent",
        "vnext#1258. A blocking S SubFlow. The test sends the CHILD's transition to the PARENT's "
        "address (the SubFlow transition proxy forwards it to the leaf) and aborts after 1 s. The "
        "child must finish and the parent must resume to cdl-parent-done; neither may stay Busy. "
        "subFlow.process keeps domain `core` (the SDK publisher does not rewrite `process`), so "
        "this flow only runs on core.",
        "cdl-parent-entry",
        [
            state(
                "cdl-parent-entry",
                1,
                0,
                "Entry (moves on automatically)",
                [auto("cdl-parent-to-subflow", "cdl-parent-subflow", "Auto to subflow")],
            ),
            state(
                "cdl-parent-subflow",
                4,
                0,
                "SubFlow (child waits for its slow transition)",
                [auto("cdl-parent-to-done", "cdl-parent-done", "Auto to done after the child finished")],
                sub_flow={
                    "type": "S",
                    "process": {
                        "key": "client-disconnect-lab-child",
                        "domain": "core",
                        "version": VERSION,
                        "flow": "sys-flows",
                    },
                    "mapping": code("CdlSubFlowMapping.csx"),
                },
            ),
            state("cdl-parent-done", 3, 1, "Done"),
        ],
        ["parent", "subflow"],
    ),
    "client-disconnect-lab-child": workflow(
        "client-disconnect-lab-child",
        "S",
        "Client Disconnect Lab Child",
        "vnext#1258. Child of client-disconnect-lab-parent. `cdl-child-slow-commit` runs the 8 s "
        "task; the test reaches it through the PARENT's address.",
        "cdl-child-ready",
        [
            state(
                "cdl-child-ready",
                1,
                0,
                "Ready",
                [
                    manual(
                        "cdl-child-slow-commit",
                        "cdl-child-done",
                        "Run the slow task, then finish",
                        [slow_task()],
                    )
                ],
            ),
            state("cdl-child-done", 3, 1, "Done"),
        ],
        ["child", "subflow"],
    ),
}

for name, body in flows.items():
    out = ROOT / f"{name}.json"
    out.write_text(json.dumps(body, indent=2) + "\n")
    print(f"wrote {out}")
