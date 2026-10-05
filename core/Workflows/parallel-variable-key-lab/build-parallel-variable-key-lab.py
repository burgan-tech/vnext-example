#!/usr/bin/env python3
"""Regenerates the parallel-variable-key-lab workflow JSON files.

Three flows pin how same-order runs of ONE task keep their own response slot:

* pvk-parent — its Initial state `spawning` runs the same SubProcess task three times:
  twice at order 1 with distinct `variableKey`s (primaryChild / secondaryChild — the shape that
  threw "Parallel tasks produced conflicting output" before variableKey), once at order 2 with
  NO variableKey (legacy slot `pvkSpawnChild`), then a Script task at order 3 that reads the three
  slots from `context.TaskResponse` and records the started instance ids. A default automatic
  transition moves it to `spawned`, where it waits Active.
* pvk-reuse — order 1 starts a child into the legacy slot `pvkSpawnChild`; order 2 is a parallel
  group that re-writes that slot (no variableKey) beside `otherChild`; order 3 records both. Pins
  that a parallel group overwrites a slot an earlier order left, as a sequential run would.
* pvk-child — the SubProcess started by each run; it parks in `waiting` and never finishes.

Edit ./src/*.csx and re-run — never hand-edit the base64 blob.

    python3 core/Workflows/parallel-variable-key-lab/build-parallel-variable-key-lab.py
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


def state(key, state_type, sub_type, text, transitions=None, on_entries=None):
    return {
        "key": key,
        "stateType": state_type,
        "subType": sub_type,
        "versionStrategy": "Major",
        "labels": label(text),
        "view": None,
        "subFlow": None,
        "onEntries": on_entries or [],
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
        "tags": ["integration-test", "parallel-variable-key-lab", *tags],
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


def entry(order, task_key, mapping_file, variable_key=None):
    e = {
        "order": order,
        "task": {"key": task_key, "domain": "core", "flow": "sys-tasks", "version": "1.0.0"},
        "mapping": code(mapping_file),
    }
    if variable_key is not None:
        e["variableKey"] = variable_key
    return e


ON_ENTRIES = [
    entry(1, "pvk-spawn-child", "PvkSpawnChildMapping.csx", "primaryChild"),
    entry(1, "pvk-spawn-child", "PvkSpawnChildMapping.csx", "secondaryChild"),
    entry(2, "pvk-spawn-child", "PvkSpawnChildMapping.csx"),
    entry(3, "pvk-record-slots", "PvkRecordSlotsMapping.csx"),
]

# ── pvk-parent: same SubProcess task twice at one order, distinct slots ───────
parent = envelope(
    "pvk-parent",
    ["variable-key", "parallel-tasks", "subprocess"],
    "Parallel Variable Key Lab parent (same task twice at one order)",
    start("start", "spawning"),
    [
        state("spawning", 1, 0, "Spawning (three SubProcess starts, then slot recorder)",
              [transition("auto-spawned", "spawned", 1, "Auto to spawned")],
              on_entries=ON_ENTRIES),
        state("spawned", 2, 0, "Spawned (waits Active)"),
    ],
)

# ── pvk-reuse: a parallel group re-writes a slot an earlier order left ───────
REUSE_ON_ENTRIES = [
    entry(1, "pvk-spawn-child", "PvkSpawnFirstMapping.csx"),
    entry(2, "pvk-spawn-child", "PvkSpawnChildMapping.csx"),
    entry(2, "pvk-spawn-child", "PvkSpawnChildMapping.csx", "otherChild"),
    entry(3, "pvk-record-slots", "PvkRecordReuseMapping.csx"),
]

reuse = envelope(
    "pvk-reuse",
    ["variable-key", "parallel-tasks", "subprocess", "slot-reuse"],
    "Parallel Variable Key Lab reuse (parallel group re-writes an earlier slot)",
    start("start", "spawning"),
    [
        state("spawning", 1, 0, "Spawning (first start, parallel re-write, slot recorder)",
              [transition("auto-spawned", "spawned", 1, "Auto to spawned")],
              on_entries=REUSE_ON_ENTRIES),
        state("spawned", 2, 0, "Spawned (waits Active)"),
    ],
)

# ── pvk-child: started by every spawn run; parks and never finishes ───────────
child = envelope(
    "pvk-child",
    ["variable-key", "subprocess-child"],
    "Parallel Variable Key Lab child (parks in waiting)",
    start("start", "waiting"),
    [
        state("waiting", 1, 0, "Waiting (never finishes)"),
    ],
)

for doc in (parent, reuse, child):
    path = ROOT / f"{doc['key']}.json"
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {path.name}")
