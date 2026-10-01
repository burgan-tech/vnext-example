#!/usr/bin/env python3
"""
subflow-depth-lab: 5 seviyeli ic ice SubFlow zinciri uretir (csx -> base64 gomulu workflow JSON'lari).

    python3 core/Workflows/subflow-depth-lab/build-subflow-depth-lab.py

Uretilenler:
  subflow-depth-lab-l1.json  (type F)  l1 -> l2
  subflow-depth-lab-l2.json  (type S)  l2 -> l3
  subflow-depth-lab-l3.json  (type S)  l3 -> l4
  subflow-depth-lab-l4.json  (type S)  l4 -> l5
  subflow-depth-lab-l5.json  (type S)  yaprak, girdi bekler
  src/*.csx

Akis, ic ice SubFlow'da root'a gonderilen bir transition'in maliyetini DERINLIGE GORE olcmek
icin kurulmustur (api-tests/subflow-depth-lab/depth-latency.py):

  * Zincir tamamen auto transition ile kurulur; lK baslatildiginda l5 `l5-waiting`de Active
    bekler, ustundeki her seviye acik SubFlow korelasyonu boyunca Busy olur.
  * Derinlik d, zincirin `l{6-d}` seviyesinden baslatilmasiyla secilir: d=5 -> l1, d=1 -> l5.
    (`type: S` bir akisin dogrudan API'den baslatilmasi desteklenir.)
  * `leaf-ping` (l5-waiting -> l5-waiting, manuel, task'siz) root'tan gonderilir ve her
    seviyede forward edilerek yapraga iner. Task'siz olmasi runtime'in seviye basi sabit
    maliyetini izole eder; tekrar tekrar gonderilebilir.
  * `leaf-finish` yapragi bitirir; tamamlanma zincir boyunca yukari resume eder.
  * Hic task yoktur: olculen sure tamamen runtime'in forward / settle / relay maliyetidir.
"""

import base64
import json
import os

ROOT = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(ROOT, "src")
VERSION = "1.0.0"
DEPTH = 5
PREFIX = "subflow-depth-lab"

ALWAYS_TRUE = '''using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class AlwaysTrueRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        return Task.FromResult(true);
    }
}
'''

SUBFLOW_MAPPING = '''using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Seviye __FROM__ -> __TO__ subflow mapping'i. Giris: yalniz testId; cikis: parent verisi + alt sonucu.
/// </summary>
public class __CLASS__ : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic subInput = new ExpandoObject();
        if (data != null && HasProperty(data, "testId"))
        {
            subInput.testId = data.testId;
        }
        return Task.FromResult(new ScriptResponse { Data = subInput });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic merged = new ExpandoObject();
        var target = (IDictionary<string, object>)merged;
        target["__FLAG__"] = true;
        return Task.FromResult(new ScriptResponse { Data = merged });
    }
}
'''


def write_src(name, code):
    os.makedirs(SRC, exist_ok=True)
    with open(os.path.join(SRC, name), "w") as f:
        f.write(code)
    return {"location": f"./src/{name}", "code": base64.b64encode(code.encode()).decode()}


def labels(text):
    return [{"language": "en-US", "label": text}]


def ref(level):
    return {"key": f"{PREFIX}-l{level}", "domain": "core", "version": VERSION, "flow": "sys-flows"}


def state(key, state_type, sub_type, label, transitions, sub_flow=None):
    return {
        "key": key,
        "stateType": state_type,
        "subType": sub_type,
        "versionStrategy": "Major",
        "labels": labels(label),
        "view": None,
        "subFlow": sub_flow,
        "onEntries": [],
        "onExits": [],
        "transitions": transitions,
    }


def transition(key, target, trigger, label, rule=None):
    t = {
        "key": key,
        "target": target,
        "triggerType": trigger,
        "versionStrategy": "Minor",
        "labels": labels(label),
        "onExecutionTasks": [],
    }
    if rule is not None:
        t["rule"] = rule
    return t


def build_level(level, rule):
    k = f"l{level}"
    is_leaf = level == DEPTH
    states = [
        state(f"{k}-initial", 1, 0, f"L{level} Initial",
              [transition(f"auto-{k}-to-waiting", f"{k}-waiting", 1, "Auto to Waiting", rule)]),
    ]
    if is_leaf:
        states.append(state(f"{k}-waiting", 2, 0, "Leaf Waiting", [
            transition("leaf-ping", f"{k}-waiting", 0, "Leaf Ping (self-loop)"),
            transition("leaf-finish", f"{k}-done", 0, "Leaf Finish"),
        ]))
    else:
        cls = f"L{level}ToL{level + 1}SubFlowMapping"
        mapping = write_src(
            f"{cls}.csx",
            SUBFLOW_MAPPING.replace("__CLASS__", cls)
            .replace("__FROM__", str(level)).replace("__TO__", str(level + 1))
            .replace("__FLAG__", f"l{level + 1}Completed"))
        states.append(state(
            f"{k}-waiting", 4, 0, f"L{level} Waiting On L{level + 1}",
            [transition(f"auto-{k}-to-done", f"{k}-done", 1, "Auto to Done", rule)],
            sub_flow={"type": "S", "process": ref(level + 1), "mapping": mapping}))
    states.append(state(f"{k}-done", 3, 1, f"L{level} Done", []))

    return {
        "key": f"{PREFIX}-{k}",
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": VERSION,
        "tags": ["integration-test", "subflow", PREFIX],
        "attributes": {
            "type": "F" if level == 1 else "S",
            "timeout": None,
            "labels": labels(f"Subflow Depth Lab L{level}"),
            "functions": [],
            "features": [],
            "extensions": [],
            "startTransition": {
                "key": f"start-{PREFIX}-{k}",
                "target": f"{k}-initial",
                "triggerType": 0,
                "versionStrategy": "Major",
                "labels": labels(f"Start L{level}"),
            },
            "states": states,
        },
    }


def main():
    rule = write_src("AlwaysTrueRule.csx", ALWAYS_TRUE)
    for level in range(1, DEPTH + 1):
        wf = build_level(level, rule)
        path = os.path.join(ROOT, f"{wf['key']}.json")
        with open(path, "w") as f:
            json.dump(wf, f, indent=2, ensure_ascii=False)
            f.write("\n")
        print(f"wrote {os.path.relpath(path)}")


if __name__ == "__main__":
    main()
