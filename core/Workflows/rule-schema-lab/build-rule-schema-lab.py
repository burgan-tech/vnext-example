#!/usr/bin/env python3
"""Regenerates rule-schema-lab.json and the rule-schema-lab-echo function from the .csx sources.

The components embed every script as base64 in `code` next to its `location`; edit the .csx files
and re-run this script — never hand-edit the base64 blobs.

    python3 core/Workflows/rule-schema-lab/build-rule-schema-lab.py
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parents[2]
FUNCTION_DIR = REPO / "core" / "Functions" / "rule-schema-lab"


def code(base, name):
    raw = (base / "src" / name).read_bytes()
    return {"location": f"./src/{name}", "code": base64.b64encode(raw).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def schema_ref(key):
    return {"key": key, "domain": "core", "version": "1.0.0", "flow": "sys-schemas"}


CORPORATE = schema_ref("rule-schema-lab-corporate")
RETAIL = schema_ref("rule-schema-lab-retail")


def view_ref(key):
    return {"key": key, "domain": "core", "version": "1.0.0", "flow": "sys-views"}


def manual(key, target, text, **extra):
    return {
        "key": key,
        "target": target,
        "triggerType": 0,
        "versionStrategy": "Major",
        "labels": label(text),
        **extra,
    }


workflow = {
    "key": "rule-schema-lab",
    "flow": "sys-flows",
    "flowVersion": "1.0.0",
    "domain": "core",
    "version": "1.0.1",
    "tags": ["integration-test", "rule-schema-lab", "rule-based-schema", "caller-roles"],
    "attributes": {
        "type": "F",
        "timeout": None,
        "labels": label("Rule Schema Lab (rule-based transition schemas)"),
        "functions": [],
        "features": [],
        "extensions": [],
        # No schema on start: an undeclared schema accepts any payload.
        "startTransition": manual("start-rule-schema-lab", "review", "Start Rule Schema Lab"),
        "states": [
            {
                "key": "review",
                "stateType": 1,
                "subType": 0,
                "versionStrategy": "Major",
                "labels": label("Review"),
                # View selected by the caller's resolved roles (context.Caller), with a rule-less fallback.
                "view": [
                    {"rule": code(ROOT, "IsApproverRole.csx"), "view": view_ref("rule-schema-lab-approver-view")},
                    {"view": view_ref("rule-schema-lab-default-view")},
                ],
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [
                    # Every entry carries a rule: a caller whose x-segment matches neither is rejected
                    # with 400 Transition:100025 and the instance stays where it is.
                    manual("submit", "done", "Submit (rule-based schemas, no fallback)", schemas=[
                        {"rule": code(ROOT, "IsCorporateSegment.csx"), "schema": CORPORATE},
                        {"rule": code(ROOT, "IsRetailSegment.csx"), "schema": RETAIL},
                    ]),
                    # Rule-less last entry: an unmatched caller falls back to the retail schema.
                    manual("submit-fallback", "done", "Submit (rule-based schemas with fallback)", schemas=[
                        {"rule": code(ROOT, "IsCorporateSegment.csx"), "schema": CORPORATE},
                        {"schema": RETAIL},
                    ]),
                    # Selected by the caller's resolved roles: approvers get the corporate schema, everyone
                    # else the retail fallback. A role-provider outage fails the selection (503), never the fallback.
                    manual("submit-by-role", "done", "Submit (schema selected by caller role)", schemas=[
                        {"rule": code(ROOT, "IsApproverRole.csx"), "schema": CORPORATE},
                        {"schema": RETAIL},
                    ]),
                    # No schema at all: accepts any payload.
                    manual("note", "$self", "Note (no schema)"),
                ],
            },
            {
                "key": "done",
                "stateType": 3,
                "subType": 1,
                "versionStrategy": "Major",
                "labels": label("Done"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [],
            },
        ],
    },
}

function = {
    "key": "rule-schema-lab-echo",
    "version": "1.0.0",
    "domain": "core",
    "flow": "sys-functions",
    "flowVersion": "1.0.0",
    "tags": ["integration-test", "rule-schema-lab", "rule-based-schema", "function"],
    "attributes": {
        "scope": "D",
        "rawResponse": False,
        "labels": label("Rule Schema Lab Echo"),
        # A single rule-based entry with no fallback: a request with a body from any other segment is
        # rejected with 400 Function:800006.
        "inputSchema": [{"rule": code(FUNCTION_DIR, "IsCorporateSegment.csx"), "schema": CORPORATE}],
        "task": {
            "order": 1,
            "task": {"key": "sched-script-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"},
            "mapping": code(FUNCTION_DIR, "EchoMapping.csx"),
        },
    },
}

# Start-path cost probe (council condition C-3): the start transition's schema is chosen by the caller's
# role, so the start validates (inside its unit of work) after one identity-provider call when morph-idm is on.
start_probe = {
    "key": "rule-schema-lab-start",
    "flow": "sys-flows",
    "flowVersion": "1.0.0",
    "domain": "core",
    "version": "1.0.0",
    "tags": ["integration-test", "rule-schema-lab", "caller-roles"],
    "attributes": {
        "type": "F",
        "timeout": None,
        "labels": label("Rule Schema Lab (start schema selected by caller role)"),
        "functions": [],
        "features": [],
        "extensions": [],
        "startTransition": manual("start-rule-schema-lab-start", "open", "Start (schema by caller role)", schemas=[
            {"rule": code(ROOT, "IsApproverRole.csx"), "schema": schema_ref("rule-schema-lab-open-a")},
            {"schema": schema_ref("rule-schema-lab-open-b")},
        ]),
        "states": [
            {
                "key": "open",
                "stateType": 1,
                "subType": 0,
                "versionStrategy": "Major",
                "labels": label("Open"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [],
            }
        ],
    },
}

for path, doc in [(ROOT / "rule-schema-lab.json", workflow), (ROOT / "rule-schema-lab-start.json", start_probe), (FUNCTION_DIR / "rule-schema-lab-echo.json", function)]:
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n")
    print(f"wrote {path.relative_to(REPO)}")
