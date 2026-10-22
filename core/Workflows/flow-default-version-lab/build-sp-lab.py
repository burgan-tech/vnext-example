#!/usr/bin/env python3
"""Regenerates the SubProcess fixture of the flow-default-version lab from the .csx sources in ./src.

    python3 core/Workflows/flow-default-version-lab/build-sp-lab.py

Writes default-version-lab-sp-parent.json, default-version-lab-sp-child.json (1.0.0; the 1.1.0 variant is
published by the test from this file) and Tasks/flow-default-version-lab/default-version-lab-sp-start.json.
Never hand-edit the base64 `code` blobs.
"""
import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
TASKS = ROOT.parent.parent / "Tasks" / "flow-default-version-lab"
CHILD = "default-version-lab-sp-child"
PARENT = "default-version-lab-sp-parent"
TASK = "default-version-lab-sp-start"


def code(name):
    raw = (ROOT / "src" / name).read_bytes()
    return {"location": f"./src/{name}", "code": base64.b64encode(raw).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def ref(key, flow):
    return {"key": key, "domain": "core", "version": "1.0.0", "flow": flow}


def state(key, state_type, sub_type, text, on_entries=None, transitions=None):
    return {"key": key, "stateType": state_type, "subType": sub_type, "versionStrategy": "Major",
            "labels": label(text), "view": None, "subFlow": None, "onEntries": on_entries or [],
            "onExits": [], "transitions": transitions or []}


def transition(key, target, text, trigger, rule=None):
    t = {"key": key, "target": target, "triggerType": trigger, "versionStrategy": "Minor",
         "labels": label(text)}
    if rule:
        t["rule"] = rule
    t["onExecutionTasks"] = []
    return t


def workflow(key, wf_type, text, tags, start_target, states):
    return {"key": key, "flow": "sys-flows", "flowVersion": "1.0.0", "domain": "core", "version": "1.0.0",
            "tags": ["integration-test", "flow-default-version"] + tags,
            "attributes": {"type": wf_type, "timeout": None, "labels": label(text), "functions": [],
                           "features": [], "extensions": [],
                           "startTransition": {"key": f"start-{key}", "target": start_target, "triggerType": 0,
                                               "versionStrategy": "Major", "labels": label("Start")},
                           "states": states}}


child = workflow(CHILD, "S", "Default Version Lab SubProcess Child", ["child"], "child-initial", [
    state("child-initial", 1, 0, "Child Initial",
          transitions=[transition("finish", "child-finished", "Finish", 0)]),
    state("child-finished", 3, 1, "Child Finished"),
])

parent = workflow(PARENT, "F", "Default Version Lab SubProcess Parent", ["parent", "subprocess"], "sp-initial", [
    state("sp-initial", 1, 0, "SubProcess Parent Initial",
          on_entries=[{"order": 1, "task": ref(TASK, "sys-tasks"), "mapping": code("SpStartChildMapping.csx")}],
          transitions=[transition("auto-to-waiting", "sp-waiting", "Auto to Waiting", 1,
                                  code("SpAlwaysTrueRule.csx"))]),
    state("sp-waiting", 2, 0, "SubProcess Parent Waiting",
          transitions=[transition("complete", "sp-done", "Complete", 0)]),
    state("sp-done", 3, 1, "SubProcess Parent Done"),
])

task = {"key": TASK, "version": "1.0.0", "domain": "core", "flow": "sys-tasks", "flowVersion": "1.0.0",
        "tags": ["flow-default-version", "subprocess"],
        "attributes": {"type": "14", "config": {"domain": "core", "flow": CHILD, "version": "1"}}}


def write(path, doc):
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n")


write(ROOT / f"{CHILD}.json", child)
write(ROOT / f"{PARENT}.json", parent)
TASKS.mkdir(parents=True, exist_ok=True)
write(TASKS / f"{TASK}.json", task)
print("flow-default-version-lab: wrote SubProcess parent + child + task")
