#!/usr/bin/env python3
"""Regenerates busy-subtype-armed-timer.json from the .csx sources in ./src.

The workflow JSON embeds every mapping / rule / timer as base64 in `code` next to its
`location`; edit the .csx files and re-run this script — never hand-edit the base64 blobs.

    python3 core/Workflows/busy-subtype-armed-timer/build-busy-subtype-armed-timer.py
"""

import base64
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent


def code(name):
    raw = (ROOT / "src" / name).read_bytes()
    return {"location": f"./src/{name}", "code": base64.b64encode(raw).decode()}


def timer(name):
    return {"type": "L", **code(name)}


def script_task():
    # Shared with schedule-after-auto. No transition runs it twice at the same order, so the
    # task-journal ExecutionKey (transition, task, order) cannot collide.
    return {"key": "sched-script-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}


def label(text):
    return [{"language": "en-US", "label": text}]


def hook(mapping_file, order=1):
    return {"order": order, "task": script_task(), "mapping": code(mapping_file)}


def finish(key, sub_type, text):
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


def wait_state(key, gate_key, timeout_key, text, interaction=None):
    """A Busy-SUBTYPE wait state (the instance rests Busy here by design) carrying a data-gated
    automatic transition and a scheduled timeout. Only an updateData can open the gate."""
    state = {
        "key": key,
        "stateType": 2,
        "subType": 5,
        "versionStrategy": "Minor",
        "labels": label(text),
        "view": None,
        "subFlow": None,
        "onEntries": [],
        "onExits": [],
        "transitions": [
            {
                "key": gate_key,
                "target": "contract-signed",
                "triggerType": 1,
                "versionStrategy": "Minor",
                "labels": label("Gate open (contractReady = true)"),
                "rule": code("ContractReadyRule.csx"),
                "onExecutionTasks": [hook("ReadyMapping.csx")],
            },
            {
                "key": timeout_key,
                "target": "contract-timedout",
                "triggerType": 2,
                "versionStrategy": "Minor",
                "labels": label("Timeout (timerSeconds)"),
                "timer": timer("ContractTimeoutTimer.csx"),
                "onExecutionTasks": [hook("TimeoutMapping.csx")],
            },
        ],
    }
    if interaction is not None:
        state["interaction"] = interaction
    return state


def go(key, target, text):
    return {
        "key": key,
        "target": target,
        "triggerType": 0,
        "versionStrategy": "Minor",
        "labels": label(text),
        "onExecutionTasks": [],
    }


workflow = {
    "key": "busy-subtype-armed-timer",
    "flow": "sys-flows",
    "flowVersion": "1.0.0",
    "domain": "core",
    "version": "1.0.1",
    "tags": [
        "integration-test",
        "busy-subtype",
        "interaction",
        "update-data",
        "scheduled-transition",
        "automatic-transition",
    ],
    "attributes": {
        "type": "F",
        "timeout": None,
        "labels": label("Busy Subtype + Armed Timer (interaction wait)"),
        "functions": [],
        "features": [],
        "extensions": [],
        "startTransition": {
            "key": "start-busy-subtype-armed-timer",
            "target": "route",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start Busy Subtype Armed Timer"),
            "onExecutionTasks": [hook("SeedMapping.csx")],
        },
        "states": [
            {
                # Picks the wait variant with a MANUAL transition: only a manual hop pauses at a
                # terminating long-poll (the AutoChain profile excludes step 75).
                "key": "route",
                "stateType": 1,
                "subType": 0,
                "versionStrategy": "Major",
                "labels": label("Route (pick the wait variant)"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [
                    go("go-interaction", "contract-wait", "Interaction wait (keep polling)"),
                    go("go-ack", "ack-wait", "Interaction wait (terminate + ack)"),
                    go("go-plain", "plain-wait", "Busy subtype without interaction"),
                ],
            },
            # The QA shape: interaction that keeps the client polling, waiting for the callback.
            wait_state("contract-wait", "contract-ready", "contract-timeout",
                       "Contract Wait (interaction, keep polling)",
                       {"longPoll": {"terminate": False, "fallbackTimeoutSeconds": 30}}),
            # Terminating interaction: pauses for the client's acknowledge; the timer is armed after it.
            wait_state("ack-wait", "ack-ready", "ack-timeout",
                       "Ack Wait (interaction, terminate + ack)",
                       {"longPoll": {"terminate": True, "fallbackTimeoutSeconds": 120}}),
            # Control: Busy subtype WITHOUT interaction keeps the old rule (timer refused as busy).
            wait_state("plain-wait", "plain-ready", "plain-timeout",
                       "Plain Wait (Busy subtype, no interaction)"),
            {
                # Where an opened gate parks. Not a finish state and without a timer, so the test
                # can watch the wait state's timer stay cancelled after the instance has left it.
                "key": "contract-signed",
                "stateType": 2,
                "subType": 0,
                "versionStrategy": "Minor",
                "labels": label("Contract Signed"),
                "view": None,
                "subFlow": None,
                "onEntries": [],
                "onExits": [],
                "transitions": [go("finish-signed", "signed-done", "Finish Signed")],
            },
            finish("signed-done", 1, "Signed Done"),
            finish("contract-timedout", 8, "Timed Out (scheduled transition fired)"),
            finish("bsat-cancelled", 3, "Cancelled"),
        ],
        "cancel": {
            "key": "cancel-busy-subtype-armed-timer",
            "target": "bsat-cancelled",
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Cancel Busy Subtype Armed Timer"),
        },
        "updateData": {
            "key": "update-contract-data",
            "target": "$self",
            "triggerType": 0,
            "versionStrategy": "Minor",
            "labels": label("Update Contract Data (contract callback)"),
            "onExecutionTasks": [hook("UpdateContractMapping.csx")],
        },
    },
}


def main():
    out = ROOT / "busy-subtype-armed-timer.json"
    out.write_text(json.dumps(workflow, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
