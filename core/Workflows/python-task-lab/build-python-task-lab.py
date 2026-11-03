#!/usr/bin/env python3
"""Generates python-task-lab: its PythonTask (type 23) components and the workflow.

    python3 core/Workflows/python-task-lab/build-python-task-lab.py

Every case is a transition from `ready` to `landed` running one Python task with the shared
PtlResultProjection mapping. Bump VERSION when a fixture changes — publish is version-immutable.
"""
import base64
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
TASKS = HERE.parents[1] / "Tasks" / "python-task-lab"
VERSION = "1.0.0"
MAPPING = "PtlResultProjection.csx"

# task key -> (handler, input, timeoutSeconds)
TASK_SPECS = {
    "ptl-sum": ("ptl_sum", {"values": [1, 1]}, 10),
    "ptl-context": ("ptl_context", None, 7),
    "ptl-dateutil": ("ptl_dateutil", {"date": "2026-11-03"}, 10),
    "ptl-fail": ("ptl_fail", {"reason": "by design"}, 10),
    "ptl-unknown": ("ptl_missing", {}, 10),
    "ptl-slow": ("ptl_slow", {"seconds": 3}, 1),
    "ptl-nan": ("ptl_nan", {}, 10),
}

# transition key -> (task key, label, error boundary or None)
CASES = {
    "case-sum": ("ptl-sum", "Input from the transition body through the mapping", None),
    "case-static-input": ("ptl-sum", "Authored config.input (body has no values)", None),
    "case-context": ("ptl-context", "Handler reads the call context", None),
    "case-dependency": ("ptl-dateutil", "Handler imports a package installed at image build", None),
    "case-fail": ("ptl-fail", "Handler raises -> 422 handler_error, no boundary", None),
    "case-unknown": ("ptl-unknown", "Handler not in the image -> 404 unknown_handler", None),
    "case-timeout": ("ptl-slow", "Handler exceeds timeoutSeconds -> 504 timeout", None),
    "case-invalid-output": ("ptl-nan", "Handler returns NaN -> 422 invalid_output", None),
    "case-fail-abort": ("ptl-fail", "422 matched by a task-level Abort boundary -> instance faults",
                        {"onError": [{"action": 0, "errorCodes": ["422"], "priority": 100}]}),
}


def label(text):
    return [{"language": "en-US", "label": text}]


def task_component(key, handler, input_value, timeout):
    config = {"handler": handler, "timeoutSeconds": timeout}
    if input_value is not None:
        config["input"] = input_value
    return {
        "key": key, "version": VERSION, "domain": "core", "flow": "sys-tasks", "flowVersion": "1.0.0",
        "tags": ["integration-test", "python-task-lab", "python-task"],
        "attributes": {"type": "23", "config": config},
    }


def transition(key, task_key, text, boundary, code):
    entry = {
        "order": 1,
        "task": {"key": task_key, "domain": "core", "version": VERSION, "flow": "sys-tasks"},
        "mapping": {"location": f"./src/{MAPPING}", "code": code},
    }
    if boundary:
        entry["errorBoundary"] = boundary
    return {"key": key, "target": "landed", "triggerType": 0, "versionStrategy": "Patch",
            "labels": label(text), "onExecutionTasks": [entry]}


def state(key, state_type, sub_type, text, transitions):
    return {"key": key, "stateType": state_type, "subType": sub_type, "versionStrategy": "Major",
            "labels": label(text), "view": None, "subFlow": None, "onEntries": [], "onExits": [],
            "transitions": transitions}


def main():
    TASKS.mkdir(parents=True, exist_ok=True)
    for key, (handler, input_value, timeout) in TASK_SPECS.items():
        (TASKS / f"{key}.json").write_text(
            json.dumps(task_component(key, handler, input_value, timeout), indent=2) + "\n")

    code = base64.b64encode((HERE / "src" / MAPPING).read_bytes()).decode()
    workflow = {
        "key": "python-task-lab", "flow": "sys-flows", "flowVersion": "1.0.0", "domain": "core",
        "version": VERSION, "tags": ["integration-test", "python-task-lab", "python-task"],
        "attributes": {
            "type": "F", "timeout": None, "labels": label("Python Task Lab (PythonTask over the runtime contract)"),
            "functions": [], "features": [], "extensions": [],
            "startTransition": {"key": "start-python-task-lab", "target": "ready", "triggerType": 0,
                                "versionStrategy": "Major", "labels": label("Start"), "onExecutionTasks": []},
            "states": [
                state("ready", 1, 0, "Ready (pick a case)",
                      [transition(k, t, text, b, code) for k, (t, text, b) in CASES.items()]),
                state("landed", 3, 1, "Landed (the case ran to completion)", []),
            ],
        },
    }
    (HERE / "python-task-lab.json").write_text(json.dumps(workflow, indent=2) + "\n")


if __name__ == "__main__":
    main()
