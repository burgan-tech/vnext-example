#!/usr/bin/env python3
"""Regenerates the faulted-recovery-lab workflow from the .csx sources in ./src.

The workflow JSON embeds every mapping as base64 in `code` next to its `location`; edit the .csx
files and re-run this script — never hand-edit the base64 blobs.

    python3 core/Workflows/faulted-recovery-lab/build-faulted-recovery-lab.py

One flow, one question: can an instance that faulted because a transition's mapping (or the data
write right after it) failed be retried, and can the reserved cancel close it?

- faulted-recovery-lab (type F), closed master schema `faulted-recovery-lab-master`.
  fr-draft --fr-advance--> fr-active --fr-finish--> fr-done; cancel `fr-cancel` --> fr-cancelled.
  `fr-advance`, updateData `fr-update` and cancel `fr-cancel` share FailOnDemandMapping.csx: the
  request's `failWith` makes it throw ("throw") or return a field the master schema does not declare
  ("schema").
- faulted-recovery-lab-child (type S): fc-draft --fc-advance (FailOnDemandMapping)--> fc-done;
  cancel `fc-cancel` --> fc-cancelled.
- faulted-recovery-lab-parent (type F): fp-ready --auto--> fp-waits (SubFlow: the child)
  --auto--> fp-done; cancel `fp-cancel` --> fp-cancelled; updateData `fp-update` (FailOnDemandMapping).
  ParentChildMapping.csx's output throws when the child completed with note "fail-output".

An automatic transition cannot declare a mapping (publish refuses it), so none is here.
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
VERSION = "1.0.0"
# Publish is version-immutable: a changed component gets a patch bump.
# 1.0.1: updateData `fp-update` added.
PARENT_VERSION = "1.0.1"
# 1.0.1: the full case matrix (finish, SubFlow, timer, async, broken mapping, task, shared, exit).
MAIN_VERSION = "1.0.1"
# 1.0.1: childNote (fr-with-child's output) and failTask (fr-task's body).
SCHEMA_VERSION = "1.0.1"
KEY = "faulted-recovery-lab"


def code(name):
    raw = (ROOT / "src" / name).read_bytes()
    return {"location": f"./src/{name}", "code": base64.b64encode(raw).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def state(key, state_type, title, sub_type=0, transitions=None, sub_flow=None):
    return {
        "key": key,
        "stateType": state_type,
        "subType": sub_type,
        "versionStrategy": "Major",
        "labels": label(title),
        "view": None,
        "subFlow": sub_flow,
        "onEntries": [],
        "onExits": [],
        "transitions": transitions or [],
    }


def manual(key, target, title, mapping=None):
    transition = {
        "key": key,
        "target": target,
        "triggerType": 0,
        "versionStrategy": "Minor",
        "labels": label(title),
        "onExecutionTasks": [],
    }
    if mapping:
        transition["mapping"] = code(mapping)
    return transition


def auto(key, target, title):
    transition = manual(key, target, title)
    transition["triggerType"] = 1
    transition["rule"] = code("AlwaysTrueRule.csx")
    return transition


def flow(key, flow_type, title, start_target, states, cancel, schema=None, update_data=None, version=VERSION,
         schema_version=VERSION, exit_transition=None, shared=None):
    attributes = {
        "type": flow_type,
        "timeout": None,
        "labels": label(title),
        "functions": [],
        "features": [],
        "extensions": [],
    }
    if schema:
        attributes["schema"] = {"key": schema, "domain": "core", "version": schema_version, "flow": "sys-schemas"}
    attributes["startTransition"] = manual(f"start-{key}", start_target, f"Start {title}")
    if update_data:
        attributes["updateData"] = update_data
    if cancel:
        attributes["cancel"] = cancel
    if exit_transition:
        attributes["exit"] = exit_transition
    if shared:
        attributes["sharedTransitions"] = shared
    attributes["states"] = states
    return {
        "key": key,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": version,
        "tags": ["integration-test", KEY],
        "attributes": attributes,
    }


def scheduled(key, target, title, timer):
    transition = manual(key, target, title)
    transition["triggerType"] = 2
    transition["timer"] = {"type": "L", **code(timer)}
    return transition


def with_task(transition, task_mapping):
    transition["onExecutionTasks"] = [{
        "order": 1,
        "task": {"key": "lab-script-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"},
        "mapping": code(task_mapping),
    }]
    return transition


def child_subflow():
    return {
        "type": "S",
        "process": {"key": f"{KEY}-child", "domain": "core", "version": VERSION, "flow": "sys-flows"},
        "mapping": code("ParentChildMapping.csx"),
    }


async_advance = manual("fr-async", "fr-active", "Advance (definition says async)", "FailOnDemandMapping.csx")
async_advance["executionType"] = "A"

shared = manual("fr-shared", "fr-active", "Shared Advance", "FailOnDemandMapping.csx")
shared["availableIn"] = ["fr-draft"]

restricted_cancel = manual("rr-cancel", "rr-cancelled", "Cancel (first state only)")
restricted_cancel["availableIn"] = ["rr-draft"]

FLOWS = {
    f"{KEY}.json": flow(
        KEY, "F", "Faulted Recovery Lab", "fr-draft",
        version=MAIN_VERSION,
        schema=f"{KEY}-master",
        schema_version=SCHEMA_VERSION,
        update_data=manual("fr-update", "$self", "Update Note", "FailOnDemandMapping.csx"),
        cancel=manual("fr-cancel", "fr-cancelled", "Cancel", "FailOnDemandMapping.csx"),
        exit_transition=manual("fr-exit", "fr-exited", "Exit"),
        shared=[shared],
        states=[
            state("fr-draft", 1, "Draft", transitions=[
                manual("fr-advance", "fr-active", "Advance", "FailOnDemandMapping.csx"),
                manual("fr-finish-direct", "fr-done", "Finish Directly", "FailOnDemandMapping.csx"),
                manual("fr-spawn", "fr-with-child", "Start the Child", "FailOnDemandMapping.csx"),
                manual("fr-to-timed", "fr-timed", "To the Timed State", "FailOnDemandMapping.csx"),
                async_advance,
                manual("fr-broken", "fr-active", "Advance (mapping does not compile)", "BrokenMapping.csx"),
                with_task(manual("fr-task", "fr-active", "Advance (task fails on demand)"), "TaskOnDemandMapping.csx"),
            ]),
            state("fr-active", 2, "Active", transitions=[manual("fr-finish", "fr-done", "Finish")]),
            state("fr-timed", 2, "Timed",
                  transitions=[scheduled("fr-timer-fire", "fr-active", "Timer Fires", "FixedTimer2s.csx")]),
            state("fr-with-child", 4, "With Child", sub_flow=child_subflow(),
                  transitions=[auto("fr-after-child", "fr-done", "After Child")]),
            state("fr-done", 3, "Done", sub_type=1),
            state("fr-exited", 3, "Exited", sub_type=1),
            state("fr-cancelled", 3, "Cancelled", sub_type=7),
        ],
    ),
    f"{KEY}-child.json": flow(
        f"{KEY}-child", "S", "Faulted Recovery Lab Child", "fc-draft",
        cancel=manual("fc-cancel", "fc-cancelled", "Cancel"),
        states=[
            state("fc-draft", 1, "Child Draft",
                  transitions=[manual("fc-advance", "fc-done", "Advance", "FailOnDemandMapping.csx")]),
            state("fc-done", 3, "Child Done", sub_type=1),
            state("fc-cancelled", 3, "Child Cancelled", sub_type=7),
        ],
    ),
    f"{KEY}-parent.json": flow(
        f"{KEY}-parent", "F", "Faulted Recovery Lab Parent", "fp-ready",
        version=PARENT_VERSION,
        update_data=manual("fp-update", "$self", "Update Parent", "FailOnDemandMapping.csx"),
        cancel=manual("fp-cancel", "fp-cancelled", "Cancel"),
        states=[
            state("fp-ready", 1, "Parent Ready", transitions=[auto("fp-to-child", "fp-waits", "To Child")]),
            state("fp-waits", 4, "Parent Waits Child", sub_flow=child_subflow(),
                  transitions=[auto("fp-after-child", "fp-done", "After Child")]),
            state("fp-done", 3, "Parent Done", sub_type=1),
            state("fp-cancelled", 3, "Parent Cancelled", sub_type=7),
        ],
    ),
    f"{KEY}-restricted.json": flow(
        f"{KEY}-restricted", "F", "Faulted Recovery Lab Restricted Cancel", "rr-draft",
        cancel=restricted_cancel,
        states=[
            state("rr-draft", 1, "Draft", transitions=[manual("rr-advance", "rr-active", "Advance")]),
            state("rr-active", 2, "Active",
                  transitions=[manual("rr-fail", "rr-done", "Finish", "FailOnDemandMapping.csx")]),
            state("rr-done", 3, "Done", sub_type=1),
            state("rr-cancelled", 3, "Cancelled", sub_type=7),
        ],
    ),
    f"{KEY}-nocancel.json": flow(
        f"{KEY}-nocancel", "F", "Faulted Recovery Lab Without Cancel", "nc-draft",
        update_data=manual("nc-update", "$self", "Assign Key", "KeyOnlyMapping.csx"),
        cancel=None,
        states=[
            state("nc-draft", 1, "Draft", transitions=[manual("nc-finish", "nc-done", "Finish")]),
            state("nc-done", 3, "Done", sub_type=1),
        ],
    ),
}

for name, definition in FLOWS.items():
    (ROOT / name).write_text(json.dumps(definition, indent=2, ensure_ascii=False) + "\n")
    print(f"wrote {name}")
