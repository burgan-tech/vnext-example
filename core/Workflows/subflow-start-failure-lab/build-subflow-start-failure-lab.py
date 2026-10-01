#!/usr/bin/env python3
"""Regenerates subflow-start-failure-lab-parent.json from the .csx sources in ./src.

The workflow JSON embeds every mapping / rule as base64 in `code` next to its `location`;
edit the .csx files and re-run this script — never hand-edit the base64 blobs.

    python3 core/Workflows/subflow-start-failure-lab/build-subflow-start-failure-lab.py
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent


def code(name):
    raw = (ROOT / "src" / name).read_bytes()
    return {
        "location": f"./src/{name}",
        "code": base64.b64encode(raw).decode(),
    }


def script_task():
    return {"key": "subflow-script-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}


def label(text):
    return [{"language": "en-US", "label": text}]


workflow = {
    "key": "subflow-start-failure-lab-parent",
    "flow": "sys-flows",
    "flowVersion": "1.0.0",
    "domain": "core",
    "version": "1.0.1",
    "tags": [
        "integration-test",
        "subflow-start-failure-lab",
        "parent",
        "subflow",
        "post-commit-fault",
        "retry",
    ],
    "attributes": {
        "type": "F",
        "timeout": None,
        "labels": label("SubFlow Start Failure Lab Parent"),
        "functions": [],
        "features": [],
        "extensions": [],
        "startTransition": {
            "key": "start-subflow-start-failure-lab-parent",
            "target": "parent-initial",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start Parent"),
            "onExecutionTasks": [
                {"order": 1, "task": script_task(), "mapping": code("ParentStartMapping.csx")}
            ],
        },
        "states": [
            {
                "key": "parent-initial",
                "stateType": 1,
                "subType": 0,
                "versionStrategy": "Major",
                "labels": label("Parent Initial"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [
                    {
                        "key": "auto-parent-to-subflow",
                        "target": "parent-subflow-state",
                        "triggerType": 1,
                        "versionStrategy": "Minor",
                        "labels": label("Auto to Parent SubFlow State"),
                        "rule": code("AlwaysTrueRule.csx"),
                        "onExecutionTasks": [],
                    }
                ],
            },
            {
                # stateType 4 = SubFlow. subFlow.type MUST be "S" here: a state-level "P" (SubProcess)
                # is rejected at publish (WorkflowValidator, ~line 386) — use a SubProcess TASK instead
                # for that case. This lab is specifically about the "S" post-commit-fault path.
                #
                # FORM TRIED FIRST (cheaper): subFlow.process pointing at a real child key
                # (subflow-orchestration-child) but a version that was never published ("9.9.9").
                # `wf sync` ACCEPTED that at publish time — no reference-consistency check walks
                # into subFlow.process versions. But starting the PARENT then failed synchronously,
                # BEFORE any instance/correlation existed at all: IComponentCacheStore resolves the
                # whole reachable component graph (including every subFlow.process) when the
                # parent's own definition is loaded, so the 404 on "9.9.9" surfaced as a plain
                # StartInstance 404 on the PARENT itself (error target
                # "core/subflow-orchestration-child@9.9.9", Cache:300001) — never reaching
                # HandleSubFlowStep, so no InstanceCorrelation was ever committed. That does not
                # reproduce the bug under test (a correlation committed BEFORE the child-start
                # failure), so this lab uses the schema fallback below instead.
                "key": "parent-subflow-state",
                "stateType": 4,
                "subType": 0,
                "versionStrategy": "Minor",
                "labels": label("Parent SubFlow State (child start fails schema validation)"),
                "view": None,
                "subFlow": {
                    "type": "S",
                    "process": {
                        # A REAL, resolvable child (own component, own published version) so the
                        # component-cache graph load for the PARENT succeeds and the parent reaches
                        # parent-subflow-state normally. HandleSubFlowStep (order 70) commits the
                        # InstanceCorrelation in its own UoW; only THEN does the post-commit
                        # StartSubflowJob call subflow-start-failure-lab-child's start transition,
                        # whose schema requires "mustProvide" — a field
                        # ParentToChildSubFlowMapping.csx deliberately never supplies. That failure
                        # is classified Validation ("client error") by DefaultPostCommitFailurePolicy,
                        # but TransitionRunner.CompensateFailedCoordinationAsync faults the parent
                        # unconditionally for a failed StartSubflowJob regardless of that
                        # classification (see its "E31" comment) — so the fault path under test
                        # fires from a genuinely committed-correlation state.
                        "key": "subflow-start-failure-lab-child",
                        "domain": "core",
                        "version": "1.0.0",
                        "flow": "sys-flows",
                    },
                    "mapping": code("ParentToChildSubFlowMapping.csx"),
                },
                "onEntries": [],
                "onExits": [],
                "transitions": [
                    {
                        # Unreachable in this fixture (HandleSubFlowStep skips straight to Finalize for
                        # subFlow.type "S"), kept only so the workflow has a nameable path to completion
                        # if the reference were ever made resolvable.
                        "key": "auto-parent-to-completed",
                        "target": "parent-completed",
                        "triggerType": 1,
                        "versionStrategy": "Minor",
                        "labels": label("Auto to Parent Completed"),
                        "rule": code("AlwaysTrueRule.csx"),
                        "onExecutionTasks": [],
                    }
                ],
            },
            {
                "key": "parent-completed",
                "stateType": 3,
                "subType": 1,
                "versionStrategy": "Major",
                "labels": label("Parent Completed"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [],
            },
        ],
    },
}

out = ROOT / "subflow-start-failure-lab-parent.json"
out.write_text(json.dumps(workflow, indent=2) + "\n")
print(f"wrote {out}")
