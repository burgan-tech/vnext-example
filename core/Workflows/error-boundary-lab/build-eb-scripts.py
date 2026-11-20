#!/usr/bin/env python3
"""Regenerates eb-scripts.json from the EbThrow*.csx sources in ./src.

    python3 core/Workflows/error-boundary-lab/build-eb-scripts.py

eb-scripts (vnext#1268) is the error-boundary lab's script-failure workflow: one manual case per
place a flow-author script can throw during a transition, each failing in a helper method so the
incident's stack trace has more than one script frame to show.

  case-task-input          task input handler, inner exception, no boundary  -> unhandled task incident
  case-task-input-abort    the same with a task-level abort                  -> the boundary's incident
  case-task-output         script task output handler, no boundary anywhere  -> unhandled task incident
  case-task-output-nomatch the same with a boundary that does not match      -> unhandled task incident
  case-transition-mapping  transition mapping                                -> pipeline incident
  case-lock-key            resource-lock key script                          -> pipeline incident
  case-timer               timer script of the target state                  -> pipeline incident
  case-rule                automatic-transition rule                         -> no incident (by design)

The API never serves an incident's stack trace, so the tests assert the incident itself and the
README carries the psql check of InstanceIncidents."StackTrace".
"""

import base64
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
SRC = HERE / "src"
KEY = "eb-scripts"
VERSION = "1.0.0"
TASK = {"key": "eb-script-fail-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}
ABORT = {"onError": [{"action": 0, "priority": 999}]}
# A boundary that exists but never matches a script failure's status (500): the engine then has a
# chain, finds no rule, and reports the failure as unhandled.
NO_MATCH = {"onError": [{"action": 0, "errorCodes": ["404"]}]}


def code(name):
    return {"location": f"./src/{name}.csx", "code": base64.b64encode((SRC / f"{name}.csx").read_bytes()).decode()}


def labels(text):
    return [{"language": "en-US", "label": text}]


def transition(key, target, label, **extra):
    return {"key": key, "target": target, "triggerType": 0, "versionStrategy": "Patch",
            "labels": labels(label), "onExecutionTasks": [], **extra}


def task(mapping, boundary=None):
    entry = {"order": 1, "task": TASK, "mapping": code(mapping)}
    if boundary is not None:
        entry["errorBoundary"] = boundary
    return [entry]


def state(key, state_type, label, transitions):
    return {"key": key, "stateType": state_type, "versionStrategy": "Patch", "labels": labels(label),
            "transitions": transitions}


workflow = {
    "key": KEY,
    "flow": "sys-flows",
    "flowVersion": "1.0.0",
    "domain": "core",
    "version": VERSION,
    "tags": ["integration-test", "error-boundary-lab", "incident", "script-failure"],
    "attributes": {
        "type": "F",
        "labels": labels("Error Boundary Lab - Script Failures"),
        "functions": [],
        "features": [],
        "extensions": [],
        "startTransition": transition("start-eb-scripts", "ready", "Start"),
        "states": [
            state("ready", 1, "Ready", [
                transition("case-task-input", "landed", "Task input handler throws",
                           onExecutionTasks=task("EbThrowInputMapping")),
                transition("case-task-input-abort", "landed", "Task input handler throws, task abort",
                           onExecutionTasks=task("EbThrowInputMapping", ABORT)),
                transition("case-task-output", "landed", "Script task output handler throws",
                           onExecutionTasks=task("EbThrowOutputMapping")),
                transition("case-task-output-nomatch", "landed", "Output handler throws, boundary does not match",
                           onExecutionTasks=task("EbThrowOutputMapping", NO_MATCH)),
                transition("case-transition-mapping", "landed", "Transition mapping throws",
                           mapping=code("EbThrowTransitionMapping")),
                transition("case-lock-key", "landed", "Resource-lock key script throws",
                           resourceLock={"keyExpression": code("EbThrowLockKey"), "action": "Acquire",
                                         "ttlSeconds": 60, "onConflict": "Abort"}),
                transition("case-timer", "timer-zone", "Target state's timer script throws"),
                transition("case-rule", "rule-zone", "Target state's automatic rule throws"),
            ]),
            state("timer-zone", 2, "Timer zone", [
                {"key": "timer-fire", "target": "landed", "triggerType": 2, "versionStrategy": "Patch",
                 "labels": labels("Timer"), "onExecutionTasks": [], "timer": code("EbThrowTimer")},
            ]),
            state("rule-zone", 2, "Rule zone", [
                {"key": "rule-fire", "target": "landed", "triggerType": 1, "versionStrategy": "Patch",
                 "labels": labels("Rule"), "onExecutionTasks": [], "rule": code("EbThrowRule")},
            ]),
            state("landed", 3, "Landed", []),
        ],
    },
}

(HERE / f"{KEY}.json").write_text(json.dumps(workflow, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print(f"wrote {KEY}.json")
