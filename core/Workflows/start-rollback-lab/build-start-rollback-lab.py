#!/usr/bin/env python3
"""Regenerates the start-rollback-lab workflows from the .csx sources in ./src.

The workflow JSON embeds every mapping / rule as base64 in `code` next to its `location`;
edit the .csx files and re-run this script — never hand-edit the base64 blobs.

    python3 core/Workflows/start-rollback-lab/build-start-rollback-lab.py

Five flows, one question: does a start that fails after its instance row was inserted leave
anything behind?

- start-rollback-lab-root      (type F): its start mapping throws without `customerId`.
- start-rollback-lab-child     (type S): the same start mapping, started by the parent below.
- start-rollback-lab-parent    (type F): auto-enters a SubFlow state that starts the child, passing
  `customerId` on only when the parent's own data has one.
- start-rollback-lab-process   (type P): the same start mapping, started by the SubProcess parent.
- start-rollback-lab-sp-parent (type F): its initial state's onEntry runs the SubProcess task
  `start-rollback-lab-start-process` (core/Tasks/start-rollback-lab/), passing `customerId` on
  only when the parent's own data has one.
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


def state(key, state_type, title, sub_type=0, transitions=None, sub_flow=None, on_entries=None):
    return {
        "key": key,
        "stateType": state_type,
        "subType": sub_type,
        "versionStrategy": "Major",
        "labels": label(title),
        "view": None,
        "subFlow": sub_flow,
        "onEntries": on_entries or [],
        "onExits": [],
        "transitions": transitions or [],
    }


def task_ref(order, key, mapping):
    return {
        "order": order,
        "task": {"key": key, "domain": "core", "version": VERSION, "flow": "sys-tasks"},
        "mapping": code(mapping),
    }


def auto(key, target, title):
    return {
        "key": key,
        "target": target,
        "triggerType": 1,
        "versionStrategy": "Minor",
        "labels": label(title),
        "rule": code("AlwaysTrueRule.csx"),
        "onExecutionTasks": [],
    }


def workflow(key, flow_type, title, start_target, states, start_mapping=None, tags=()):
    start = {
        "key": f"start-{key}",
        "target": start_target,
        "triggerType": 0,
        "versionStrategy": "Major",
        "labels": label(f"Start {title}"),
        "onExecutionTasks": [],
    }
    if start_mapping:
        start["mapping"] = code(start_mapping)
    return {
        "key": key,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": VERSION,
        "tags": ["integration-test", "start-rollback-lab", *tags],
        "attributes": {
            "type": flow_type,
            "timeout": None,
            "labels": label(title),
            "functions": [],
            "features": [],
            "extensions": [],
            "startTransition": start,
            "states": states,
        },
    }


FLOWS = {
    "start-rollback-lab-root.json": workflow(
        "start-rollback-lab-root", "F", "Start Rollback Lab Root", "root-waiting",
        [state("root-waiting", 1, "Root Waiting")],
        start_mapping="RequireCustomerStartMapping.csx",
        tags=("root",),
    ),
    "start-rollback-lab-child.json": workflow(
        "start-rollback-lab-child", "S", "Start Rollback Lab Child", "child-waiting",
        # No transitions: a child that started stays observable in child-waiting.
        [state("child-waiting", 1, "Child Waiting")],
        start_mapping="RequireCustomerStartMapping.csx",
        tags=("child", "subflow"),
    ),
    "start-rollback-lab-parent.json": workflow(
        "start-rollback-lab-parent", "F", "Start Rollback Lab Parent", "parent-ready",
        [
            state("parent-ready", 1, "Parent Ready",
                  transitions=[auto("auto-parent-to-child", "parent-waits-child", "Auto to SubFlow")]),
            state(
                "parent-waits-child", 4, "Parent Waits Child",
                sub_flow={
                    "type": "S",
                    "process": {
                        "key": "start-rollback-lab-child",
                        "domain": "core",
                        "version": VERSION,
                        "flow": "sys-flows",
                    },
                    "mapping": code("ParentToChildMapping.csx"),
                },
                # Not reached in this lab (the child never completes); a path to completion only.
                transitions=[auto("auto-parent-to-done", "parent-done", "Auto to Done")],
            ),
            state("parent-done", 3, "Parent Done", sub_type=1),
        ],
        tags=("parent", "subflow"),
    ),
    "start-rollback-lab-process.json": workflow(
        "start-rollback-lab-process", "P", "Start Rollback Lab Process", "process-waiting",
        # No transitions: a process that started stays observable in process-waiting.
        [state("process-waiting", 1, "Process Waiting")],
        start_mapping="RequireCustomerStartMapping.csx",
        tags=("process", "subprocess"),
    ),
    "start-rollback-lab-sp-parent.json": workflow(
        "start-rollback-lab-sp-parent", "F", "Start Rollback Lab SubProcess Parent", "sp-parent-ready",
        # No transitions: a parent whose SubProcess started stays observable in sp-parent-ready; a
        # failed SubProcess start fails the onEntry task and faults it there.
        [state("sp-parent-ready", 1, "SubProcess Parent Ready",
               on_entries=[task_ref(1, "start-rollback-lab-start-process", "ParentToProcessMapping.csx")])],
        tags=("parent", "subprocess"),
    ),
}

for file_name, definition in FLOWS.items():
    out = ROOT / file_name
    out.write_text(json.dumps(definition, indent=2) + "\n")
    print(f"wrote {out}")
