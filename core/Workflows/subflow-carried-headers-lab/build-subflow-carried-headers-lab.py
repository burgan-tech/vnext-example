#!/usr/bin/env python3
"""Regenerates the subflow-carried-headers-lab workflow JSON files.

Proves the runtime's carried terminal headers: when a SubFlow child settles, the headers of the
request that settled it ride the terminal event to the parent.

* sfch-parent (F) — sfch-begin → sfch-a (SubFlow S → sfch-child, aMode from the start data). A's output
  mapping writes `carried.*` from `context.Headers`. On the resumed hop sfch-a routes to
  sfch-resume-saw-sub when the resume context has a `sub` header (ApplyToResume on), else to
  sfch-resume-no-sub (default auto); both record `resumeRoute` and go to sfch-b (SubFlow S →
  sfch-child, mode auto), whose INPUT mapping forwards the resume context's `sub` as `seenSub` and
  whose output mapping copies it back as `bSeenSub`; then sfch-done.
* sfch-child (S) — sfch-child-route sends mode=timer to sfch-child-timer (2s scheduled finish),
  mode=auto straight to sfch-child-done, anything else to sfch-child-wait (manual `finish`,
  manual `explode` whose task throws under a task-level Abort boundary, flow-level `cancel-sfch-child`).

Edit ./src/*.csx and re-run — never hand-edit the base64 blob. Publish is version-immutable: bump
VERSION (patch) for any change to an already-published flow.

    python3 core/Workflows/subflow-carried-headers-lab/build-subflow-carried-headers-lab.py
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
VERSION = "1.0.2"
TASK = {"key": "sfch-script", "domain": "core", "flow": "sys-tasks", "version": "1.0.0"}


def code(name):
    raw = (ROOT / "src" / name).read_bytes()
    return {"location": f"./src/{name}", "code": base64.b64encode(raw).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def auto(key, target, text, rule=None):
    t = {"key": key, "target": target, "triggerType": 1, "versionStrategy": "Minor", "labels": label(text)}
    if rule:
        t["rule"] = code(rule)
    else:
        t["triggerKind"] = 10
    return t


def manual(key, target, text, tasks=None):
    t = {"key": key, "target": target, "triggerType": 0, "versionStrategy": "Minor", "labels": label(text)}
    if tasks:
        t["onExecutionTasks"] = tasks
    return t


def scheduled(key, target, text, timer):
    return {"key": key, "target": target, "triggerType": 2, "versionStrategy": "Minor", "labels": label(text),
            "timer": {"type": "L", **code(timer)}}


def task(mapping_file, abort=False):
    entry = {"order": 1, "task": TASK, "mapping": code(mapping_file)}
    if abort:
        # Task-level Abort boundary (action 0): without ANY boundary a task failure is not acted on
        # (error-boundary-lab U1) and the child would simply finish.
        entry["errorBoundary"] = {"onError": [{"action": 0, "priority": 999}]}
    return entry


def state(key, state_type, text, transitions=None, on_entries=None, sub_flow=None, sub_type=None):
    return {
        "key": key,
        "stateType": state_type,
        "subType": sub_type if sub_type is not None else (1 if state_type == 3 else 0),
        "versionStrategy": "Minor",
        "labels": label(text),
        "view": None,
        "subFlow": sub_flow,
        "onEntries": on_entries or [],
        "onExits": [],
        "transitions": transitions or [],
    }


def sub_flow(mapping_file):
    return {
        "type": "S",
        "process": {"key": "sfch-child", "domain": "core", "flow": "sys-flows", "version": VERSION},
        "mapping": code(mapping_file),
    }


def envelope(key, flow_type, tags, text, target, states, cancel=None):
    attributes = {
        "type": flow_type,
        "labels": label(text),
        "functions": [],
        "features": [],
        "extensions": [],
        "startTransition": {
            "key": "start", "target": target, "triggerType": 0, "versionStrategy": "Minor", "labels": label("Start"),
        },
    }
    if cancel:
        attributes["cancel"] = cancel
    attributes["states"] = states
    return {
        "key": key,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": VERSION,
        "tags": ["integration-test", "subflow-carried-headers-lab", *tags],
        "attributes": attributes,
    }


parent = envelope("sfch-parent", "F", ["parent"], "SubFlow Carried Headers Lab parent", "sfch-begin", [
    state("sfch-begin", 2, "Begin", [auto("auto-a", "sfch-a", "Auto to SubFlow A")]),
    state("sfch-a", 4, "SubFlow A (sfch-child)", [
        auto("resume-saw-sub", "sfch-resume-saw-sub", "Resume context carried sub", rule="SfchResumeSawSubRule.csx"),
        auto("resume-no-sub", "sfch-resume-no-sub", "Resume context had no sub"),
    ], sub_flow=sub_flow("SfchChildAMapping.csx")),
    state("sfch-resume-saw-sub", 2, "Resume saw sub", [auto("auto-b", "sfch-b", "Auto to SubFlow B")],
          on_entries=[task("SfchRouteMapping.csx")]),
    state("sfch-resume-no-sub", 2, "Resume saw no sub", [auto("auto-b", "sfch-b", "Auto to SubFlow B")],
          on_entries=[task("SfchRouteMapping.csx")]),
    state("sfch-b", 4, "SubFlow B (sfch-child, auto)", [auto("auto-done", "sfch-done", "Auto to done")],
          sub_flow=sub_flow("SfchChildBMapping.csx")),
    state("sfch-done", 3, "Done"),
])

child = envelope("sfch-child", "S", ["child"], "SubFlow Carried Headers Lab child", "sfch-child-route", [
    state("sfch-child-route", 1, "Route by mode", [
        auto("to-timer", "sfch-child-timer", "mode = timer", rule="SfchModeTimerRule.csx"),
        auto("to-auto", "sfch-child-done", "mode = auto", rule="SfchModeAutoRule.csx"),
        auto("to-wait", "sfch-child-wait", "Otherwise wait"),
    ]),
    state("sfch-child-wait", 2, "Wait for the caller", [
        manual("finish", "sfch-child-done", "Finish"),
        manual("explode", "sfch-child-done", "Explode (task throws)", tasks=[task("SfchExplodeMapping.csx", abort=True)]),
    ]),
    state("sfch-child-timer", 2, "Wait for the timer", [
        scheduled("timer-finish", "sfch-child-done", "Timer finish", "SfchTimerFinishTimer.csx"),
    ]),
    state("sfch-child-done", 3, "Done"),
    state("sfch-child-cancelled", 3, "Cancelled", sub_type=3),
], cancel={"key": "cancel-sfch-child", "target": "sfch-child-cancelled", "triggerType": 0,
           "versionStrategy": "Major", "labels": label("Cancel SubFlow Carried Headers child")})

for doc in (parent, child):
    path = ROOT / f"{doc['key']}.json"
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {path.name}")
