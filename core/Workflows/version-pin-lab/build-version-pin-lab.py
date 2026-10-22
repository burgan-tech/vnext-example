#!/usr/bin/env python3
"""Regenerates the version-pin-lab workflows and task from the .csx sources in ./src.

The JSON embeds every mapping / rule as base64 in `code` next to its `location`; edit the .csx
files and re-run this script — never hand-edit the base64 blobs.

    python3 core/Workflows/version-pin-lab/build-version-pin-lab.py

The child's 1.0.3 variant is deliberately NOT generated here: it must stay outside core/ so the SDK
publisher does not post it, and the test publishes it itself (see VersionPinLabTests).
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
TASKS = ROOT.parent.parent / "Tasks" / "version-pin-lab"

CHILD = "version-pin-lab-child"


def code(name):
    raw = (ROOT / "src" / name).read_bytes()
    return {"location": f"./src/{name}", "code": base64.b64encode(raw).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def ref(key, flow, version="1.0.0"):
    return {"key": key, "domain": "core", "version": version, "flow": flow}


def state(key, state_type, sub_type, text, *, sub_flow=None, on_entries=None, transitions=None):
    return {
        "key": key,
        "stateType": state_type,
        "subType": sub_type,
        "versionStrategy": "Major",
        "labels": label(text),
        "view": None,
        "subFlow": sub_flow,
        "onEntries": on_entries or [],
        "onExits": [],
        "transitions": transitions or [],
    }


def auto(key, target, text):
    return {
        "key": key,
        "target": target,
        "triggerType": 1,
        "versionStrategy": "Minor",
        "labels": label(text),
        "rule": code("AlwaysTrueRule.csx"),
        "onExecutionTasks": [],
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


def workflow(key, wf_type, text, extra_tags, start_target, states, version="1.0.0"):
    return {
        "key": key,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": version,
        "tags": ["integration-test", "version-pin-lab"] + extra_tags,
        "attributes": {
            "type": wf_type,
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
                "labels": label("Start"),
            },
            "states": states,
        },
    }


child = workflow(CHILD, "S", "Version Pin Lab Child", ["child"], "child-initial", [
    state("child-initial", 1, 0, "Child Initial",
          transitions=[manual("finish", "child-finished", "Finish")]),
    state("child-finished", 3, 1, "Child Finished"),
])

subflow_parent = workflow("version-pin-lab-subflow-parent", "F", "Version Pin Lab SubFlow Parent",
                          ["parent", "subflow"], "sf-initial", [
    state("sf-initial", 1, 0, "SubFlow Parent Initial",
          transitions=[auto("auto-to-subflow", "sf-subflow", "Auto to SubFlow")]),
    # subFlow.process carries the PARTIAL version "1.0"; the correlation must pin the resolved one.
    state("sf-subflow", 4, 0, "SubFlow State",
          sub_flow={"type": "S", "process": ref(CHILD, "sys-flows", "1.0"),
                    "mapping": code("ParentToChildSubFlowMapping.csx")},
          transitions=[auto("auto-to-completed", "sf-completed", "Auto to Completed")]),
    state("sf-completed", 3, 1, "SubFlow Parent Completed"),
], version="1.0.1")  # 1.0.1: mapping signature fix (publish is version-immutable)

subprocess_parent = workflow("version-pin-lab-subprocess-parent", "F", "Version Pin Lab SubProcess Parent",
                             ["parent", "subprocess"], "sp-initial", [
    state("sp-initial", 1, 0, "SubProcess Parent Initial",
          on_entries=[{"order": 1,
                       "task": ref("version-pin-lab-start-subprocess", "sys-tasks"),
                       "mapping": code("StartChildSubProcessMapping.csx")}],
          transitions=[auto("auto-to-waiting", "sp-waiting", "Auto to Waiting")]),
    state("sp-waiting", 2, 0, "SubProcess Parent Waiting",
          transitions=[manual("complete", "sp-completed", "Complete")]),
    state("sp-completed", 3, 1, "SubProcess Parent Completed"),
])

task = {
    "key": "version-pin-lab-start-subprocess",
    "version": "1.0.0",
    "domain": "core",
    "flow": "sys-tasks",
    "flowVersion": "1.0.0",
    "tags": ["version-pin-lab", "subprocess"],
    "attributes": {
        "type": "14",
        # The mapping overrides all three at run time; the trigger version is the partial "1.0".
        "config": {"domain": "core", "flow": CHILD, "version": "1.0"},
    },
}


def write(path, doc):
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n")


write(ROOT / f"{CHILD}.json", child)
write(ROOT / "version-pin-lab-subflow-parent.json", subflow_parent)
write(ROOT / "version-pin-lab-subprocess-parent.json", subprocess_parent)
TASKS.mkdir(parents=True, exist_ok=True)
write(TASKS / "version-pin-lab-start-subprocess.json", task)
print("version-pin-lab: wrote 3 workflows + 1 task")
