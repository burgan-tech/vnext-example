#!/usr/bin/env python3
"""Regenerates fan-out-documents.json from the .csx sources in ./src.

The workflow JSON embeds every mapping/rule as base64 in `code` next to its
`location`; edit the .csx files and re-run this script — never hand-edit the
base64 blobs.

    python3 core/Workflows/fan-out-documents/build-fan-out-documents.py
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent

# BUMP THIS whenever any .csx under ./src changes. POST /api/v1/definitions/publish has no
# overwrite: an unchanged version answers 409 "A record with the same version already exists" and
# the runtime keeps serving the OLD embedded scripts, so an edited mapping silently does nothing.
# 1.0.1 — dropped the redundant IFanOutMapping.OutputHandler (the runtime's default packaging is
#         what the scenario asserts now) and bracketed the batch with the before/after stamp pair.
# 1.0.2 — DOC-SLOW ids now route to api/fan-out/slow-documents/process. MockLab matches by PREFIX,
#         so the old api/fan-out/documents/process-slow was permanently shadowed by
#         api/fan-out/documents/process: the straggler answered in ~15ms, the delay never applied,
#         and fanout-load.py's straggler-ratio metric was measuring jitter.
VERSION = "1.0.2"


def code(name):
    raw = (ROOT / "src" / name).read_bytes()
    return {
        "location": f"./src/{name}",
        "code": base64.b64encode(raw).decode(),
    }


def label(text):
    return [{"language": "en-US", "label": text}]


def stamp_task():
    return {"key": "fanout-stamp-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}


def stamp_before_task():
    # A SECOND script-task component, identical in config to fanout-stamp-task. It exists only so
    # the pre-batch and post-batch stamps are distinct task keys: two entries in one onEntries list
    # sharing a task key collide in the InstanceTask journal.
    return {"key": "fanout-stamp-before-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}


def fan_out_task():
    # TaskType 21. Its own config (itemsPath / inner task / execution / join) lives in the task
    # component; the workflow only references it and supplies the IFanOutMapping.
    return {"key": "fan-out-documents-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}


workflow = {
    "key": "fan-out-documents",
    "flow": "sys-flows",
    "flowVersion": "1.0.0",
    "domain": "core",
    "version": VERSION,
    "tags": [
        "integration-test",
        "fan-out-documents",
        "fan-out",
        "task-type-21",
        "all-settled",
        "single-write-invariant",
        "bulkhead",
    ],
    "attributes": {
        "type": "F",
        "timeout": None,
        "labels": label("Fan-Out Documents"),
        "functions": [],
        "features": [],
        "extensions": [],
        "startTransition": {
            "key": "start-fan-out-documents",
            "target": "documents-received",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start Fan-Out Documents"),
            # Counts the caller-supplied documents and stamps the pre-batch data version.
            "onExecutionTasks": [
                {"order": 1, "task": stamp_task(), "mapping": code("FanOutStartMapping.csx")}
            ],
        },
        "states": [
            {
                "key": "documents-received",
                "stateType": 1,
                "subType": 0,
                "versionStrategy": "Major",
                "labels": label("Documents Received"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [
                    {
                        "key": "process-documents",
                        "target": "documents-processing",
                        "triggerType": 0,
                        "versionStrategy": "Patch",
                        "labels": label("Process Documents (fan out)"),
                        "onExecutionTasks": [],
                    }
                ],
            },
            {
                "key": "documents-processing",
                "stateType": 2,
                "subType": 0,
                "versionStrategy": "Patch",
                "labels": label("Documents Processing (fan-out batch)"),
                "view": None,
                "subFlow": None,
                # ORDER IS LOAD-BEARING — this triple IS the single-write measurement.
                #
                #   1  stamp BEFORE  -> versionBeforeFanOutBatch (the row it supersedes), 1 write
                #   2  the batch     -> must be exactly 1 write however many items there are
                #   3  stamp AFTER   -> versionAfterFanOut (what the next task sees)
                #
                # so patch(after) - patch(before) == 2 iff the batch wrote once. All three sit in
                # ONE state entry: no transition, no state change, nothing else between them.
                # Anything inserted here widens the delta and silently voids the assertion.
                #
                # The pre-batch stamp is a separate task because the fan-out mapping no longer
                # overrides OutputHandler (the runtime's default packaging is what the tests now
                # assert against), and the default packaging cannot carry instrumentation keys.
                "onEntries": [
                    {"order": 1, "task": stamp_before_task(), "mapping": code("FanOutStampBeforeMapping.csx")},
                    {"order": 2, "task": fan_out_task(), "mapping": code("FanOutDocumentsMapping.csx")},
                    {"order": 3, "task": stamp_task(), "mapping": code("FanOutStampAfterMapping.csx")},
                ],
                "onExits": [],
                # Evaluated by RunAutomaticTransitionsStep (order 90), i.e. AFTER the onEntry
                # tasks (order 60) have written the summary. Mutually exclusive by construction.
                "transitions": [
                    {
                        "key": "auto-partial-failure",
                        "target": "documents-partial-failure",
                        "triggerType": 1,
                        "versionStrategy": "Patch",
                        "labels": label("Some Documents Failed"),
                        "rule": code("PartialFailureRule.csx"),
                        "onExecutionTasks": [],
                    },
                    {
                        "key": "auto-all-succeeded",
                        "target": "documents-completed",
                        "triggerType": 1,
                        "versionStrategy": "Patch",
                        "labels": label("All Documents Processed"),
                        "rule": code("AllSucceededRule.csx"),
                        "onExecutionTasks": [],
                    },
                ],
            },
            {
                "key": "documents-completed",
                "stateType": 3,
                "subType": 1,
                "versionStrategy": "Major",
                "labels": label("Documents Completed"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [],
            },
            {
                "key": "documents-partial-failure",
                "stateType": 3,
                "subType": 2,
                "versionStrategy": "Major",
                "labels": label("Documents Partially Failed"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [],
            },
            {
                "key": "documents-cancelled",
                "stateType": 3,
                "subType": 7,
                "versionStrategy": "Major",
                "labels": label("Documents Cancelled"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [],
            },
        ],
        "cancel": {
            "key": "cancel-fan-out-documents",
            "target": "documents-cancelled",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Cancel Fan-Out Documents"),
        },
    },
}


def main():
    out = ROOT / "fan-out-documents.json"
    out.write_text(json.dumps(workflow, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
