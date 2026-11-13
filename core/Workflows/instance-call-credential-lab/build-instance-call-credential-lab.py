#!/usr/bin/env python3
"""Regenerates the instance-call-credential-lab workflow and task JSON files.

Pins the runtime's caller-credential rule on instance calls (vnext docs/domain/role-grant-authorization.md
§ "Caller credential on instance calls"): for `sub`, `act_sub`, `position`, `client_id` and `role`, a mapping
value that is non-blank wins, a BLANK one withholds the credential (the caller's value is not added), an
absent key is filled from the caller (the request that drove the parent's hop). Same domain only (`core`).

* icc-parent (F) — start → icc-p-ready (rests Active). The test drives ONE manual transition with its own
  `sub` / `act_sub` (and `role`) headers; the start data's `variant` picks the mapping headers:
    do-subflow    → icc-p-subflow   (SubFlow S → icc-sub,      IccSubFlowMapping: none|dict-blank|other|string)
    do-subflow-anon → icc-p-subflow-anon (SubFlow S → icc-sub, IccSubFlowAnonMapping: Headers = new { sub = "" })
    do-nest       → icc-p-nest      (SubFlow S → icc-sub-nest, sub = "")
    do-subprocess → icc-p-subprocess (onEntry SubProcessTask → icc-target, key {parentId}-proc)
    do-start      → icc-p-start      (onEntry StartTask      → icc-target, key {parentId}-start)
    do-trigger    → icc-p-trigger    (onEntry DirectTrigger  → targetId, transition `touch`)
    do-get / do-list / do-data → onEntry GetInstance / GetInstances / GetInstanceData on targetId / testId
  every call state then goes on (default auto) to icc-p-done.
* icc-sub (S) — start → icc-s-entry (onEntry recorder) → icc-s-done.
* icc-sub-nest (S) — start → icc-n-entry (onEntry recorder, then StartTask → icc-target with no mapping
  headers, key {childId}-start) → icc-n-done.
* icc-target (F, master schema icc-target-master: `secretNote` x-roles allow `icc.reader`) — start →
  icc-t-entry (onEntry recorder) → icc-t-wait when mode = "wait" (manual `touch` → icc-t-touched, whose
  onExecutionTasks recorder writes `touchSeen`), else icc-t-done.

Edit ./src/*.csx and re-run — never hand-edit the base64 blobs. Publish is version-immutable: bump VERSION
(patch) for any change to an already-published component.

    python3 core/Workflows/instance-call-credential-lab/build-instance-call-credential-lab.py
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
TASKS = ROOT.parent.parent / "Tasks" / "instance-call-credential-lab"
VERSION = "1.0.1"
TAG = "instance-call-credential-lab"


def ref(key, flow):
    return {"key": key, "domain": "core", "flow": flow, "version": VERSION}


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


def task(task_key, mapping_file, order=1):
    return {"order": order, "task": ref(task_key, "sys-tasks"), "mapping": code(mapping_file)}


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


def sub_flow(process_key, mapping_file):
    return {"type": "S", "process": ref(process_key, "sys-flows"), "mapping": code(mapping_file)}


def envelope(key, flow_type, tags, text, target, states, schema=None):
    attributes = {"type": flow_type, "labels": label(text)}
    if schema:
        attributes["schema"] = ref(schema, "sys-schemas")
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
        "domain": "core",
        "version": VERSION,
        "tags": ["integration-test", TAG, *tags],
        "attributes": attributes,
    }


RECORD = "IccRecordMapping.csx"
CALL = "IccCallMapping.csx"

target = envelope("icc-target", "F", ["target"], "Instance Call Credential Lab target", "icc-t-entry", [
    state("icc-t-entry", 1, "Record the start hop", [
        auto("to-wait", "icc-t-wait", "mode = wait", rule="IccModeWaitRule.csx"),
        auto("to-done", "icc-t-done", "Otherwise finish"),
    ], on_entries=[task("icc-script", RECORD)]),
    state("icc-t-wait", 2, "Wait for touch", [
        manual("touch", "icc-t-touched", "Touch (records the trigger hop)", tasks=[task("icc-script", RECORD)]),
    ]),
    state("icc-t-touched", 3, "Touched"),
    state("icc-t-done", 3, "Done"),
], schema="icc-target-master")

sub = envelope("icc-sub", "S", ["child"], "Instance Call Credential Lab SubFlow child", "icc-s-entry", [
    state("icc-s-entry", 1, "Record the start hop", [auto("to-done", "icc-s-done", "Finish")],
          on_entries=[task("icc-script", RECORD)]),
    state("icc-s-done", 3, "Done"),
])

nest = envelope("icc-sub-nest", "S", ["child", "nested"], "Instance Call Credential Lab nested child", "icc-n-entry", [
    state("icc-n-entry", 1, "Record, then start a target", [auto("to-done", "icc-n-done", "Finish")],
          on_entries=[task("icc-script", RECORD, 1), task("icc-start", CALL, 2)]),
    state("icc-n-done", 3, "Done"),
])


def call_state(key, task_key, text):
    return state(key, 2, text, [auto("to-done", "icc-p-done", "Done")], on_entries=[task(task_key, CALL)])


parent = envelope("icc-parent", "F", ["parent"], "Instance Call Credential Lab parent", "icc-p-ready", [
    state("icc-p-ready", 1, "Ready — the test picks the call", [
        manual("do-subflow", "icc-p-subflow", "SubFlow start"),
        manual("do-subflow-anon", "icc-p-subflow-anon", "SubFlow start (anonymous-object headers)"),
        manual("do-nest", "icc-p-nest", "Nested SubFlow start (sub blank)"),
        manual("do-subprocess", "icc-p-subprocess", "SubProcess task"),
        manual("do-start", "icc-p-start", "Start task"),
        manual("do-trigger", "icc-p-trigger", "DirectTrigger task"),
        manual("do-get", "icc-p-get", "GetInstance task"),
        manual("do-list", "icc-p-list", "GetInstances task"),
        manual("do-data", "icc-p-data", "GetInstanceData task"),
    ]),
    state("icc-p-subflow", 4, "SubFlow icc-sub", [auto("to-done", "icc-p-done", "Done")],
          sub_flow=sub_flow("icc-sub", "IccSubFlowMapping.csx")),
    state("icc-p-subflow-anon", 4, "SubFlow icc-sub (anonymous headers)", [auto("to-done", "icc-p-done", "Done")],
          sub_flow=sub_flow("icc-sub", "IccSubFlowAnonMapping.csx")),
    state("icc-p-nest", 4, "SubFlow icc-sub-nest", [auto("to-done", "icc-p-done", "Done")],
          sub_flow=sub_flow("icc-sub-nest", "IccNestSubFlowMapping.csx")),
    call_state("icc-p-subprocess", "icc-subprocess", "SubProcess call"),
    call_state("icc-p-start", "icc-start", "Start call"),
    call_state("icc-p-trigger", "icc-trigger", "DirectTrigger call"),
    call_state("icc-p-get", "icc-get", "GetInstance call"),
    call_state("icc-p-list", "icc-list", "GetInstances call"),
    call_state("icc-p-data", "icc-data", "GetInstanceData call"),
    state("icc-p-done", 3, "Done"),
])

TARGET = {"domain": "core", "flow": "icc-target"}
tasks = {
    "icc-script": ("7", {}, "script-task"),
    "icc-subprocess": ("14", {**TARGET, "version": VERSION}, "subprocess"),
    "icc-start": ("11", {**TARGET, "sync": True, "timeoutSeconds": 30}, "start"),
    "icc-trigger": ("12", {**TARGET, "transitionName": "touch", "sync": True, "timeoutSeconds": 30}, "direct-trigger"),
    "icc-get": ("19", {**TARGET, "timeoutSeconds": 30}, "get-instance"),
    "icc-list": ("15", {**TARGET, "page": 1, "pageSize": 10, "timeoutSeconds": 30}, "get-instances"),
    "icc-data": ("13", {**TARGET, "timeoutSeconds": 30}, "get-instance-data"),
}

for doc in (target, sub, nest, parent):
    path = ROOT / f"{doc['key']}.json"
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {path.name}")

TASKS.mkdir(parents=True, exist_ok=True)
for key, (task_type, config, tag) in tasks.items():
    doc = {
        "key": key,
        "version": VERSION,
        "domain": "core",
        "flow": "sys-tasks",
        "flowVersion": "1.0.0",
        "tags": ["integration-test", TAG, tag],
        "attributes": {"type": task_type, "config": config},
    }
    path = TASKS / f"{key}.json"
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote Tasks/{TAG}/{path.name}")
