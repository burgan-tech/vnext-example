#!/usr/bin/env python3
"""Regenerates the history-none-subflow-lab workflow JSON files (vnext#1006).

A `history: none` parent requires a `history: none` SubFlow child; the reverse is allowed.

* hns-parent (none) — hns-prepare writes parentToken, then hns-sub (SubFlow S → hns-child) hands it
  over; childEcho comes back as childResult; a default automatic transition reaches hns-done.
* hns-child (S, none) — echoes parentToken as childEcho and finishes.
* hns-parent-bad (none) — same shape, but its child hns-child-full keeps full history: the child
  refuses to start (Instance:100044) and the parent faults.
* hns-child-full (S, full) — hns-child without `history`.
* hns-parent-full (full) — a full-history parent of the none child hns-child: allowed.

Edit ./src/*.csx and re-run — never hand-edit the base64 blob.

    python3 core/Workflows/history-none-subflow-lab/build-history-none-subflow-lab.py
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


def auto(key, target, text):
    return {"key": key, "target": target, "triggerType": 1, "triggerKind": 10,
            "versionStrategy": "Minor", "labels": label(text)}


def state(key, state_type, text, transitions=None, on_entries=None, sub_flow=None):
    return {
        "key": key,
        "stateType": state_type,
        "subType": 1 if state_type == 3 else 0,
        "versionStrategy": "Minor",
        "labels": label(text),
        "view": None,
        "subFlow": sub_flow,
        "onEntries": on_entries or [],
        "onExits": [],
        "transitions": transitions or [],
    }


def entry(mapping_file):
    return {
        "order": 1,
        "task": {"key": "hns-script", "domain": "core", "flow": "sys-tasks", "version": "1.0.0"},
        "mapping": code(mapping_file),
    }


def envelope(key, flow_type, tags, text, target, states, history):
    attributes = {"type": flow_type}
    if history is not None:
        attributes["history"] = history
    attributes.update({
        "labels": label(text),
        "functions": [],
        "features": [],
        "extensions": [],
        "startTransition": {
            "key": "start", "target": target, "triggerType": 0, "versionStrategy": "Minor", "labels": label("Start"),
        },
        "states": states,
    })
    return {
        "key": key,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": VERSION,
        "tags": ["integration-test", "history-none-subflow-lab", *tags],
        "attributes": attributes,
    }


def parent(key, child, history, text):
    return envelope(key, "F", ["parent"], text, "hns-prepare", [
        state("hns-prepare", 2, "Prepare parentToken", [auto("auto-sub", "hns-sub", "Auto to SubFlow")],
              on_entries=[entry("HnsPrepareMapping.csx")]),
        state("hns-sub", 4, f"SubFlow {child}", [auto("auto-done", "hns-done", "Auto to done")],
              sub_flow={
                  "type": "S",
                  "process": {"key": child, "domain": "core", "flow": "sys-flows", "version": VERSION},
                  "mapping": code("HnsParentToChildMapping.csx"),
              }),
        state("hns-done", 3, "Done"),
    ], history)


def child(key, history, text):
    return envelope(key, "S", ["child"], text, "hns-child-work", [
        state("hns-child-work", 2, "Echo parentToken", [auto("auto-done", "hns-child-done", "Auto to done")],
              on_entries=[entry("HnsChildEchoMapping.csx")]),
        state("hns-child-done", 3, "Done"),
    ], history)


docs = [
    parent("hns-parent", "hns-child", "none", "History None SubFlow Lab parent"),
    child("hns-child", "none", "History None SubFlow Lab child"),
    parent("hns-parent-bad", "hns-child-full", "none", "History None SubFlow Lab parent with a full-history child"),
    child("hns-child-full", None, "History None SubFlow Lab full-history child"),
    parent("hns-parent-full", "hns-child", None, "History None SubFlow Lab full-history parent"),
]

for doc in docs:
    path = ROOT / f"{doc['key']}.json"
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {path.name}")
