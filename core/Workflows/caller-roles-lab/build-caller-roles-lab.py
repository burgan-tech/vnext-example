#!/usr/bin/env python3
"""Regenerates the caller-roles-lab workflows from the .csx sources in ./src (vnext script-caller-roles,
council 2026-11-12-script-caller-roles-background): context.Caller in an automatic rule, a timer script and a
SubFlow output mapping. Edit the .csx files and re-run — never hand-edit the base64 blobs.

    python3 core/Workflows/caller-roles-lab/build-caller-roles-lab.py
"""
import base64, json
from pathlib import Path

ROOT = Path(__file__).resolve().parent


def code(name):
    return {"location": f"./src/{name}", "code": base64.b64encode((ROOT / "src" / name).read_bytes()).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def state(key, state_type, transitions=None, sub_type=0, **extra):
    s = {"key": key, "stateType": state_type, "subType": sub_type, "versionStrategy": "Major", "labels": label(key),
         "view": None, "subFlow": None, "onEntries": [], "onExits": [], "transitions": transitions or []}
    s.update(extra)
    return s


def tr(key, target, trigger, **extra):
    return {"key": key, "target": target, "triggerType": trigger, "versionStrategy": "Major", "labels": label(key), **extra}


def flow(key, start_target, states, tags):
    return {"key": key, "flow": "sys-flows", "flowVersion": "1.0.0", "domain": "core", "version": "1.0.0",
            "tags": ["integration-test", "caller-roles-lab", *tags],
            "attributes": {"type": "F", "timeout": None, "labels": label(key), "functions": [], "features": [],
                           "extensions": [], "startTransition": tr(f"start-{key}", start_target, 0), "states": states}}


flows = {
    # Automatic rule by role: approvers go to `approved`, everyone else takes the DefaultAutoTransition.
    "caller-roles-auto-lab": flow("caller-roles-auto-lab", "gate", [
        state("gate", 1, [
            tr("to-approved", "approved", 1, rule=code("IsApproverRole.csx"), priority=10),
            tr("to-standard", "standard", 1, triggerKind=10),
        ]),
        state("approved", 3, sub_type=1),
        state("standard", 3, sub_type=1),
    ], ["automatic-transition"]),
    # Timer by role, computed at arming (state entry).
    "caller-roles-timer-lab": flow("caller-roles-timer-lab", "wait", [
        state("wait", 1, [tr("expire", "expired", 2, timer=code("ApproverWindowTimer.csx"))]),
        state("expired", 3, sub_type=1),
    ], ["timer"]),
    # SubFlow output mapping reads the child settler's roles.
    "caller-roles-child-lab": flow("caller-roles-child-lab", "child-review", [
        state("child-review", 1, [tr("child-complete", "child-done", 0)]),
        state("child-done", 3, sub_type=1),
    ], ["subflow"]),
    "caller-roles-parent-lab": flow("caller-roles-parent-lab", "parent-wait", [
        state("parent-wait", 4,
              [tr("to-parent-done", "parent-done", 1, rule=code("AlwaysTrueRule.csx"))],
              subFlow={"type": "S",
                       "process": {"key": "caller-roles-child-lab", "domain": "core", "version": "1.0.0", "flow": "sys-flows"},
                       "mapping": code("CallerAwareSubFlowMapping.csx")}),
        state("parent-done", 3, sub_type=1),
    ], ["subflow"]),
}

for key, doc in flows.items():
    path = ROOT / f"{key}.json"
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n")
    print(f"wrote {path.relative_to(ROOT.parents[2])}")
