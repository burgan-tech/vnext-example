#!/usr/bin/env python3
"""Regenerates fan-out-config-matrix.json from the .csx sources in ./src.

The workflow JSON embeds every mapping as base64 in `code` next to its `location`;
edit the .csx files and re-run this script — never hand-edit the base64 blobs.

    python3 core/Workflows/fan-out-config-matrix/build-fan-out-config-matrix.py

WHY THIS WORKFLOW HAS THE SHAPE IT HAS
--------------------------------------
Everything the FanOutTask configurable surface varies — join.policy, join.minSuccess, mode,
execution.maxDegreeOfParallelism / itemTimeoutSeconds / batchTimeoutSeconds, the per-item
errorBoundary — lives in the TASK component's own config. It is static per component and there is
no runtime or SDK way for a caller to supply it. So the config axis is necessarily one task
component per variant.

What CAN be collapsed is the workflow: one dispatcher state (`matrix-ready`) with one manual
transition per case, each landing in a state whose onEntry runs exactly that case's fan-out task.
The item mix (which documents succeed / fail / straggle) comes from instance data at start, so it
IS caller-parameterised. That is the smallest possible set: 1 workflow, N task components,
1 test class — instead of N near-identical workflows.

Each case state carries ONE unconditional auto transition to the shared `case-settled` finish
state. That makes the join outcome directly observable without reading task internals:

  * join SUCCEEDED -> the onEntry task succeeds -> the auto transition fires -> `case-settled` (C)
  * join FAILED    -> the onEntry task fails -> no error boundary is configured anywhere in this
                      workflow (deliberately: the fault IS the observable) -> instance Faulted (F),
                      still sitting in the case state.

Do NOT add a state-level or workflow-level errorBoundary here. It would convert every failed-join
case into a silent success and half this matrix would stop testing anything.
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent

# BUMP THIS whenever any .csx under ./src changes. POST /api/v1/definitions/publish has no
# overwrite: an unchanged version answers 409 "A record with the same version already exists" and
# the runtime keeps serving the OLD embedded scripts, so an edited mapping silently does nothing.
# 1.0.0 — initial configurable-surface matrix (join policies, minSuccess, timeouts, mdop,
#         per-item errorBoundary, empty collection).
VERSION = "1.0.0"


def code(name):
    raw = (ROOT / "src" / name).read_bytes()
    return {
        "location": f"./src/{name}",
        "code": base64.b64encode(raw).decode(),
    }


def label(text):
    return [{"language": "en-US", "label": text}]


def task_ref(key):
    return {"key": key, "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}


# (transition key, state key, fan-out task component key, human label)
#
# One row per configuration under test. The transition key is what the integration test fires; the
# task component key is the only thing that differs between rows.
CASES = [
    (
        "run-join-all",
        "case-join-all",
        "fanout-case-join-all-task",
        "Join: all",
    ),
    (
        "run-join-all-settled",
        "case-join-all-settled",
        "fanout-case-join-all-settled-task",
        "Join: allSettled",
    ),
    (
        "run-join-quorum",
        "case-join-quorum",
        "fanout-case-join-quorum-task",
        "Join: quorum (minSuccess 2)",
    ),
    (
        "run-join-first-success",
        "case-join-first-success",
        "fanout-case-join-first-success-task",
        "Join: firstSuccess",
    ),
    (
        "run-item-timeout",
        "case-item-timeout",
        "fanout-case-item-timeout-task",
        "Item timeout (itemTimeoutSeconds 1, batchTimeoutSeconds 30)",
    ),
    (
        "run-batch-timeout-serial",
        "case-batch-timeout-serial",
        "fanout-case-batch-timeout-serial-task",
        "Batch timeout, serial (mdop 1, both timeouts 2s)",
    ),
    (
        "run-parallel-baseline",
        "case-parallel-baseline",
        "fanout-case-parallel-baseline-task",
        "Parallel control arm (mdop 4, both timeouts 2s)",
    ),
    (
        "run-item-boundary-ignore",
        "case-item-boundary-ignore",
        "fanout-case-item-boundary-ignore-task",
        "Per-item errorBoundary: ignore, under join all",
    ),
    (
        "run-item-boundary-retry",
        "case-item-boundary-retry",
        "fanout-case-item-boundary-retry-task",
        "Per-item errorBoundary: retry, under join allSettled",
    ),
]

SETTLED_STATE = "case-settled"


def dispatcher_transitions():
    return [
        {
            "key": transition_key,
            "target": state_key,
            "triggerType": 0,
            "versionStrategy": "Patch",
            "labels": label(text),
            "onExecutionTasks": [],
        }
        for transition_key, state_key, _task_key, text in CASES
    ]


def case_state(transition_key, state_key, task_key, text):
    return {
        "key": state_key,
        "stateType": 2,
        "subType": 0,
        "versionStrategy": "Patch",
        "labels": label(text),
        "view": None,
        "subFlow": None,
        # Exactly ONE onEntry task: the fan-out batch under test. Nothing else, so a failure here
        # can only be the batch's join verdict.
        "onEntries": [
            {"order": 1, "task": task_ref(task_key), "mapping": code("FanOutCaseMapping.csx")},
        ],
        "onExits": [],
        # Unconditional (no `rule`) — a lone auto transition is valid precisely when it always
        # fires. It only gets the chance if the onEntry batch succeeded, which is the whole point:
        # reaching `case-settled` == the join succeeded.
        "transitions": [
            {
                "key": f"auto-settled-from-{state_key}",
                "target": SETTLED_STATE,
                "triggerType": 1,
                "versionStrategy": "Patch",
                "labels": label("Case Settled"),
                "onExecutionTasks": [],
            }
        ],
    }


workflow = {
    "key": "fan-out-config-matrix",
    "flow": "sys-flows",
    "flowVersion": "1.0.0",
    "domain": "core",
    "version": VERSION,
    "tags": [
        "integration-test",
        "fan-out-config-matrix",
        "fan-out",
        "task-type-21",
        "join-policy",
        "min-success",
        "item-error-boundary",
        "timeouts",
        "max-degree-of-parallelism",
    ],
    "attributes": {
        "type": "F",
        "timeout": None,
        "labels": label("Fan-Out Config Matrix"),
        "functions": [],
        "features": [],
        "extensions": [],
        # NO errorBoundary — see the module docstring. The fault IS the observable.
        "startTransition": {
            "key": "start-fan-out-config-matrix",
            "target": "matrix-ready",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start Fan-Out Config Matrix"),
            "onExecutionTasks": [],
        },
        "states": [
            {
                "key": "matrix-ready",
                "stateType": 1,
                "subType": 0,
                "versionStrategy": "Major",
                "labels": label("Matrix Ready (pick a case)"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": dispatcher_transitions(),
            },
        ]
        + [case_state(*row) for row in CASES]
        + [
            {
                "key": SETTLED_STATE,
                "stateType": 3,
                "subType": 1,
                "versionStrategy": "Major",
                "labels": label("Case Settled (join succeeded)"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [],
            },
            {
                "key": "case-cancelled",
                "stateType": 3,
                "subType": 7,
                "versionStrategy": "Major",
                "labels": label("Case Cancelled"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [],
            },
        ],
        "cancel": {
            "key": "cancel-fan-out-config-matrix",
            "target": "case-cancelled",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Cancel Fan-Out Config Matrix"),
        },
    },
}


def main():
    out = ROOT / "fan-out-config-matrix.json"
    out.write_text(json.dumps(workflow, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {out} ({len(CASES)} cases)")


if __name__ == "__main__":
    main()
