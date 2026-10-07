#!/usr/bin/env python3
"""Regenerates the history-none-lab workflow JSON files (vnext#1006).

Four flows pin `attributes.history: "none"` — a one-shot flow that writes no transition or task
history and a single, buffered InstancesData row:

* hn-oneshot — `history: none`. hn-collect runs a seed task (order 1), the same script task twice
  in parallel with distinct variableKeys (order 2) and a summarizer that reads both (order 3); a
  default automatic transition moves to hn-route, whose two rule-based automatic transitions pick
  hn-done-high or hn-done-low by `amount`. Every read happens on the in-memory buffer.
* hn-stuck — `history: none`. hn-gate's only automatic transitions never fire, so the stage rests at
  a non-Finish state: the runtime faults it (incident Instance:100046) and writes the buffered data.
* hn-fault (1.0.1) — `history: none`. hn-boom's task throws and its task-level error boundary aborts
  (action 0): the instance faults with its buffered data written, and retry answers 409
  (Instance:100045). Without the boundary a task's business error does NOT fault — the flow continues
  with its automatic transitions (1.0.0 completed for that reason).
* hn-full-control — hn-oneshot's shape without `history`: still writes full history.

Edit ./src/*.csx and re-run — never hand-edit the base64 blob.

    python3 core/Workflows/history-none-lab/build-history-none-lab.py
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


def auto(key, target, text, rule=None):
    t = {"key": key, "target": target, "triggerType": 1, "versionStrategy": "Minor", "labels": label(text)}
    if rule is None:
        t["triggerKind"] = 10  # default automatic transition
    else:
        t["rule"] = code(rule)
    return t


def state(key, state_type, text, transitions=None, on_entries=None):
    return {
        "key": key,
        "stateType": state_type,
        "subType": 1 if state_type == 3 else 0,
        "versionStrategy": "Minor",
        "labels": label(text),
        "view": None,
        "subFlow": None,
        "onEntries": on_entries or [],
        "onExits": [],
        "transitions": transitions or [],
    }


def entry(order, mapping_file, variable_key=None, abort=False):
    e = {
        "order": order,
        "task": {"key": "hn-script", "domain": "core", "flow": "sys-tasks", "version": "1.0.0"},
        "mapping": code(mapping_file),
    }
    if variable_key is not None:
        e["variableKey"] = variable_key
    if abort:
        e["errorBoundary"] = {"onError": [{"action": 0, "priority": 100}]}
    return e


def envelope(key, tags, text, target, states, history="none"):
    attributes = {
        "type": "F",
        "labels": label(text),
        "functions": [],
        "features": [],
        "extensions": [],
        "startTransition": {
            "key": "start", "target": target, "triggerType": 0, "versionStrategy": "Minor", "labels": label("Start"),
        },
        "states": states,
    }
    if history is not None:
        attributes = {"type": "F", "history": history, **{k: v for k, v in attributes.items() if k != "type"}}
    return {
        "key": key,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": VERSION,
        "tags": ["integration-test", "history-none-lab", *tags],
        "attributes": attributes,
    }


def oneshot_states():
    return [
        state("hn-collect", 2, "Collect (seed, two parallel slots, summary)",
              [auto("auto-route", "hn-route", "Auto to route")],
              on_entries=[
                  entry(1, "HnSeedMapping.csx"),
                  entry(2, "HnLeftMapping.csx", "left"),
                  entry(2, "HnRightMapping.csx", "right"),
                  entry(3, "HnSummarizeMapping.csx"),
              ]),
        state("hn-route", 2, "Route by amount",
              [auto("to-high", "hn-done-high", "High amount", "HnHighRule.csx"),
               auto("to-low", "hn-done-low", "Low amount", "HnLowRule.csx")]),
        state("hn-done-high", 3, "Done (high)"),
        state("hn-done-low", 3, "Done (low)"),
    ]


oneshot = envelope("hn-oneshot", ["history-none", "one-shot"], "History None Lab one-shot",
                   "hn-collect", oneshot_states())

stuck = envelope("hn-stuck", ["history-none", "non-terminal-rest"], "History None Lab stuck gate", "hn-gate", [
    state("hn-gate", 2, "Gate (no automatic rule ever fires)",
          [auto("never-a", "hn-done", "Never A", "HnNeverRule.csx"),
           auto("never-b", "hn-done", "Never B", "HnNeverRule.csx")]),
    state("hn-done", 3, "Done (unreachable at runtime)"),
])

fault = envelope("hn-fault", ["history-none", "fault"], "History None Lab task fault", "hn-boom", [
    state("hn-boom", 2, "Boom (task throws, boundary aborts)",
          [auto("auto-done", "hn-done", "Auto to done")],
          on_entries=[entry(1, "HnThrowMapping.csx", abort=True)]),
    state("hn-done", 3, "Done (unreachable at runtime)"),
])
fault["version"] = "1.0.1"

control = envelope("hn-full-control", ["full-history-control"], "History None Lab full-history control",
                   "hn-collect", oneshot_states(), history=None)

for doc in (oneshot, stuck, fault, control):
    path = ROOT / f"{doc['key']}.json"
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {path.name}")
