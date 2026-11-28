#!/usr/bin/env python3
"""
build-subflow-cost-lab.py — subflow-cost-lab akislarini uretir.

    python3 core/Workflows/subflow-cost-lab/build-subflow-cost-lab.py

morph-idm'in 2026-11-27 subflow gecis maliyeti raporundaki sekli kurar, task'siz:

  subflow-cost-lab-parent (F): start -> p-init --auto--> p-sub (SubFlow S, input/output mapping) --auto--> p-done
  subflow-cost-lab-child  (S): start -> c-init --auto--> c-done
  subflow-cost-lab-forced-sync (F): start -> f-init --enter-sub (manual, executionType S)--> f-sub (SubFlow S)
                                     --auto--> f-mid (Intermediate, rests Active) --finish--> f-done

Cocuk tek basina da baslatilabilir (type S akisin API'den baslatilmasi desteklenir); boylece ayni cocugun
tek basina ve subflow olarak suresi karsilastirilir. Task yok: olculen sure runtime'in kendi isi.
"""

import base64
import json
import pathlib

HERE = pathlib.Path(__file__).resolve().parent
SRC = HERE / "src"
DOMAIN = "core"
PREFIX = "subflow-cost-lab"


def code(name):
    text = (SRC / name).read_text()
    return {"location": f"./src/{name}", "code": base64.b64encode(text.encode()).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def auto(key, target):
    return {
        "key": key, "target": target, "triggerType": 1, "versionStrategy": "Minor",
        "labels": label(key), "onExecutionTasks": [], "rule": code("AlwaysTrueRule.csx"),
    }


def manual(key, target, execution_type=None):
    transition = {
        "key": key, "target": target, "triggerType": 0, "versionStrategy": "Minor",
        "labels": label(key), "onExecutionTasks": [],
    }
    if execution_type:
        transition["executionType"] = execution_type
    return transition


def state(key, state_type, sub_type=0, transitions=None, sub_flow=None):
    return {
        "key": key, "stateType": state_type, "subType": sub_type, "versionStrategy": "Major",
        "labels": label(key), "view": None, "subFlow": sub_flow,
        "onEntries": [], "onExits": [], "transitions": transitions or [],
    }


def workflow(key, flow_type, start_target, states):
    return {
        "key": key, "flow": "sys-flows", "flowVersion": "1.0.0", "domain": DOMAIN, "version": "1.0.0",
        "tags": ["integration-test", "subflow", PREFIX],
        "attributes": {
            "type": flow_type, "timeout": None, "labels": label(key),
            "functions": [], "features": [], "extensions": [],
            # Unmapped start transition: the creation stores the payload as the initial version.
            "startTransition": {
                "key": "start", "target": start_target, "triggerType": 0, "versionStrategy": "Major",
                "labels": label("start"),
            },
            "states": states,
        },
    }


child = workflow(f"{PREFIX}-child", "S", "c-init", [
    state("c-init", 1, transitions=[auto("auto-complete", "c-done")]),
    state("c-done", 3, sub_type=1),
])

parent = workflow(f"{PREFIX}-parent", "F", "p-init", [
    state("p-init", 1, transitions=[auto("start-subflow", "p-sub")]),
    state("p-sub", 4, transitions=[auto("subflow-completed", "p-done")], sub_flow={
        "type": "S",
        "process": {"key": f"{PREFIX}-child", "domain": DOMAIN, "version": "1.0.0", "flow": "sys-flows"},
        "mapping": code("ParentToChildSubFlowMapping.csx"),
    }),
    state("p-done", 3, sub_type=1),
])

def child_subflow():
    return {
        "type": "S",
        "process": {"key": f"{PREFIX}-child", "domain": DOMAIN, "version": "1.0.0", "flow": "sys-flows"},
        "mapping": code("ParentToChildSubFlowMapping.csx"),
    }


# A manual transition the definition forces to sync, entering a SubFlow: the response must be the
# enriched body whatever sync the caller sent (vnext: post-commit settle loads data by the caller's mode).
forced_sync = workflow(f"{PREFIX}-forced-sync", "F", "f-init", [
    state("f-init", 1, transitions=[manual("enter-sub", "f-sub", execution_type="S")]),
    state("f-sub", 4, transitions=[auto("subflow-completed", "f-mid")], sub_flow=child_subflow()),
    state("f-mid", 2, transitions=[manual("finish", "f-done")]),
    state("f-done", 3, sub_type=1),
])

for wf in (parent, child, forced_sync):
    (HERE / f"{wf['key']}.json").write_text(json.dumps(wf, indent=2) + "\n")
    print("wrote", wf["key"])
