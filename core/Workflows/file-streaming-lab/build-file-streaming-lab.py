#!/usr/bin/env python3
"""Regenerates the file-streaming-lab components (vnext-client-sdk-core#137 / #142 / #145).

* fsl-master (schema) — x-storage fields, every store a Dapr binding on the orchestration sidecar:
    inlineDoc   fsl-minio      transport inline (default)   base64 content in the body
    files[]     fsl-minio      transport any                inline content OR a streamed upload; rows carry documentType
    streamDoc   fsl-minio      transport stream             streamed uploads only
    bankDoc     fsl-stub       transport any                runtime sidecar bindings.aws.s3 -> the DUMB store behind TLS
    bankDapr    fsl-stub-dapr  transport any                Dapr bindings.aws.s3 -> the DUMB store behind TLS (#145 repro)
    converted   fsl-minio                                   written by a script (PutFileAsync)
* fsl-flow — waiting --add-document--> waiting; --convert--> converted (script: read, stream-process, write back);
  --inspect--> inspected (DaprBinding list + get on fsl-minio); --delete-file--> deleted (DaprBinding delete);
  every side state --back--> waiting.
* fsl-download (function, rawResponse) — serves inlineDoc as its own bytes (#99 / #142-E).
* Tasks: fsl-script (type 7), fsl-binding-list / fsl-binding-get / fsl-binding-delete (type 2, fsl-minio).

Edit ./src/*.csx and re-run; never hand-edit a base64 blob. Publish is version-immutable: bump VERSION on change.

    python3 core/Workflows/file-streaming-lab/build-file-streaming-lab.py
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
CORE = ROOT.parent.parent
VERSION = "1.0.0"


def code(path):
    raw = path.read_bytes()
    return {"location": f"./src/{path.name}", "code": base64.b64encode(raw).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def ref(key, flow):
    return {"key": key, "domain": "core", "flow": flow, "version": VERSION}


def write(path, doc):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {path.relative_to(CORE)}")


HANDLE = {
    "component": {"type": "string"},
    "file": {"type": "string", "format": "uuid"},
    "name": {"type": ["string", "null"]},
    "mimeType": {"type": ["string", "null"]},
    "size": {"type": "integer"},
    "eTag": {"type": "string"},
    "owner": {"type": "object", "properties": {
        "domain": {"type": "string"}, "flow": {"type": "string"}, "instance": {"type": "string"}}},
}


def file_node(binding, transport=None, extra=None):
    storage = {"binding": binding}
    if transport:
        storage["transport"] = transport
    return {"type": "object", "x-storage": storage, "properties": {**HANDLE, **(extra or {})}}


schema = {
    "key": "fsl-master", "version": VERSION, "domain": "core", "flow": "sys-schemas", "flowVersion": "1.0.0",
    "tags": ["integration-test", "file-streaming-lab", "master", "schema"],
    "attributes": {
        "type": "master",
        "schema": {
            "$schema": "https://json-schema.org/draft/2020-12/schema",
            "$id": "urn:vnext:res:schema:core:fsl-master",
            "title": "File Streaming Lab Master",
            "description": "Persisted shape of fsl-flow: every x-storage node describes the stored HANDLE.",
            "type": "object",
            "properties": {
                "inlineDoc": file_node("fsl-minio"),
                "files": {"type": "array", "items": file_node("fsl-minio", "any", {"documentType": {"type": "string"}})},
                "streamDoc": file_node("fsl-minio", "stream"),
                "bankDoc": file_node("fsl-stub", "any"),
                "bankDapr": file_node("fsl-stub-dapr", "any"),
                "converted": file_node("fsl-minio"),
            },
        },
    },
}
write(CORE / "Schemas/file-streaming-lab/fsl-master.json", schema)


def task(key, task_type, config, tags):
    return {"key": key, "version": VERSION, "domain": "core", "flow": "sys-tasks", "flowVersion": "1.0.0",
            "tags": ["integration-test", "file-streaming-lab", *tags], "attributes": {"type": task_type, "config": config}}


write(CORE / "Tasks/file-streaming-lab/fsl-script.json", task("fsl-script", "7", {}, ["script-task"]))
for op in ("list", "get", "delete"):
    write(CORE / f"Tasks/file-streaming-lab/fsl-binding-{op}.json",
          task(f"fsl-binding-{op}", "2", {"bindingName": "fsl-minio", "operation": op, "metadata": {}}, ["dapr-binding"]))

src = ROOT / "src"


def entry(order, task_key, mapping):
    return {"order": order, "task": ref(task_key, "sys-tasks"), "mapping": code(src / mapping)}


def manual(key, target, text):
    return {"key": key, "target": target, "triggerType": 0, "versionStrategy": "Minor", "labels": label(text)}


def state(key, state_type, text, transitions=None, on_entries=None):
    return {"key": key, "stateType": state_type, "subType": 1 if state_type == 3 else 0, "versionStrategy": "Minor",
            "labels": label(text), "view": None, "subFlow": None, "onEntries": on_entries or [], "onExits": [],
            "transitions": transitions or []}


back = [manual("back", "waiting", "Back"), manual("finish", "done", "Finish")]
flow = {
    "key": "fsl-flow", "flow": "sys-flows", "flowVersion": "1.0.0", "domain": "core", "version": VERSION,
    "tags": ["integration-test", "file-streaming-lab", "files"],
    "attributes": {
        "type": "F", "labels": label("File Streaming Lab"), "functions": [ref("fsl-download", "sys-functions")],
        "features": [], "extensions": [],
        "schema": {"key": "fsl-master", "domain": "core", "version": VERSION, "flow": "sys-schemas"},
        "startTransition": {"key": "start", "target": "waiting", "triggerType": 0, "versionStrategy": "Minor",
                            "labels": label("Start")},
        "states": [
            state("waiting", 1, "Waiting", [
                manual("add-document", "waiting", "Add document"),
                manual("convert", "converted", "Convert files[0]"),
                manual("inspect", "inspected", "List and get through the binding"),
                manual("delete-file", "deleted", "Delete files[0] through the binding"),
                manual("finish", "done", "Finish"),
            ]),
            state("converted", 2, "Converted", back, [entry(1, "fsl-script", "FslConvertMapping.csx")]),
            state("inspected", 2, "Inspected", back, [
                entry(1, "fsl-binding-list", "FslBindingListMapping.csx"),
                entry(2, "fsl-binding-get", "FslBindingGetMapping.csx"),
            ]),
            state("deleted", 2, "Deleted", back, [entry(1, "fsl-binding-delete", "FslBindingDeleteMapping.csx")]),
            state("done", 3, "Done"),
        ],
    },
}
write(ROOT / "fsl-flow.json", flow)

fsrc = CORE / "Functions/file-streaming-lab/src"
function = {
    "key": "fsl-download", "version": VERSION, "domain": "core", "flow": "sys-functions", "flowVersion": "1.0.0",
    "tags": ["integration-test", "file-streaming-lab", "raw-binary"],
    "attributes": {
        "scope": "I",
        "rawResponse": True,
        "task": {"order": 1, "task": ref("fsl-script", "sys-tasks"), "mapping": code(fsrc / "FslNoopMapping.csx")},
        "output": code(fsrc / "FslDownloadOutput.csx"),
    },
}
write(CORE / "Functions/file-streaming-lab/fsl-download.json", function)
