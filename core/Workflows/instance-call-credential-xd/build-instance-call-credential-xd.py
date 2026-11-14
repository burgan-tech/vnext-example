#!/usr/bin/env python3
"""Regenerates the instance-call-credential-xd components (cross-domain lab: core + partner).

Pins the caller-credential rule on instance calls ACROSS the domain boundary (build plan:
labs/cross-domain/VNEXT-BUILD-PLAN-instance-call-credential.md, XC-01 … XC-08). Same-domain twin:
core/Workflows/instance-call-credential-lab/.

core
* icc-xd-parent (F) — start → icc-xd-p-ready (rests Active). The test drives ONE manual transition with its own
  sub / act_sub (/ role / userId) headers; the start data's `variant` picks the mapping headers:
    do-subflow    → icc-xd-p-subflow   (SubFlow S → partner icc-xd-sub,      IccXdSubFlowMapping: none|blank)
    do-nest       → icc-xd-p-nest      (SubFlow S → partner icc-xd-sub-nest, IccXdSubFlowMapping: none|blank)
    do-subprocess / do-start / do-trigger / do-get / do-list / do-data
                  → onEntry task 14 / 11 / 12 / 19 / 15 / 13 against partner icc-xd-target (useDapr), IccXdCallMapping
  every call state then goes on (default auto) to icc-xd-p-done.
* icc-xd-core-target (F, schema icc-xd-core-target-master: `starterNote` x-roles allow $InstanceBehalfOfStarter) —
  the XC-06 target the partner child calls back into: start → record → icc-t-wait (mode = wait) → `touch` records
  `touchSeen`.

partner
* icc-xd-target (F, schema icc-xd-target-master: `secretNote` x-roles allow icc.reader) — record on start;
  mode = wait parks in icc-t-wait, whose `touch` records `touchSeen`.
* icc-xd-sub (S) — record on start, finish.
* icc-xd-sub-nest (S) — record on start, then DirectTrigger + GetInstance back into core's icc-xd-core-target with
  NO mapping headers (XC-06), finish.

Edit ./src/*.csx (both roots) and re-run — never hand-edit the base64 blobs (labs/cross-domain/encode-scripts.py
re-encodes the same blobs from `location` and must leave them unchanged). Publish is version-immutable: bump
VERSION (patch) for any change to an already-published component — and keep the SetVersion("…") calls in
src/IccXdCallMapping.csx equal to VERSION: with only the task config's `version` the cross-domain Start answered
404 from partner (first lab run), so the mapping names the partner version itself, as cross-domain-lab's do.

    python3 core/Workflows/instance-call-credential-xd/build-instance-call-credential-xd.py
"""

import base64
import json
from pathlib import Path

CORE = Path(__file__).resolve().parent
REPO = CORE.parent.parent.parent
PARTNER = REPO / "partner" / "Workflows" / "instance-call-credential-xd"
VERSION = "1.0.2"
TAG = "instance-call-credential-xd"


def ref(key, domain, flow):
    return {"key": key, "domain": domain, "flow": flow, "version": VERSION}


def code(root, name):
    raw = (root / "src" / name).read_bytes()
    return {"location": f"./src/{name}", "code": base64.b64encode(raw).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def auto(key, target, text, rule=None):
    t = {"key": key, "target": target, "triggerType": 1, "versionStrategy": "Minor", "labels": label(text)}
    if rule:
        t["rule"] = rule
    else:
        t["triggerKind"] = 10
    return t


def manual(key, target, text, tasks=None):
    t = {"key": key, "target": target, "triggerType": 0, "versionStrategy": "Minor", "labels": label(text)}
    if tasks:
        t["onExecutionTasks"] = tasks
    return t


def task(root, domain, task_key, mapping_file, order=1):
    return {"order": order, "task": ref(task_key, domain, "sys-tasks"), "mapping": code(root, mapping_file)}


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


def envelope(key, domain, flow_type, tags, text, target, states, schema=None):
    attributes = {"type": flow_type, "labels": label(text)}
    if schema:
        attributes["schema"] = ref(schema, domain, "sys-schemas")
    attributes.update({
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
        "domain": domain,
        "version": VERSION,
        "tags": ["integration-test", "cross-domain-lab", TAG, *tags],
        "attributes": attributes,
    }


def recording_target(root, domain, key, text, schema, script_task):
    record = "IccXdRecordMapping.csx"
    return envelope(key, domain, "F", ["target"], text, "icc-t-entry", [
        state("icc-t-entry", 1, "Record the start hop", [
            auto("to-wait", "icc-t-wait", "mode = wait", rule=code(root, "IccXdModeWaitRule.csx")),
            auto("to-done", "icc-t-done", "Otherwise finish"),
        ], on_entries=[task(root, domain, script_task, record)]),
        state("icc-t-wait", 2, "Wait for touch", [
            manual("touch", "icc-t-touched", "Touch (records the trigger hop)",
                   tasks=[task(root, domain, script_task, record)]),
        ]),
        state("icc-t-touched", 3, "Touched"),
        state("icc-t-done", 3, "Done"),
    ], schema=schema)


# ── partner ─────────────────────────────────────────────────────────────────

P_SCRIPT = "icc-xd-script"
partner_target = recording_target(PARTNER, "partner", "icc-xd-target", "Instance Call Credential XD partner target",
                                  "icc-xd-target-master", P_SCRIPT)
partner_sub = envelope("icc-xd-sub", "partner", "S", ["child"], "Instance Call Credential XD SubFlow child",
                       "icc-xd-s-entry", [
    state("icc-xd-s-entry", 1, "Record the start hop", [auto("to-done", "icc-xd-s-done", "Finish")],
          on_entries=[task(PARTNER, "partner", P_SCRIPT, "IccXdRecordMapping.csx")]),
    state("icc-xd-s-done", 3, "Done"),
])
partner_nest = envelope("icc-xd-sub-nest", "partner", "S", ["child", "nested"],
                        "Instance Call Credential XD nested child (calls back into core)", "icc-xd-n-entry", [
    state("icc-xd-n-entry", 1, "Record, then DirectTrigger + GetInstance into core", [auto("to-done", "icc-xd-n-done", "Finish")],
          on_entries=[
              task(PARTNER, "partner", P_SCRIPT, "IccXdRecordMapping.csx", 1),
              task(PARTNER, "partner", "icc-xd-back-trigger", "IccXdBackTriggerMapping.csx", 2),
              task(PARTNER, "partner", "icc-xd-back-get", "IccXdBackGetMapping.csx", 3),
          ]),
    state("icc-xd-n-done", 3, "Done"),
])

# ── core ────────────────────────────────────────────────────────────────────

C_SCRIPT = "icc-xd-core-script"
core_target = recording_target(CORE, "core", "icc-xd-core-target", "Instance Call Credential XD core target (XC-06)",
                               "icc-xd-core-target-master", C_SCRIPT)


def call_state(key, task_key, text):
    return state(key, 2, text, [auto("to-done", "icc-xd-p-done", "Done")],
                 on_entries=[task(CORE, "core", task_key, "IccXdCallMapping.csx")])


def partner_sub_flow(process_key):
    return {"type": "S", "process": ref(process_key, "partner", "sys-flows"),
            "mapping": code(CORE, "IccXdSubFlowMapping.csx")}


core_parent = envelope("icc-xd-parent", "core", "F", ["parent"], "Instance Call Credential XD parent", "icc-xd-p-ready", [
    state("icc-xd-p-ready", 1, "Ready — the test picks the call", [
        manual("do-subflow", "icc-xd-p-subflow", "SubFlow start (partner)"),
        manual("do-nest", "icc-xd-p-nest", "Nested SubFlow start (partner, calls back into core)"),
        manual("do-subprocess", "icc-xd-p-subprocess", "SubProcess task (partner)"),
        manual("do-start", "icc-xd-p-start", "Start task (partner)"),
        manual("do-trigger", "icc-xd-p-trigger", "DirectTrigger task (partner)"),
        manual("do-get", "icc-xd-p-get", "GetInstance task (partner)"),
        manual("do-list", "icc-xd-p-list", "GetInstances task (partner)"),
        manual("do-data", "icc-xd-p-data", "GetInstanceData task (partner)"),
    ]),
    state("icc-xd-p-subflow", 4, "SubFlow partner icc-xd-sub", [auto("to-done", "icc-xd-p-done", "Done")],
          sub_flow=partner_sub_flow("icc-xd-sub")),
    state("icc-xd-p-nest", 4, "SubFlow partner icc-xd-sub-nest", [auto("to-done", "icc-xd-p-done", "Done")],
          sub_flow=partner_sub_flow("icc-xd-sub-nest")),
    call_state("icc-xd-p-subprocess", "icc-xd-subprocess", "SubProcess call"),
    call_state("icc-xd-p-start", "icc-xd-start", "Start call"),
    call_state("icc-xd-p-trigger", "icc-xd-trigger", "DirectTrigger call"),
    call_state("icc-xd-p-get", "icc-xd-get", "GetInstance call"),
    call_state("icc-xd-p-list", "icc-xd-list", "GetInstances call"),
    call_state("icc-xd-p-data", "icc-xd-data", "GetInstanceData call"),
    state("icc-xd-p-done", 3, "Done"),
])

# ── tasks ───────────────────────────────────────────────────────────────────

TO_PARTNER = {"domain": "partner", "flow": "icc-xd-target", "useDapr": True}
TO_CORE = {"domain": "core", "flow": "icc-xd-core-target", "useDapr": True}
core_tasks = {
    C_SCRIPT: ("7", {}, "script-task"),
    "icc-xd-subprocess": ("14", {**TO_PARTNER, "version": VERSION, "timeoutSeconds": 30}, "subprocess"),
    "icc-xd-start": ("11", {**TO_PARTNER, "version": VERSION, "sync": True, "timeoutSeconds": 30}, "start"),
    "icc-xd-trigger": ("12", {**TO_PARTNER, "transitionName": "touch", "sync": True, "timeoutSeconds": 30}, "direct-trigger"),
    "icc-xd-get": ("19", {**TO_PARTNER, "timeoutSeconds": 30}, "get-instance"),
    "icc-xd-list": ("15", {**TO_PARTNER, "page": 1, "pageSize": 10, "timeoutSeconds": 30}, "get-instances"),
    "icc-xd-data": ("13", {**TO_PARTNER, "timeoutSeconds": 30}, "get-instance-data"),
}
partner_tasks = {
    P_SCRIPT: ("7", {}, "script-task"),
    "icc-xd-back-trigger": ("12", {**TO_CORE, "transitionName": "touch", "sync": True, "timeoutSeconds": 30}, "direct-trigger"),
    "icc-xd-back-get": ("19", {**TO_CORE, "timeoutSeconds": 30}, "get-instance"),
}


def write(path, doc):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {path.relative_to(REPO)}")


def task_doc(key, domain, task_type, config, tag):
    return {
        "key": key,
        "version": VERSION,
        "domain": domain,
        "flow": "sys-tasks",
        "flowVersion": "1.0.0",
        "tags": ["integration-test", "cross-domain-lab", TAG, tag],
        "attributes": {"type": task_type, "config": config},
    }


for doc in (core_parent, core_target):
    write(CORE / f"{doc['key']}.json", doc)
for doc in (partner_target, partner_sub, partner_nest):
    write(PARTNER / f"{doc['key']}.json", doc)
for key, (task_type, config, tag) in core_tasks.items():
    write(REPO / "core" / "Tasks" / TAG / f"{key}.json", task_doc(key, "core", task_type, config, tag))
for key, (task_type, config, tag) in partner_tasks.items():
    write(REPO / "partner" / "Tasks" / TAG / f"{key}.json", task_doc(key, "partner", task_type, config, tag))
