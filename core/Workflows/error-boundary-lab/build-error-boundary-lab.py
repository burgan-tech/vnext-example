#!/usr/bin/env python3
"""Regenerates the error-boundary-lab workflows from the .csx sources in ./src.

    python3 core/Workflows/error-boundary-lab/build-error-boundary-lab.py

Two workflows are emitted, and they have to be two:

  error-boundary-lab         — NO workflow-level boundary. Task- and state-level cases, the
                               ordering cases, retry, the continue-style actions, and the
                               "no boundary anywhere" control.
  error-boundary-lab-global  — one workflow-level boundary. Global-level cases and the
                               "boundaries exist but none match" control.

WHY TWO
-------
`TaskExecutionEngine` asks `CompiledBoundaryChain.HasAnyBoundary` before it acts on a task failure.
A workflow-level boundary makes that true for EVERY task in the workflow, so the control case
"no boundary is configured anywhere" cannot exist in the same workflow as a global boundary. The
two controls are different behaviours and both matter:

  U1 (no boundary anywhere)      -> the failure is not acted on; the pipeline CONTINUES, no
                                    incident is recorded, the instance does not fault.
  U2 (boundaries exist, no match)-> the failure is unhandled; the instance FAULTS and an incident
                                    is recorded with boundaryAction/boundaryLevel both null.

SHAPE
-----
One hub state (`ready` / `g-ready`) with one manual transition per case. Each case either
  (a) carries the failing task on the TRANSITION (onExecutionTasks) — those see the boundary of the
      state the instance is IN, i.e. the hub, because OnExecute (order 30) runs before ChangeState
      (50); or
  (b) targets a "zone" state whose onEntries carry the failing task — those see the ZONE's
      boundary, because OnEntry (60) runs after ChangeState (50).
That split is the only way to place a state-level boundary deterministically, and it is exactly
what `TaskExecutionEngine.GetStateBoundary` (which reads `instance.CurrentState`) implies.

RULES THE VALIDATOR ENFORCES (all of these are publish-blocking)
---------------------------------------------------------------
  * `action` and `backoffType` are INTEGER enums here (0..5 / 0..1). The schema shipped with
    vnext-schema types them as integers; the string spellings some older components use are why
    those components fail `npm run validate`.
  * abort (0) must NOT carry a transition — abort-without-transition IS the fault path.
  * rollback (2) and notify (4) MUST carry a transition.
  * retry (1) MUST carry a retryPolicy.
  * a boundary's transition key must exist somewhere in the workflow (start / cancel / shared /
    any state's transitions). It does NOT have to be available from the current state: an
    error-boundary transition runs with IsErrorBoundaryTransition and bypasses the state list.
  * duplicate EffectivePriority inside ONE boundary is rejected. A wildcard rule left at the
    default priority (100) is demoted to 999, so two default-priority wildcards collide.
  * auto transitions (triggerType 1) must carry a rule — the lab has none, on purpose.

ERROR CODES IN RULES ARE STATUS STRINGS ("500"/"503"), NEVER "Task:Http:...".
The retry decision matches against the NORMALIZED code, but the post-exhaustion fallback
(`ResolveExcluding`) matches against the ORIGINAL code (null for HTTP/script failures) plus the
status code. Only the status string matches on both paths; a `Task:...` code would silently work
for the retry rule and then fail to match its own fallback.

TASK COMPONENT PER SLOT
-----------------------
The task journal key is (TransitionId, TaskId): the same task component twice in one transition is
silently skipped. Hence eb-http-500-task-a..i and three marker components rather than one of each.
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent

# BUMP on every .csx or shape change. A published version is immutable: re-publishing answers 409
# and the runtime keeps serving the OLD embedded scripts, so an edited mapping silently does
# nothing until the version moves.
# 1.0.0 — initial lab: task/state/global levels, ordering, retry, continue actions, controls,
#         retry endpoint and the queryRoles-gated incident read.
# 1.1.0 — EbHttpMapping counts attempts and records the last status code. MEASURED: the output
#         handler runs on failed attempts too, so the old httpSucceeded stamp was true even when
#         the call had failed; httpAttempts/httpLastStatus are what actually discriminate, and
#         httpAttempts is how the retry tests prove a recovery without reading MockLab's logs.
VERSION = "1.1.0"

VIEWER_ROLE = "eb-lab.viewer"

RETRY_POLICY_FIXED = {
    # Fixed backoff (0) with jitter off keeps the wall-clock deterministic: 1 + maxRetries calls,
    # initialDelay apart. Exponential + jitter would make the request-count assertions flaky.
    "maxRetries": 2,
    "initialDelay": "PT0.2S",
    "backoffType": 0,
    "backoffMultiplier": 2,
    "maxDelay": "PT1S",
    "useJitter": False,
}

RETRY_POLICY_RECOVER = dict(RETRY_POLICY_FIXED, maxRetries=3)


def code(name):
    raw = (ROOT / "src" / name).read_bytes()
    return {"location": f"./src/{name}", "code": base64.b64encode(raw).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def task_ref(key):
    return {"key": key, "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}


SCRIPT_MAPPING = "EbScriptFailMapping.csx"
HTTP_MAPPING = "EbHttpMapping.csx"
MARKER_MAPPING = "EbMarkerMapping.csx"


def hook(order, task_key, mapping, boundary=None):
    entry = {"order": order, "task": task_ref(task_key), "mapping": code(mapping)}
    if boundary is not None:
        entry["errorBoundary"] = {"onError": boundary}
    return entry


def transition(key, target, text, tasks=None, trigger=0):
    return {
        "key": key,
        "target": target,
        "triggerType": trigger,
        "versionStrategy": "Patch",
        "labels": label(text),
        "onExecutionTasks": tasks or [],
    }


def state(key, text, state_type=2, sub_type=0, entries=None, transitions=None, boundary=None,
          query_roles=None):
    node = {
        "key": key,
        "stateType": state_type,
        "subType": sub_type,
        "versionStrategy": "Major" if state_type != 2 else "Patch",
        "labels": label(text),
        "view": None,
        "subFlow": None,
        "onEntries": entries or [],
        "onExits": [],
        "transitions": transitions or [],
    }
    if boundary is not None:
        node["errorBoundary"] = {"onError": boundary}
    if query_roles is not None:
        node["queryRoles"] = query_roles
    return node


def terminal(key, text, sub_type):
    return state(key, text, state_type=3, sub_type=sub_type)


# ── error-boundary-lab ───────────────────────────────────────────────────────
#
# Case transitions that carry their own failing task. `to-rolled-back` / `to-notified-from-ready`
# are the boundary targets these cases route to; they are ordinary manual transitions on the hub.
LAB_CASES = [
    transition(
        "case-task-abort", "landed", "Task-level abort (instance faults)",
        [hook(1, "eb-script-fail-task", SCRIPT_MAPPING, [{"action": 0, "priority": 999}])]),
    transition(
        "case-task-rollback", "landed", "Task-level rollback to a target transition",
        [hook(1, "eb-http-500-task-b", HTTP_MAPPING,
              [{"action": 2, "transition": "to-rolled-back", "priority": 999}])]),
    transition(
        # Two rules, both left at the DEFAULT priority (100). The wildcard is demoted to 999 by
        # EffectivePriority, so the specific one wins — and the two do not collide, which a pair of
        # wildcards at the default would.
        "case-within-level-order", "landed", "Within-level: specific rule beats default wildcard",
        [hook(1, "eb-http-500-task-c", HTTP_MAPPING, [
            {"action": 0},
            {"action": 2, "errorCodes": ["500"], "transition": "to-rolled-back"},
        ])]),
    transition(
        # Both rules match; the lower priority number wins regardless of definition order.
        "case-within-level-priority", "landed", "Within-level: lower priority number wins",
        [hook(1, "eb-http-500-task-d", HTTP_MAPPING, [
            {"action": 2, "errorCodes": ["500"], "transition": "to-rolled-back", "priority": 200},
            {"action": 4, "errorCodes": ["500"], "transition": "to-notified-from-ready", "priority": 50},
        ])]),
    transition(
        # Retry exhausts (1 + 2 calls), then the chain is searched again EXCLUDING retry rules and
        # the abort fallback applies.
        "case-retry-exhaust", "landed", "Retry exhausts, falls back to abort",
        [hook(1, "eb-http-500-task-e", HTTP_MAPPING, [
            {"action": 1, "errorCodes": ["500"], "priority": 10, "retryPolicy": RETRY_POLICY_FIXED},
            {"action": 0, "priority": 999},
        ])]),
    transition(
        # The flaky MockLab endpoint answers 500,500,200: the third attempt succeeds and the task
        # is a plain success — no incident, no boundary outcome.
        "case-retry-recover", "landed", "Retry recovers on the third attempt",
        [hook(1, "eb-http-flaky-task", HTTP_MAPPING, [
            {"action": 1, "errorCodes": ["500"], "priority": 10, "retryPolicy": RETRY_POLICY_RECOVER},
        ])]),
    transition(
        "case-ignore", "landed", "Ignore: pipeline continues (does the hook finish?)",
        [hook(1, "eb-http-500-task-f", HTTP_MAPPING, [{"action": 3, "priority": 999}]),
         hook(2, "eb-marker-script-task", MARKER_MAPPING)]),
    transition(
        "case-log", "landed", "Log: pipeline continues (does the hook finish?)",
        [hook(1, "eb-http-500-task-g", HTTP_MAPPING, [{"action": 5, "priority": 999}]),
         hook(2, "eb-marker-script-task-2", MARKER_MAPPING)]),
    transition(
        # U1 control: no boundary at any level for this task.
        "case-unhandled", "landed", "No boundary anywhere (control)",
        [hook(1, "eb-http-500-task-h", HTTP_MAPPING),
         hook(2, "eb-marker-script-task-3", MARKER_MAPPING)]),
    transition(
        # Same shape as case-task-abort but its own transition, so the retry-endpoint tests never
        # share an instance with the abort test.
        "case-retry-endpoint", "landed", "Task-level abort, used by the retry endpoint tests",
        [hook(1, "eb-script-fail-task", SCRIPT_MAPPING, [{"action": 0, "priority": 999}])]),
    # Zone-targeting cases: the failing task lives on the zone's onEntry.
    transition("case-state-notify", "zone-state-notify", "State-level notify"),
    transition("case-precedence-task-over-state", "zone-precedence", "Task level beats state level"),
    transition("case-secured", "zone-secured", "Faults inside a queryRoles-gated state"),
    # Boundary targets reachable from the hub.
    transition("to-rolled-back", "rolled-back", "Boundary target: rolled back"),
    transition("to-notified-from-ready", "notified", "Boundary target: notified"),
]

LAB_STATES = [
    state("ready", "Ready (pick a case)", state_type=1, transitions=LAB_CASES),
    state(
        "zone-state-notify", "Zone: state-level notify",
        entries=[hook(1, "eb-http-500-task-a", HTTP_MAPPING)],
        boundary=[{"action": 4, "transition": "to-notified", "priority": 999}],
        transitions=[transition("to-notified", "notified", "Boundary target: notified")],
    ),
    state(
        # The task rule and the state rule both match. The state rule is MORE specific and has the
        # lower priority number, and still loses: level dominance (Task -> State -> Global) is
        # decided before priority is even read.
        "zone-precedence", "Zone: task boundary beats state boundary",
        entries=[hook(1, "eb-script-fail-task", SCRIPT_MAPPING, [{"action": 0, "priority": 999}])],
        boundary=[{"action": 2, "errorCodes": ["500"], "transition": "to-rolled-back-from-zone",
                   "priority": 1}],
        transitions=[transition("to-rolled-back-from-zone", "rolled-back", "Boundary target: rolled back")],
    ),
    state(
        # queryRoles here (not on the workflow root) so starting and firing the case stay open while
        # every READ of this instance — state function and the incident history — is gated.
        "zone-secured", "Zone: reads gated by queryRoles",
        entries=[hook(1, "eb-script-fail-task", SCRIPT_MAPPING, [{"action": 0, "priority": 999}])],
        query_roles=[{"role": VIEWER_ROLE, "grant": "allow"}],
    ),
    terminal("landed", "Landed (the case ran to the end)", 1),
    terminal("rolled-back", "Rolled back by a boundary", 2),
    terminal("notified", "Notified by a boundary", 2),
    terminal("eb-cancelled", "Cancelled", 7),
]

LAB = {
    "key": "error-boundary-lab",
    "flow": "sys-flows",
    "flowVersion": "1.0.0",
    "domain": "core",
    "version": VERSION,
    "tags": ["integration-test", "error-boundary-lab", "error-boundary", "incident", "retry"],
    "attributes": {
        "type": "F",
        "timeout": None,
        "labels": label("Error Boundary Lab"),
        "functions": [],
        "features": [],
        "extensions": [],
        # NO workflow-level errorBoundary — see the module docstring (U1 control).
        "startTransition": {
            "key": "start-error-boundary-lab",
            "target": "ready",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start Error Boundary Lab"),
            "onExecutionTasks": [],
        },
        "states": LAB_STATES,
        "cancel": {
            "key": "cancel-error-boundary-lab",
            "target": "eb-cancelled",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Cancel Error Boundary Lab"),
        },
    },
}

# ── error-boundary-lab-global ────────────────────────────────────────────────
GLOBAL_CASES = [
    transition(
        "g-case-global-rollback", "landed", "Global-level rollback",
        [hook(1, "eb-http-503-task", HTTP_MAPPING)]),
    transition(
        # 500 does not match the global rule's ["503"], so nothing handles it: U2.
        "g-case-unhandled-no-match", "landed", "Boundaries exist, none match (control)",
        [hook(1, "eb-http-500-task-i", HTTP_MAPPING)]),
    transition("g-case-state-over-global", "g-zone", "State level beats global level"),
    transition("g-to-rolled-back", "rolled-back", "Boundary target: rolled back"),
]

GLOBAL_STATES = [
    state("g-ready", "Ready (pick a case)", state_type=1, transitions=GLOBAL_CASES),
    state(
        "g-zone", "Zone: state boundary shadows the global one",
        entries=[hook(1, "eb-http-503-task-b", HTTP_MAPPING)],
        boundary=[{"action": 4, "errorCodes": ["503"], "transition": "g-to-notified", "priority": 999}],
        transitions=[transition("g-to-notified", "notified", "Boundary target: notified")],
    ),
    terminal("landed", "Landed (the case ran to the end)", 1),
    terminal("rolled-back", "Rolled back by a boundary", 2),
    terminal("notified", "Notified by a boundary", 2),
    terminal("eb-cancelled", "Cancelled", 7),
]

GLOBAL = {
    "key": "error-boundary-lab-global",
    "flow": "sys-flows",
    "flowVersion": "1.0.0",
    "domain": "core",
    "version": VERSION,
    "tags": ["integration-test", "error-boundary-lab", "error-boundary", "global-boundary"],
    "attributes": {
        "type": "F",
        "timeout": None,
        "labels": label("Error Boundary Lab (global boundary)"),
        "functions": [],
        "features": [],
        "extensions": [],
        # Scoped to 503 on purpose: it must NOT catch the 500 the U2 control raises.
        "errorBoundary": {
            "onError": [
                {"action": 2, "errorCodes": ["503"], "transition": "g-to-rolled-back", "priority": 999}
            ]
        },
        "startTransition": {
            "key": "start-error-boundary-lab-global",
            "target": "g-ready",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start Error Boundary Lab (global)"),
            "onExecutionTasks": [],
        },
        "states": GLOBAL_STATES,
        "cancel": {
            "key": "cancel-error-boundary-lab-global",
            "target": "eb-cancelled",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Cancel Error Boundary Lab (global)"),
        },
    },
}


def main():
    for workflow in (LAB, GLOBAL):
        out = ROOT / f"{workflow['key']}.json"
        out.write_text(json.dumps(workflow, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        cases = len(workflow["attributes"]["states"][0]["transitions"])
        print(f"wrote {out} ({cases} transitions on the hub)")


if __name__ == "__main__":
    main()
