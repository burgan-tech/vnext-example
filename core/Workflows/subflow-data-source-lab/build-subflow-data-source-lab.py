#!/usr/bin/env python3
"""
subflow-data-source-lab akislarini ve view'larini uretir.

    python3 core/Workflows/subflow-data-source-lab/build-subflow-data-source-lab.py

Bu lab TEK BIR SEYI sabitler: bir parent'in icinde AKTIF bir `S` SubFlow varken bir istemcinin
hangi instance'in VERISINI okumasi gerektigini ve runtime'in bunun icin ne verdigini.

  * Parent'in state function'i cocuga iner (state / view / transitions cocugun), ama `data.href`
    PARENT'in data function'ini gosterir ve parent'in verisi cocukta olan degisikligi tasimaz
    (Amorphie 474774, vnext#1084: parent data function 304 doner).
  * `activeCorrelations[]` icindeki her kaydin `href`'i COCUGUN data function'idir
    (`InstanceQueryAppService.BuildInstanceStateOutputAsync`). Istemci (vnext-client-workflow-manager,
    WMP 482207) aktif `S` korelasyonun bu href'ini kullanacak; lab o sozlesmeyi test olarak yazar.

Veri her seviyede farkli alanlarla isaretlenir, boylece "hangi instance'in verisi" alan adindan okunur:
parent `parentMarker` / `parentCounter`, cocuk `childMarker` / `childCounter`.

Akislar
-------
  PARENT (F)  parent-initial --auto--> parent-wait (view sds-parent-view)
                --enter-child (manuel)--> parent-child (S -> CHILD, mapping SdsParentToChildMapping)
                --auto (cocuk bitince)--> parent-after (view sds-parent-view)
                --finish-parent (manuel)--> parent-done
  CHILD  (S)  child-initial [SdsChildStartMapping] --auto--> child-wait (view sds-child-view)
                --touch (manuel, $self, SdsTouchMapping: childCounter+1)   <- veri degisir, state degismez
                --next  (manuel)--> child-wait-2 (view sds-child-view)       <- cocugun state'i ilerler
                --finish (manuel)--> child-done

Long-poll YOK, MockLab YOK (yalniz script task). Edit ./src/*.csx and re-run — never hand-edit the base64 blob.
"""

import base64
import json
import pathlib

HERE = pathlib.Path(__file__).resolve().parent
SRC = HERE / "src"
SRC.mkdir(exist_ok=True)
VIEWS = HERE.parents[1] / "Views" / "subflow-data-source-lab"
VIEWS.mkdir(parents=True, exist_ok=True)

# Publish is VERSION-IMMUTABLE (409 Instance:100002): bump on every fixture change.
FIXTURE_VERSION = "1.0.0"
VIEW_VERSION = "1.0.0"
TASK_VERSION = "1.0.0"

PARENT = "sds-parent"
CHILD = "sds-child"
TASK = "sds-script-task"
PARENT_VIEW = "sds-parent-view"
CHILD_VIEW = "sds-child-view"


def label(text):
    return [{"language": "en-US", "label": text}]


def view_ref(key):
    return {"key": key, "domain": "core", "version": VIEW_VERSION, "flow": "sys-views"}


def view_def(key):
    # loadData: the view renders instance data, so a client that honours the flag fetches it.
    return {"view": view_ref(key), "loadData": True}


def task_ref():
    return {"key": TASK, "domain": "core", "version": TASK_VERSION, "flow": "sys-tasks"}


def csx(name: str, body: str) -> dict:
    path = SRC / f"{name}.csx"
    path.write_text(body, encoding="utf-8")
    return {"location": f"./src/{name}.csx", "code": base64.b64encode(body.encode("utf-8")).decode("ascii")}


def script_task(name: str, body: str) -> dict:
    return {"order": 1, "task": task_ref(), "mapping": csx(name, body)}


ALWAYS_TRUE = """using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// Each flow walks itself to its waiting state so a test only starts the parent.
public class SdsAlwaysTrueRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context) => Task.FromResult(true);
}
"""

PARENT_START = """using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

// Marks the PARENT's data. A reader that sees `parentMarker` is reading the parent instance.
public class SdsParentStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();
        result.parentMarker = "parent";
        result.parentCounter = 0;
        if (data != null && HasProperty(data, "labRef"))
        {
            result.labRef = data.labRef;
        }
        LogInformation("SdsParentStartMapping: parentMarker set");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
"""

CHILD_START = """using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

// Marks the CHILD's data. A reader that sees `childMarker` is reading the child instance.
public class SdsChildStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();
        result.childMarker = "child";
        result.childCounter = 0;
        if (data != null)
        {
            if (HasProperty(data, "parentMarker"))
            {
                result.parentMarker = data.parentMarker;
            }
            if (HasProperty(data, "labRef"))
            {
                result.labRef = data.labRef;
            }
        }
        LogInformation("SdsChildStartMapping: childMarker set");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
"""

TOUCH = """using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

// `touch` ($self): changes the CHILD's data without changing the child's state. The parent's own
// data is untouched — exactly the moment the parent's data function keeps answering 304.
public class SdsTouchMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        var inst = context.Instance.Data as IDictionary<string, object>;
        if (inst != null)
        {
            foreach (var kv in inst)
            {
                target[kv.Key] = kv.Value;
            }
        }
        var current = 0;
        if (inst != null && inst.TryGetValue("childCounter", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out current);
        }
        target["childCounter"] = current + 1;
        target["lastTouchAt"] = DateTime.UtcNow.ToString("o");
        LogInformation($"SdsTouchMapping: childCounter {current} -> {current + 1}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
"""

PARENT_TO_CHILD = """using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// Input: hands the parent's marker to the child so the child's data also shows where it came from.
// Output: merges the child's result into the parent and stamps `childResult`.
public class SdsParentToChildMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic childInput = new ExpandoObject();
        if (data != null && HasProperty(data, "parentMarker"))
        {
            childInput.parentMarker = data.parentMarker;
        }
        if (data != null && HasProperty(data, "labRef"))
        {
            childInput.labRef = data.labRef;
        }
        LogInformation("SdsParentToChildMapping: prepared child input");
        return Task.FromResult(new ScriptResponse { Data = childInput });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;
        dynamic merged = new ExpandoObject();
        var target = (IDictionary<string, object>)merged;
        var inst = context.Instance.Data as IDictionary<string, object>;
        if (inst != null)
        {
            foreach (var kv in inst)
            {
                target[kv.Key] = kv.Value;
            }
        }
        if (body != null)
        {
            foreach (var kv in body)
            {
                target[kv.Key] = kv.Value;
            }
        }
        target["childResult"] = true;
        LogInformation("SdsParentToChildMapping: merged child output");
        return Task.FromResult(new ScriptResponse { Data = merged });
    }
}
"""


def envelope(key, attributes, *tags):
    return {
        "key": key,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": FIXTURE_VERSION,
        "tags": ["integration-test", "subflow-data-source-lab", *tags],
        "attributes": attributes,
    }


def auto(key, target):
    return {
        "key": key,
        "target": target,
        "triggerType": 1,
        "versionStrategy": "Minor",
        "labels": label("Auto"),
        "rule": csx("SdsAlwaysTrueRule", ALWAYS_TRUE),
        "onExecutionTasks": [],
    }


def manual(key, target, text, view=None, tasks=None):
    transition = {
        "key": key,
        "target": target,
        "triggerType": 0,
        "versionStrategy": "Minor",
        "labels": label(text),
        "onExecutionTasks": tasks or [],
    }
    if view is not None:
        transition["view"] = view_def(view)
    return transition


def state(key, state_type, sub_type, text, transitions, view=None, on_entries=None, sub_flow=None):
    return {
        "key": key,
        "stateType": state_type,
        "subType": sub_type,
        "versionStrategy": "Major",
        "labels": label(text),
        "view": view_def(view) if view else None,
        "subFlow": sub_flow,
        "onEntries": on_entries or [],
        "onExits": [],
        "transitions": transitions,
    }


def finish_states(prefix):
    return [
        state(f"{prefix}-done", 3, 1, f"{prefix} done", []),
        state(f"{prefix}-cancelled", 3, 7, f"{prefix} cancelled", []),
    ]


def cancel(prefix):
    return {
        "cancel": {"key": f"cancel-{prefix}", "target": f"{prefix}-cancelled", "triggerType": 0,
                   "versionStrategy": "Major", "labels": label("Cancel")},
    }


def build_child():
    attributes = {
        "type": "S",
        "timeout": None,
        "labels": label("SubFlow Data Source Lab Child"),
        "functions": [], "features": [], "extensions": [], "sharedTransitions": [],
        **cancel("child"),
        "startTransition": {"key": f"start-{CHILD}", "target": "child-initial", "triggerType": 0,
                            "versionStrategy": "Major", "labels": label("Start Child")},
        "states": [
            state("child-initial", 1, 0, "Child Initial",
                  [auto("auto-child-to-wait", "child-wait")],
                  on_entries=[script_task("SdsChildStartMapping", CHILD_START)]),
            # subType 6 (Human): the state waits for a manual step.
            state("child-wait", 2, 6, "Child Wait (view, manual steps)", [
                manual("touch", "$self", "Touch (child data only)",
                       tasks=[script_task("SdsTouchMapping", TOUCH)]),
                manual("next", "child-wait-2", "Next"),
                manual("finish", "child-done", "Finish"),
            ], view=CHILD_VIEW),
            state("child-wait-2", 2, 6, "Child Wait 2 (view)", [
                manual("finish", "child-done", "Finish"),
            ], view=CHILD_VIEW),
            *finish_states("child"),
        ],
    }
    return envelope(CHILD, attributes, "child")


def build_parent():
    attributes = {
        "type": "F",
        "timeout": None,
        "labels": label("SubFlow Data Source Lab Parent"),
        "functions": [], "features": [], "extensions": [], "sharedTransitions": [],
        **cancel("parent"),
        "startTransition": {"key": f"start-{PARENT}", "target": "parent-initial", "triggerType": 0,
                            "versionStrategy": "Major", "labels": label("Start Parent"),
                            "onExecutionTasks": [script_task("SdsParentStartMapping", PARENT_START)]},
        "states": [
            state("parent-initial", 1, 0, "Parent Initial", [auto("auto-parent-to-wait", "parent-wait")]),
            state("parent-wait", 2, 6, "Parent Wait (view)", [
                manual("enter-child", "parent-child", "Enter Child"),
            ], view=PARENT_VIEW),
            state("parent-child", 4, 0, "Parent Child (S)", [auto("auto-parent-to-after", "parent-after")],
                  sub_flow={
                      "type": "S",
                      "process": {"key": CHILD, "domain": "core", "version": FIXTURE_VERSION, "flow": "sys-flows"},
                      "mapping": csx("SdsParentToChildMapping", PARENT_TO_CHILD),
                  }),
            # Distinct key from the child's `finish`: while the child is open a transition sent to the
            # parent is forwarded to the child; the names must not read as the same step.
            state("parent-after", 2, 6, "Parent After (view)", [
                manual("finish-parent", "parent-done", "Finish Parent"),
            ], view=PARENT_VIEW),
            *finish_states("parent"),
        ],
    }
    return envelope(PARENT, attributes, "parent")


def text(content, variant="bodyLarge"):
    return {"type": "Text", "content": content, "variant": variant}


def build_view(key, title, fields):
    return {
        "key": key,
        "version": VIEW_VERSION,
        "domain": "core",
        "flow": "sys-views",
        "flowVersion": "1.0.0",
        "tags": ["integration-test", "subflow-data-source-lab", "pseudo-ui"],
        "attributes": {
            "type": 1,
            "display": "full-page",
            "renderer": "pseudo-ui",
            "content": {
                "$schema": "https://amorphie.io/meta/view-vocabulary/1.0",
                "view": {
                    "type": "Column",
                    "gap": "md",
                    "children": [
                        text({"en": title, "tr": title}, "headlineMedium"),
                        # One line per field: the screen reads which instance's data it is showing.
                        *[text(f"$instance.{field}") for field in fields],
                    ],
                },
            },
        },
    }


def write(doc, folder=HERE):
    path = folder / f"{doc['key']}.json"
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print("wrote", path.relative_to(HERE.parents[2]))


def main():
    write(build_view(PARENT_VIEW, "sds parent", ["parentMarker", "parentCounter", "childResult", "childCounter"]), VIEWS)
    write(build_view(CHILD_VIEW, "sds child", ["childMarker", "childCounter", "parentMarker", "lastTouchAt"]), VIEWS)
    write(build_child())
    write(build_parent())


if __name__ == "__main__":
    main()
