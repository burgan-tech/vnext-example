#!/usr/bin/env python3
"""Regenerates the file-offload-lab workflow JSON files (vnext-client-sdk-core#101, x-storage).

* fo-flow — master fo-master (passport / files[] / secret x-storage on vnext-blob-local).
  waiting --add-document--> waiting (plain merge; uploads are swapped to handles at admission)
  waiting --to-review (transition mapping: upload -> passport)--> review
  review onEntry: FoChecksumMapping reads the passport through GetFileAsync and writes checksum
  waiting --lock--> locked (state queryRoles: fo.reader only — functions/file 403 for others)
  review / locked --finish--> done
* fo-parent — master fo-parent-master; p-sub starts fo-child as an S SubFlow.
* fo-child — master fo-child-master; c-waiting --child-upload--> c-waiting, --child-finish--> c-done.
* fo-missing-binding — master fo-missing-master (binding vnext-blob-missing does not exist).

Edit ./src/*.csx and re-run — never hand-edit the base64 blob. Publish is version-immutable: bump
VERSION (and the component references) on every change.

    python3 core/Workflows/file-offload-lab/build-file-offload-lab.py
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
VERSION = "1.0.1"
SCHEMA_VERSION = "1.0.0"
TASK = {"key": "fo-script", "domain": "core", "flow": "sys-tasks", "version": "1.0.0"}


def code(name):
    raw = (ROOT / "src" / name).read_bytes()
    return {"location": f"./src/{name}", "code": base64.b64encode(raw).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def manual(key, target, text, mapping=None):
    t = {"key": key, "target": target, "triggerType": 0, "versionStrategy": "Minor", "labels": label(text)}
    if mapping:
        t["mapping"] = code(mapping)
    return t


def auto(key, target, text):
    return {"key": key, "target": target, "triggerType": 1, "triggerKind": 10,
            "versionStrategy": "Minor", "labels": label(text)}


def state(key, state_type, text, transitions=None, on_entries=None, sub_flow=None, query_roles=None):
    s = {
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
    if query_roles:
        s["queryRoles"] = query_roles
    return s


def entry(mapping_file):
    return {"order": 1, "task": TASK, "mapping": code(mapping_file)}


def envelope(key, flow_type, tags, text, schema, target, states):
    return {
        "key": key,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": VERSION,
        "tags": ["integration-test", "file-offload-lab", *tags],
        "attributes": {
            "type": flow_type,
            "labels": label(text),
            "functions": [],
            "features": [],
            "extensions": [],
            "schema": {"key": schema, "domain": "core", "version": SCHEMA_VERSION, "flow": "sys-schemas"},
            "startTransition": {
                "key": "start", "target": target, "triggerType": 0, "versionStrategy": "Minor",
                "labels": label("Start"),
            },
            "states": states,
        },
    }


docs = [
    envelope("fo-flow", "F", ["files"], "File Offload Lab", "fo-master", "waiting", [
        state("waiting", 1, "Waiting for documents", [
            manual("add-document", "waiting", "Add document"),
            manual("to-review", "review", "Send upload to review", mapping="FoRelocateUploadMapping.csx"),
            manual("lock", "locked", "Lock"),
        ]),
        state("review", 2, "Review (checksum via GetFileAsync)", [manual("finish", "done", "Finish")],
              on_entries=[entry("FoChecksumMapping.csx")]),
        state("locked", 2, "Locked (queryRoles fo.reader)", [manual("finish", "done", "Finish")],
              query_roles=[{"role": "fo.reader", "grant": "allow"}]),
        state("done", 3, "Done"),
    ]),
    envelope("fo-parent", "F", ["parent"], "File Offload Lab parent", "fo-parent-master", "p-sub", [
        state("p-sub", 4, "SubFlow fo-child", [auto("auto-done", "p-done", "Auto to done")],
              sub_flow={
                  "type": "S",
                  "process": {"key": "fo-child", "domain": "core", "flow": "sys-flows", "version": VERSION},
                  "mapping": code("FoParentToChildMapping.csx"),
              }),
        state("p-done", 3, "Done"),
    ]),
    envelope("fo-child", "S", ["child"], "File Offload Lab child", "fo-child-master", "c-waiting", [
        state("c-waiting", 2, "Child waiting for upload", [
            manual("child-upload", "c-waiting", "Child upload"),
            manual("child-finish", "c-done", "Child finish"),
        ]),
        state("c-done", 3, "Child done"),
    ]),
    envelope("fo-missing-binding", "F", ["missing-binding"], "File Offload Lab missing binding",
             "fo-missing-master", "m-waiting", [
        state("m-waiting", 1, "Waiting", [manual("m-finish", "m-done", "Finish")]),
        state("m-done", 3, "Done"),
    ]),
]

for doc in docs:
    path = ROOT / f"{doc['key']}.json"
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {path.name}")
