#!/usr/bin/env python3
"""Regenerates the cross-domain x-storage scenario (vnext-client-sdk-core#101) — core AND partner components.

* core/fo-xd-parent — master fo-xd-parent-master (the same passport x-storage path as the child, to catch a
  parent-side swap). p-hub --enter-child--> p-child [S SubFlow partner/fo-xd-child] --auto--> p-done
* partner/fo-xd-child — master fo-xd-child-master (passport x-storage on vnext-blob-local = the PARTNER
  sidecar's binding). child-waiting --child-upload--> child-waiting --child-finish--> child-done
  (child-upload loops so the child stays the active leaf: the parent's functions/file is then a real
  "no descent" check, and GetFileAsync reads an active instance).
* core/fo-xd-reader — no master schema. r-waiting --read--> r-read (onEntry FoXdReadMapping:
  GetFileAsync(sourceDomain, sourceFlow, sourceInstance, sourceFileId) -> checksum) --finish--> r-done

Edit ./src/*.csx and re-run — never hand-edit the base64 blob. Publish is version-immutable: bump VERSION
(and the component references) on every change.

    python3 core/Workflows/file-offload-xd/build-file-offload-xd.py
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent.parent.parent
VERSION = "1.0.0"
SCHEMA_VERSION = "1.0.0"
TASK = {"key": "fo-script", "domain": "core", "flow": "sys-tasks", "version": "1.0.0"}
BINDING = "vnext-blob-local"


def code(name):
    raw = (ROOT / "src" / name).read_bytes()
    return {"location": f"./src/{name}", "code": base64.b64encode(raw).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def manual(key, target, text):
    return {"key": key, "target": target, "triggerType": 0, "versionStrategy": "Minor", "labels": label(text)}


def auto(key, target, text):
    return {"key": key, "target": target, "triggerType": 1, "triggerKind": 10,
            "versionStrategy": "Minor", "labels": label(text)}


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


def envelope(domain, key, flow_type, tags, text, schema, target, states):
    attributes = {
        "type": flow_type,
        "labels": label(text),
        "functions": [],
        "features": [],
        "extensions": [],
    }
    if schema:
        attributes["schema"] = {"key": schema, "domain": domain, "version": SCHEMA_VERSION, "flow": "sys-schemas"}
    attributes["startTransition"] = {
        "key": "start", "target": target, "triggerType": 0, "versionStrategy": "Minor", "labels": label("Start"),
    }
    attributes["states"] = states
    return {
        "key": key,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": domain,
        "version": VERSION,
        "tags": ["integration-test", "file-offload-xd", "cross-domain-lab", *tags],
        "attributes": attributes,
    }


def handle_schema():
    nullable = {"type": ["string", "null"]}
    return {
        "type": "object",
        "x-storage": {"binding": BINDING},
        "properties": {
            "component": {"type": "string"},
            "file": {"type": "string", "format": "uuid"},
            "name": nullable,
            "mimeType": nullable,
            "size": {"type": "integer"},
            "eTag": {"type": "string"},
            "owner": {"type": "object", "properties": {
                "domain": {"type": "string"}, "flow": {"type": "string"}, "instance": {"type": "string"}}},
        },
    }


def master(domain, key, title, description, passport_description):
    passport = handle_schema()
    passport["description"] = passport_description
    return {
        "key": key,
        "version": SCHEMA_VERSION,
        "domain": domain,
        "flow": "sys-schemas",
        "flowVersion": "1.0.0",
        "tags": ["integration-test", "file-offload-xd", "master", "schema"],
        "attributes": {
            "type": "schema",
            "schema": {
                "$schema": "https://json-schema.org/draft/2020-12/schema",
                "$id": f"urn:vnext:res:schema:{domain}:{key}",
                "title": title,
                "description": description,
                "type": "object",
                "properties": {
                    "passport": passport,
                    "note": {"type": "string"},
                    "parentInstanceId": {"type": ["string", "null"]},
                },
            },
        },
    }


outputs = {
    REPO / "core/Workflows/file-offload-xd/fo-xd-parent.json":
        envelope("core", "fo-xd-parent", "F", ["parent"], "Cross-domain x-storage parent", "fo-xd-parent-master",
                 "p-hub", [
                     state("p-hub", 1, "Hub", [manual("enter-child", "p-child", "Enter partner child")]),
                     state("p-child", 4, "SubFlow partner/fo-xd-child", [auto("auto-done", "p-done", "Auto to done")],
                           sub_flow={
                               "type": "S",
                               "process": {"key": "fo-xd-child", "domain": "partner", "flow": "sys-flows",
                                           "version": VERSION},
                               "mapping": code("FoXdParentToChildMapping.csx"),
                           }),
                     state("p-done", 3, "Done"),
                 ]),
    REPO / "core/Workflows/file-offload-xd/fo-xd-reader.json":
        envelope("core", "fo-xd-reader", "F", ["reader"], "Cross-domain x-storage reader (GetFileAsync)", None,
                 "r-waiting", [
                     state("r-waiting", 1, "Waiting for a source", [manual("read", "r-read", "Read a remote file")]),
                     state("r-read", 2, "Read (checksum via GetFileAsync)", [manual("finish", "r-done", "Finish")],
                           on_entries=[{"order": 1, "task": TASK, "mapping": code("FoXdReadMapping.csx")}]),
                     state("r-done", 3, "Done"),
                 ]),
    REPO / "partner/Workflows/file-offload-xd/fo-xd-child.json":
        envelope("partner", "fo-xd-child", "S", ["child", "partner"], "Cross-domain x-storage child (partner)",
                 "fo-xd-child-master", "child-waiting", [
                     state("child-waiting", 2, "Child waiting for upload", [
                         manual("child-upload", "child-waiting", "Child upload"),
                         manual("child-finish", "child-done", "Child finish"),
                     ]),
                     state("child-done", 3, "Child done"),
                 ]),
    REPO / "core/Schemas/file-offload-xd/fo-xd-parent-master.json":
        master("core", "fo-xd-parent-master", "Cross-domain x-storage parent master",
               "fo-xd-parent declares the child's passport path too: a request the parent FORWARDS to its partner "
               "child must be swapped by the leaf (owner = partner child), never by the core parent.",
               "Same path as the partner child's, to catch a parent-side swap."),
    REPO / "partner/Schemas/file-offload-xd/fo-xd-child-master.json":
        master("partner", "fo-xd-child-master", "Cross-domain x-storage child master",
               "fo-xd-child (partner S SubFlow of core fo-xd-parent) owns the passport file in the partner "
               "sidecar's vnext-blob-local binding.",
               "The child's file."),
}

for path, doc in outputs.items():
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {path.relative_to(REPO)}")
