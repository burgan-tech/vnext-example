#!/usr/bin/env python3
"""Regenerates the implicit-start-lab workflow JSON files.

Three flows prove that a workflow may omit the Initial state (stateType 1):

* implicit-start-lab         — NO Initial state; startTransition enters a Wizard state directly.
* implicit-start-lab-child   — NO Initial state; started by the parent's SubFlow state.
* implicit-start-lab-parent  — DECLARES an Initial state (the regression side) and starts the child.

An Initial-less instance is born in the runtime's implicit `$start` source state, so its first
history row reads `fromState == "$start"`. The only script is the SubFlow mapping, which the
schema requires; edit ./src/*.csx and re-run — never hand-edit the base64 blob.

    python3 core/Workflows/implicit-start-lab/build-implicit-start-lab.py
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


def transition(key, target, trigger_type, text, version_strategy="Minor"):
    t = {
        "key": key,
        "target": target,
        "triggerType": trigger_type,
        "versionStrategy": version_strategy,
        "labels": label(text),
    }
    if trigger_type == 1:
        t["triggerKind"] = 10  # default automatic transition: no rule, fires once the state settles
    return t


def state(key, state_type, sub_type, text, transitions=None, sub_flow=None):
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
        "transitions": transitions or [],
    }


def envelope(key, tags, text, start, states):
    return {
        "key": key,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": VERSION,
        "tags": ["integration-test", "implicit-start-lab", *tags],
        "attributes": {
            "type": "F",
            "timeout": None,
            "labels": label(text),
            "functions": [],
            "features": [],
            "extensions": [],
            "startTransition": start,
            "states": states,
        },
    }


def start(key, target):
    return {
        "key": key,
        "target": target,
        "triggerType": 0,
        "versionStrategy": "Major",
        "labels": label("Start"),
    }


# ── implicit-start-lab: no Initial, start enters a Wizard state ───────────────
lab = envelope(
    "implicit-start-lab",
    ["implicit-start", "no-initial-state", "wizard"],
    "Implicit Start Lab (no Initial state, start enters a wizard)",
    start("start-implicit", "step-1"),
    [
        state("step-1", 5, 0, "Step 1 (wizard, entered straight from $start)",
              [transition("next", "done", 0, "Next")]),
        state("done", 3, 1, "Done"),
    ],
)

# ── implicit-start-lab-child: no Initial, born by a parent's SubFlow state ────
child = envelope(
    "implicit-start-lab-child",
    ["implicit-start", "no-initial-state", "subflow-child"],
    "Implicit Start Lab child (no Initial state, started as a SubFlow)",
    start("start-implicit-child", "step-1"),
    [
        state("step-1", 2, 0, "Step 1 (intermediate, entered straight from $start)",
              [transition("next", "done", 0, "Next")]),
        state("done", 3, 1, "Done"),
    ],
)

# ── implicit-start-lab-parent: DECLARES an Initial (regression side) ──────────
parent = envelope(
    "implicit-start-lab-parent",
    ["initial-state-declared", "subflow-parent"],
    "Implicit Start Lab parent (declares Initial, starts an Initial-less child)",
    start("start-implicit-parent", "waiting"),
    [
        state("waiting", 1, 0, "Waiting (declared Initial state)",
              [transition("auto-to-child", "in-child", 1, "Auto to child")]),
        state("in-child", 4, 0, "In child (SubFlow)",
              [transition("auto-child-done", "done", 1, "Auto after child")],
              sub_flow={
                  "type": "S",
                  "process": {
                      "key": "implicit-start-lab-child",
                      "domain": "core",
                      "version": VERSION,
                      "flow": "sys-flows",
                  },
                  "mapping": code("ImplicitStartLabSubFlowMapping.csx"),
              }),
        state("done", 3, 1, "Done"),
    ],
)

for doc in (lab, child, parent):
    path = ROOT / f"{doc['key']}.json"
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {path.name}")
