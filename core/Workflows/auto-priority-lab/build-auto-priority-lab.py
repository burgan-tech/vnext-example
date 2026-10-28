#!/usr/bin/env python3
"""Regenerates auto-priority-lab.json from the .csx sources in ./src.

The workflow JSON embeds every rule as base64 in `code` next to its `location`; edit the .csx files
and re-run this script — never hand-edit the base64 blobs.

    python3 core/Workflows/auto-priority-lab/build-auto-priority-lab.py
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent


def code(name):
    raw = (ROOT / "src" / name).read_bytes()
    return {"location": f"./src/{name}", "code": base64.b64encode(raw).decode()}


def label(text):
    return [{"language": "en-US", "label": text}]


def finish(key, text, sub_type=1):
    return {
        "key": key,
        "stateType": 3,
        "subType": sub_type,
        "versionStrategy": "Major",
        "labels": label(text),
        "view": None,
        "subFlow": None,
        "onEntries": [],
        "onExits": [],
        "transitions": [],
    }


workflow = {
    "key": "auto-priority-lab",
    "flow": "sys-flows",
    "flowVersion": "1.0.0",
    "domain": "core",
    "version": "1.0.0",
    "tags": ["integration-test", "auto-priority-lab", "automatic-transition", "priority"],
    "attributes": {
        "type": "F",
        "timeout": None,
        "labels": label("Auto Priority Lab (automatic transition priority order)"),
        "functions": [],
        "features": [],
        "extensions": [],
        "startTransition": {
            "key": "start-auto-priority-lab",
            "target": "evaluate",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start Auto Priority Lab"),
            "onExecutionTasks": [],
        },
        "states": [
            {
                # The whole scenario lives here. Declaration order is deliberately the OPPOSITE of
                # priority order: to-low (30) comes first, to-high (10) second, and the default
                # transition (no rule, no priority) last. Evaluation must follow priority, with the
                # default always last.
                "key": "evaluate",
                "stateType": 1,
                "subType": 0,
                "versionStrategy": "Major",
                "labels": label("Evaluate (three automatic transitions)"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [
                    {
                        "key": "to-low",
                        "target": "low",
                        "triggerType": 1,
                        "priority": 30,
                        "versionStrategy": "Minor",
                        "labels": label("To Low (priority 30, always true)"),
                        "rule": code("AlwaysTrueRule.csx"),
                        "onExecutionTasks": [],
                    },
                    {
                        "key": "to-high",
                        "target": "high",
                        "triggerType": 1,
                        "priority": 10,
                        "versionStrategy": "Minor",
                        "labels": label("To High (priority 10, false when skipHigh)"),
                        "rule": code("HighPriorityRule.csx"),
                        "onExecutionTasks": [],
                    },
                    {
                        # Default automatic transition: no rule, and it must NOT carry a priority.
                        "key": "to-fallback",
                        "target": "fallback",
                        "triggerType": 1,
                        "triggerKind": 10,
                        "versionStrategy": "Minor",
                        "labels": label("To Fallback (default, always last)"),
                        "onExecutionTasks": [],
                    },
                ],
            },
            finish("high", "High (priority 10 won)"),
            finish("low", "Low (priority 30 won)"),
            finish("fallback", "Fallback (default transition reached)"),
            finish("apl-cancelled", "Cancelled", sub_type=3),
        ],
        "cancel": {
            "key": "cancel-auto-priority-lab",
            "target": "apl-cancelled",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Cancel Auto Priority Lab"),
        },
    },
}


def main():
    out = ROOT / "auto-priority-lab.json"
    out.write_text(json.dumps(workflow, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
