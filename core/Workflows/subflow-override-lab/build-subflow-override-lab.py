#!/usr/bin/env python3
"""
subflow-override-lab akislarini ve view'larini uretir.

    python3 core/Workflows/subflow-override-lab/build-subflow-override-lab.py

Bu lab TEK BIR SEYI olcer: bir parent'in `state.subFlow.overrides` ile, tukettigi COCUGUN
state'lerine verdigi iki yeni override'in (runtime 2026-09-23, branch
feature/subflow-override-interaction-views) uctan uca davranisini:

  * `overrides.states.<childState>.interaction.longPoll.{fallbackTimeoutSeconds, roles}`
    — alan duzeyinde: yazilmayan alan cocugunkini korur; `roles` listesi butun olarak degisir;
    override long-poll'u OLMAYAN bir state'e long-poll EKLEMEZ.
  * `overrides.states.<childState>.views.<viewKey>` ve
    `overrides.transitions.<childTransition>.views.<viewKey>` — cocugun KENDI kurallarinin sectigi
    view'un referansini degistirir; state ve transition birbirine dusmez.

Override'lar cocuga baslangicta damgalanir ve COCUK tarafinda, cocugun kendi CurrentState'i
uzerinden cozulur — bu yuzden cocuk dogrudan adreslendiginde de gecerlidir ve tek hop'tur.

Akislar
-------
  CHILD  (S)  child-initial --auto--> lp-wait
               lp-wait : interaction.longPoll {terminate, fallback 600, roles [ovr.child-ack]}
                         view child-lp-view
                         transitions: confirm (view child-confirm-view), note (view child-lp-view)
  PLAIN-CHILD (S) plain-initial --auto--> plain-wait (long-poll YOK, view child-plain-view)

  PARENT         (F) waiting -> CHILD   overrides.states.lp-wait:
                                          interaction.longPoll.fallbackTimeoutSeconds = 120 (yalniz sure)
                                          views { child-lp-view -> parent-lp-view }
                                        overrides.transitions.confirm.views
                                          { child-confirm-view -> parent-confirm-view }
  PARENT-SHORT   (F) waiting -> CHILD   overrides.states.lp-wait.interaction.longPoll.fallback = 5
                                        (pencere davranissal olarak olculur: cocuk ~5 s'de devam etmeli)
  PARENT-ROLES   (F) waiting -> CHILD   overrides.states.lp-wait:
                                          interaction.longPoll.roles = [ovr.parent-ack] (yalniz roller)
                                          views { child-lp-view -> <YOK olan view> }  (D10: geri dusus)
  PARENT-NOLP    (F) waiting -> PLAIN-CHILD
                                        overrides.states.plain-wait.interaction.longPoll.fallback = 90
                                        (cocukta long-poll yok -> yok sayilir, EventId 20305)
  PARENT-LEGACY  (F) waiting -> CHILD   overrides.views { child-lp-view -> legacy-view }
                                        (eski, parent-tarafli harita — regresyon kontrolu)
  TOP (F) waiting -> MID (S) mid-wait -> CHILD
                                        TOP'un override'i `lp-wait` anahtarini adlandirir — o anahtar
                                        MID'de YOK, torunda (CHILD) var. Tek hop: torunu etkilememeli.
                                        MID kendi override'ini bildirmez.

Neden ayri bir akis: authorization-chain-lab'in leaf'i zaten bir long-poll tasiyor ve
AckAndParentRetainedTests onun `roles = [chain.admin]` oldugunu varsayiyor; pencereyi/rolleri
override etmek o suite'in ack sozlesmesini degistirirdi.

Sema acigi: kurulu @burgan-tech/vnext-schema `subFlowStateOverride.interaction`/`views` ve
`subFlowTransitionOverride.views` alanlarini tanimiyor (`additionalProperties: false`), yani bu
akislar `npm run validate`'ten gecmez. SDK'nin LocalDomainPublisher'i JSON'u dogrudan
/definitions/publish'e gonderdigi icin test icin engel degil; sema degisikligi yerel bir
vnext-schema dalinda, yayinlanmadi.
"""

import base64
import json
import pathlib

HERE = pathlib.Path(__file__).resolve().parent
SRC = HERE / "src"
SRC.mkdir(exist_ok=True)
VIEWS = HERE.parents[1] / "Views" / "subflow-override-lab"
VIEWS.mkdir(parents=True, exist_ok=True)

# Publish is VERSION-IMMUTABLE (409 Instance:100002): bump on every fixture change.
FIXTURE_VERSION = "1.0.2"
VIEW_VERSION = "1.0.0"

CHILD_ACK = "ovr.child-ack"
PARENT_ACK = "ovr.parent-ack"

CHILD = "subflow-override-lab-child"
PLAIN_CHILD = "subflow-override-lab-plain-child"
MID = "subflow-override-lab-mid"

CHILD_LP_VIEW = "subflow-override-lab-child-lp-view"
CHILD_CONFIRM_VIEW = "subflow-override-lab-child-confirm-view"
CHILD_PLAIN_VIEW = "subflow-override-lab-child-plain-view"
PARENT_LP_VIEW = "subflow-override-lab-parent-lp-view"
PARENT_CONFIRM_VIEW = "subflow-override-lab-parent-confirm-view"
LEGACY_VIEW = "subflow-override-lab-legacy-view"
MISSING_VIEW = "subflow-override-lab-missing-view"  # never published — D10 fallback probe


def grants(*roles):
    return [{"role": r, "grant": "allow"} for r in roles]


def label(text):
    return [{"language": "en-US", "label": text}]


def view_ref(key):
    return {"key": key, "domain": "core", "version": VIEW_VERSION, "flow": "sys-views"}


def view_def(key):
    return {"view": view_ref(key), "loadData": False}


def csx(name: str, body: str) -> dict:
    path = SRC / f"{name}.csx"
    path.write_text(body, encoding="utf-8")
    return {"location": f"./src/{name}.csx", "code": base64.b64encode(body.encode("utf-8")).decode("ascii")}


SUBFLOW_MAPPING = """using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// Pass-through for data. It DOES forward the caller's role headers: SubflowStarter sends the child
// only the headers the input mapping returns, and the long-poll arm (order 75) pauses only when the
// TRIGGERING caller satisfies the effective long-poll roles. Without this forward a roles-armed
// long-poll in a SubFlow child never pauses, and nothing about the override would be observable.
public class OverrideLabSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        dynamic input = new ExpandoObject();
        input.startedByOverrideLab = true;

        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "x-roles", "role", "user_reference", "x-device-id", "x-token-id" })
        {
            try
            {
                string? value = (string?)context.Headers[name];
                if (!string.IsNullOrEmpty(value)) headers[name] = value;
            }
            catch (Exception)
            {
                // header absent
            }
        }

        return Task.FromResult(new ScriptResponse { Data = input, Headers = headers });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
        => Task.FromResult(new ScriptResponse());
}
"""

ALWAYS_TRUE = """using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// Each flow walks itself to its waiting state so a test only starts the top.
public class OverrideLabAlwaysTrueRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context) => Task.FromResult(true);
}
"""


def envelope(key, attributes, *tags):
    return {
        "key": key,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": FIXTURE_VERSION,
        "tags": ["integration-test", "subflow-override-lab", *tags],
        "attributes": attributes,
    }


def initial_state(prefix, target):
    return {
        "key": f"{prefix}-initial",
        "stateType": 1,
        "subType": 0,
        "versionStrategy": "Major",
        "labels": label(f"{prefix} initial"),
        "view": None,
        "subFlow": None,
        "transitions": [{
            "key": f"auto-{prefix}-to-{target}",
            "target": target,
            "triggerType": 1,
            "versionStrategy": "Minor",
            "labels": label("Auto"),
            "rule": csx("OverrideLabAlwaysTrueRule", ALWAYS_TRUE),
        }],
    }


def finish_states(prefix):
    return [
        {"key": f"{prefix}-done", "stateType": 3, "subType": 1, "versionStrategy": "Major",
         "labels": label(f"{prefix} done"), "view": None, "subFlow": None, "transitions": []},
        {"key": f"{prefix}-cancelled", "stateType": 3, "subType": 7, "versionStrategy": "Major",
         "labels": label(f"{prefix} cancelled"), "view": None, "subFlow": None, "transitions": []},
    ]


def subflow_state(key, child_key, overrides=None):
    state = {
        "key": key,
        "stateType": 4,
        "subType": 0,
        "versionStrategy": "Major",
        "labels": label(key),
        "view": None,
        "transitions": [],
        "subFlow": {
            "type": "S",
            "process": {"key": child_key, "domain": "core", "version": FIXTURE_VERSION, "flow": "sys-flows"},
            "mapping": csx("OverrideLabSubFlowMapping", SUBFLOW_MAPPING),
        },
    }
    if overrides is not None:
        state["subFlow"]["overrides"] = overrides
    return state


def cancel(prefix):
    return {
        "cancel": {"key": f"cancel-{prefix}", "target": f"{prefix}-cancelled", "triggerType": 0,
                   "versionStrategy": "Major", "labels": label("Cancel")},
    }


def build_child():
    attributes = {
        "type": "S",
        "timeout": None,
        "labels": label("SubFlow Override Lab Child"),
        "functions": [], "features": [], "extensions": [], "sharedTransitions": [],
        "startTransition": {"key": f"start-{CHILD}", "target": "child-initial", "triggerType": 0,
                            "versionStrategy": "Major", "labels": label("Start Child")},
        "states": [
            initial_state("child", "lp-wait"),
            {
                "key": "lp-wait",
                "stateType": 2,
                "subType": 6,
                "versionStrategy": "Major",
                "labels": label("Long-poll Wait"),
                "view": view_def(CHILD_LP_VIEW),
                "subFlow": None,
                # 600 s: long enough that the fallback never fires inside a test run, and far from
                # every override value below so a leaked value is unmistakable.
                "interaction": {"longPoll": {"terminate": True, "fallbackTimeoutSeconds": 600,
                                             "roles": grants(CHILD_ACK)}},
                "transitions": [
                    {"key": "confirm", "target": "child-done", "triggerType": 0, "versionStrategy": "Minor",
                     "labels": label("Confirm"), "view": view_def(CHILD_CONFIRM_VIEW)},
                    # Same view KEY as the state view, no transition override: the state override
                    # must not leak into a transition view that happens to share the key.
                    {"key": "note", "target": "$self", "triggerType": 0, "versionStrategy": "Minor",
                     "labels": label("Note"), "view": view_def(CHILD_LP_VIEW)},
                ],
            },
            *finish_states("child"),
        ],
    }
    return envelope(CHILD, attributes, "child")


def build_plain_child():
    attributes = {
        "type": "S",
        "timeout": None,
        "labels": label("SubFlow Override Lab Plain Child"),
        "functions": [], "features": [], "extensions": [], "sharedTransitions": [],
        "startTransition": {"key": f"start-{PLAIN_CHILD}", "target": "plain-initial", "triggerType": 0,
                            "versionStrategy": "Major", "labels": label("Start Plain Child")},
        "states": [
            initial_state("plain", "plain-wait"),
            {
                "key": "plain-wait",
                "stateType": 2,
                "subType": 6,
                "versionStrategy": "Major",
                "labels": label("Plain Wait (no long-poll)"),
                "view": view_def(CHILD_PLAIN_VIEW),
                "subFlow": None,
                "transitions": [
                    {"key": "finish-plain", "target": "plain-done", "triggerType": 0,
                     "versionStrategy": "Minor", "labels": label("Finish")},
                ],
            },
            *finish_states("plain"),
        ],
    }
    return envelope(PLAIN_CHILD, attributes, "plain-child")


def build_mid():
    attributes = {
        "type": "S",
        "timeout": None,
        "labels": label("SubFlow Override Lab Mid"),
        "functions": [], "features": [], "extensions": [], "sharedTransitions": [],
        "startTransition": {"key": f"start-{MID}", "target": "mid-initial", "triggerType": 0,
                            "versionStrategy": "Major", "labels": label("Start Mid")},
        "states": [
            initial_state("mid", "mid-wait"),
            # No overrides of its own: whatever reaches the grandchild could only have come from TOP.
            subflow_state("mid-wait", CHILD),
            *finish_states("mid"),
        ],
    }
    return envelope(MID, attributes, "mid")


def build_parent(key, child, overrides, tag):
    attributes = {
        "type": "F",
        "timeout": None,
        "labels": label(f"SubFlow Override Lab {tag}"),
        "functions": [], "features": [], "extensions": [], "sharedTransitions": [],
        **cancel("parent"),
        "startTransition": {"key": f"start-{key}", "target": "parent-initial", "triggerType": 0,
                            "versionStrategy": "Major", "labels": label("Start")},
        "states": [
            initial_state("parent", "waiting"),
            subflow_state("waiting", child, overrides),
            *finish_states("parent"),
        ],
    }
    return envelope(key, attributes, tag)


def build_view(key, text):
    return {
        "key": key,
        "version": VIEW_VERSION,
        "domain": "core",
        "flow": "sys-views",
        "flowVersion": "1.0.0",
        "tags": ["integration-test", "subflow-override-lab", "pseudo-ui"],
        "attributes": {
            "type": 1,
            "display": "full-page",
            "renderer": "pseudo-ui",
            "content": {
                "$schema": "https://amorphie.io/meta/view-vocabulary/1.0",
                "view": {"type": "Text", "content": {"en": text, "tr": text}, "variant": "headlineMedium"},
            },
        },
    }


def write(doc, folder=HERE):
    path = folder / f"{doc['key']}.json"
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print("wrote", path.relative_to(HERE.parents[2]))


def main():
    for key, text in [
        (CHILD_LP_VIEW, "child long-poll view"),
        (CHILD_CONFIRM_VIEW, "child confirm view"),
        (CHILD_PLAIN_VIEW, "child plain view"),
        (PARENT_LP_VIEW, "parent state-scoped override"),
        (PARENT_CONFIRM_VIEW, "parent transition-scoped override"),
        (LEGACY_VIEW, "legacy parent-side override"),
    ]:
        write(build_view(key, text), VIEWS)

    write(build_child())
    write(build_plain_child())
    write(build_mid())

    write(build_parent("subflow-override-lab-parent", CHILD, {
        "states": {"lp-wait": {
            "interaction": {"longPoll": {"fallbackTimeoutSeconds": 120}},
            "views": {CHILD_LP_VIEW: view_ref(PARENT_LP_VIEW)},
        }},
        "transitions": {"confirm": {"views": {CHILD_CONFIRM_VIEW: view_ref(PARENT_CONFIRM_VIEW)}}},
    }, "parent"))

    # The window is not persisted anywhere readable (see README: the long-poll ack InstanceJobs row
    # carries no ExecuteAt, and the Dapr job is scheduled directly). So the duration override is
    # proven BEHAVIOURALLY: a 5 s override on a 600 s child must resume the child within seconds.
    write(build_parent("subflow-override-lab-parent-short", CHILD, {
        "states": {"lp-wait": {"interaction": {"longPoll": {"fallbackTimeoutSeconds": 5}}}},
    }, "parent-short"))

    write(build_parent("subflow-override-lab-parent-roles", CHILD, {
        "states": {"lp-wait": {
            "interaction": {"longPoll": {"roles": grants(PARENT_ACK)}},
            "views": {CHILD_LP_VIEW: view_ref(MISSING_VIEW)},
        }},
    }, "parent-roles"))

    write(build_parent("subflow-override-lab-parent-nolp", PLAIN_CHILD, {
        "states": {"plain-wait": {"interaction": {"longPoll": {"fallbackTimeoutSeconds": 90}}}},
    }, "parent-nolp"))

    write(build_parent("subflow-override-lab-parent-legacy", CHILD, {
        "views": {CHILD_LP_VIEW: view_ref(LEGACY_VIEW)},
    }, "parent-legacy"))

    write(build_parent("subflow-override-lab-top", MID, {
        # Names a GRANDCHILD state. One hop: must not reach it.
        "states": {"lp-wait": {
            "interaction": {"longPoll": {"fallbackTimeoutSeconds": 45, "roles": grants(PARENT_ACK)}},
            "views": {CHILD_LP_VIEW: view_ref(PARENT_LP_VIEW)},
        }},
    }, "top"))


if __name__ == "__main__":
    main()
