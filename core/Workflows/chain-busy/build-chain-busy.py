#!/usr/bin/env python3
"""
chain-busy A -> B -> C akisini uretir (csx -> base64 gomulu workflow JSON'lari).

    python3 core/Workflows/chain-busy/build-chain-busy.py

Uretilenler:
  chain-busy-root.json    (A, type F)  root  -> middle
  chain-busy-middle.json  (B, type S)  middle-> leaf
  chain-busy-leaf.json    (C, type S)  leaf  (yaprak, girdi bekler)
  src/*.csx               sayac / rule / subflow / timer mapping'leri

Akis, su davranislari gozlemlenebilir kilmak icin kurulmustur:

  * Zincir tamamen auto transition ile kurulur; A baslatildiginda C `leaf-waiting`de
    Active bekler, A ve B acik korelasyon boyunca Busy olur.
  * Her state'in onEntry / onExit'i bir sayac task'i calistirir -> bir transition'in
    state yasam dongusune girip girmedigi veriden okunabilir.
  * `leaf-waiting`de uzun (30 dk) bir scheduled transition kurulur; hic atesleme
    yapmaz, amaci ARMED bir InstanceJob birakmaktir: ExecuteAt degeri, bir transition'in
    zamanlayiciyi yeniden kurup kurmadigini kanitlar.

  DIKKAT — yasam dongusu atlamasi YALNIZCA `updateData` icin gecerlidir. `updateData`
  onEntry/onExit'e girmez ve zamanlayiciyi yeniden kurmaz; `$self` hedefli bir SHARED
  transition ise TAM dongu kosar (onExit + onEntry girer, zamanlayiciyi yeniden kurar).
  Akis bu iki zit davranisi ayni kosuda yan yana gozlemlemek icin kurulmustur.
  * sharedTransition'lar hem parent'ta hem alt akislarda tanimlidir:
      - `root-shared-mark`  yalniz A'da  -> A karsilar, asagi forward EDILMEZ
      - `leaf-only-mark`    yalniz C'de  -> A'dan tetiklenir, C'ye forward EDILIR
  * Her uc akista cancel tanimlidir -> yukaridan asagi kaskad, asagidan yukari
    tamamlanma bildirimi.
"""

import base64
import json
import os

ROOT = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(ROOT, "src")
VERSION = "1.3.0"

# Hook basina AYRI task tanimi. Journal (TransitionId, TaskId) uzerinden tekil oldugu icin ayni
# transition'da ayni tanimi iki kez kullanmak ikincisini sessizce atlatir — bkz. `task()`.
SCRIPT_TASK = {"key": "subflow-script-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}
ENTRY_TASK = {"key": "chain-entry-script-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}
EXIT_TASK = {"key": "chain-exit-script-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}

# ─────────────────────────────────────────────────────────────────────────────
# .csx sablonlari
# ─────────────────────────────────────────────────────────────────────────────

COUNTER_TEMPLATE = '''using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// __DESC__
/// <para>
/// DELTA-ONLY: yalnizca sahibi oldugu sayaci dondurur. Full-echo yapsaydi, eszamanli
/// yazicilarin taze degerlerini bayat snapshot degeriyle ezerdi; merge zaten head'i korur.
/// </para>
/// </summary>
public class __CLASS__ : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var inst = context.Instance.Data as IDictionary<string, object>;

        var current = 0;
        if (inst != null && inst.TryGetValue("__COUNTER__", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out current);
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        target["__COUNTER__"] = current + 1;

        LogInformation($"__CLASS__: __COUNTER__ {current} -> {current + 1}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
'''

ALWAYS_TRUE_RULE = '''using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class AlwaysTrueRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        return Task.FromResult(true);
    }
}
'''

SUBFLOW_MAPPING_TEMPLATE = '''using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// __DESC__
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
        LogInformation("__CLASS__: prepared sub input");
        return Task.FromResult(new ScriptResponse { Data = subInput });
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

        target["__FLAG__"] = true;
        LogInformation("__CLASS__: merged sub output");
        return Task.FromResult(new ScriptResponse { Data = merged });
    }
}
'''

LEAF_TIMER = '''using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;

// leaf-waiting'e uzun bir zamanlayici kurar. Test suresince ASLA atesleme yapmaz —
// gorevi ARMED bir InstanceJob birakmaktir: ExecuteAt degeri sabit kaliyorsa, arada
// calisan `$self` transition zamanlayiciyi yeniden kurmamis demektir.
public class LeafExpireTimer : ITimerMapping
{
    public Task<TimerSchedule> Handler(ScriptContext context)
    {
        return Task.FromResult(TimerSchedule.FromDuration(TimeSpan.FromMinutes(30)));
    }
}
'''

COUNTERS = [
    ("RootInitialEntryMapping", "rootInitialEntries",
     "root-initial onEntry sayaci — start transition'in baslangic state'ine GIRIP girmedigini olcer."),
    ("LeafInitialEntryMapping", "leafInitialEntries",
     "leaf-initial onEntry sayaci — subflow start'in baslangic state'ine GIRIP girmedigini olcer."),
    ("RootEntryMapping", "rootEntries", "root-waiting onEntry sayaci — updateData artirmamali, $self shared transition ARTIRMALI."),
    ("RootExitMapping", "rootExits", "root-waiting onExit sayaci — updateData artirmamali, $self shared transition ARTIRMALI."),
    ("RootSharedMarkMapping", "rootSharedMarks", "A'nin KENDI shared transition'i — asagi forward edilmeden A'da calismali."),
    ("RootUpdateDataMapping", "rootUpdates", "A'nin updateData sayaci (aktif subflow'da data-only calisir)."),
    ("MiddleEntryMapping", "middleEntries", "middle-waiting onEntry sayaci."),
    ("MiddleExitMapping", "middleExits", "middle-waiting onExit sayaci."),
    ("MiddleSharedMarkMapping", "middleSharedMarks", "B'nin kendi shared transition'i."),
    ("LeafEntryMapping", "leafEntries", "leaf-waiting onEntry sayaci — updateData sonrasi SABIT, $self shared sonrasi ARTAR."),
    ("LeafExitMapping", "leafExits", "leaf-waiting onExit sayaci — updateData sonrasi SABIT, $self shared sonrasi ARTAR."),
    ("LeafOnlySharedMapping", "leafOnlyMarks", "YALNIZ C'de tanimli shared transition — A'dan tetiklenip C'ye forward edilmeli."),
    ("LeafUpdateDataMapping", "leafUpdates", "C'nin updateData sayaci — yasam dongusu atlanarak calisir."),
    ("LeafFinishMapping", "leafFinishMarks", "finish-leaf onExecute sayaci."),
]

SUBFLOWS = [
    ("RootToMiddleSubFlowMapping", "middleCompleted", "A -> B subflow giris/cikis mapping'i."),
    ("MiddleToLeafSubFlowMapping", "leafCompleted", "B -> C subflow giris/cikis mapping'i."),
]


def write_sources():
    os.makedirs(SRC, exist_ok=True)
    written = []

    with open(os.path.join(SRC, "AlwaysTrueRule.csx"), "w") as fh:
        fh.write(ALWAYS_TRUE_RULE)
    written.append("AlwaysTrueRule.csx")

    with open(os.path.join(SRC, "LeafExpireTimer.csx"), "w") as fh:
        fh.write(LEAF_TIMER)
    written.append("LeafExpireTimer.csx")

    for cls, counter, desc in COUNTERS:
        body = (COUNTER_TEMPLATE
                .replace("__CLASS__", cls)
                .replace("__COUNTER__", counter)
                .replace("__DESC__", desc))
        with open(os.path.join(SRC, cls + ".csx"), "w") as fh:
            fh.write(body)
        written.append(cls + ".csx")

    for cls, flag, desc in SUBFLOWS:
        body = (SUBFLOW_MAPPING_TEMPLATE
                .replace("__CLASS__", cls)
                .replace("__FLAG__", flag)
                .replace("__DESC__", desc))
        with open(os.path.join(SRC, cls + ".csx"), "w") as fh:
            fh.write(body)
        written.append(cls + ".csx")

    return written


def code(name):
    with open(os.path.join(SRC, name), "rb") as fh:
        return base64.b64encode(fh.read()).decode()


def ref(name):
    return {"location": "./src/" + name, "code": code(name)}


def label(text):
    return [{"language": "en-US", "label": text}]


def task(mapping_file, order=1, task_def=None):
    """
    DIKKAT: task journal'i `(TransitionId, TaskId)` uzerinden tekilder — `TaskId` = task
    TANIMININ key'i, `order` anahtarin PARCASI DEGIL (`InstanceTask.CreateExecutionKey`,
    `TaskExecutionEngine`). `TaskCoordinator` da atlanacaklari task key'i uzerinden suzuyor
    (`GetCompletedTaskIdsAsync(transitionId)`).

    Sonuc: AYNI transition icinde ayni task tanimini iki kez kullanirsan, ilki basariyla
    yazildiktan sonra ikincisi "zaten tamamlandi" sayilip SESSIZCE ATLANIR. order degistirmek
    bunu COZMEZ.

    Bu yuzden hook basina AYRI task tanimi kullaniyoruz: onEntry -> chain-entry-script-task,
    onExit -> chain-exit-script-task, onExecute -> subflow-script-task. Boylece OnExit(X) ile
    OnEntry(X) ayni transition'da (`$self` shared transition) cakismadan kosar.
    """
    return {
        "order": order,
        "task": dict(task_def or SCRIPT_TASK),
        "mapping": ref(mapping_file),
    }


def entry_task(mapping_file, order=1):
    """onEntry hook'u — kendi task tanimi, bkz. `task()`."""
    return task(mapping_file, order, ENTRY_TASK)


def exit_task(mapping_file, order=1):
    """onExit hook'u — kendi task tanimi, bkz. `task()`."""
    return task(mapping_file, order, EXIT_TASK)


def state(key, state_type, sub_type, labels, transitions,
          subflow=None, on_entries=None, on_exits=None):
    return {
        "key": key,
        "stateType": state_type,
        "subType": sub_type,
        "versionStrategy": "Major",
        "labels": label(labels),
        "view": None,
        "subFlow": subflow,
        "onEntries": on_entries or [],
        "onExits": on_exits or [],
        "transitions": transitions,
    }


def auto(key, target, labels):
    return {
        "key": key, "target": target, "triggerType": 1, "versionStrategy": "Minor",
        "labels": label(labels), "rule": ref("AlwaysTrueRule.csx"), "onExecutionTasks": [],
    }


def manual(key, target, labels, tasks=None):
    return {
        "key": key, "target": target, "triggerType": 0, "versionStrategy": "Minor",
        "labels": label(labels), "onExecutionTasks": tasks or [],
    }


def scheduled(key, target, labels, timer_file):
    return {
        "key": key, "target": target, "triggerType": 2, "versionStrategy": "Minor",
        "labels": label(labels),
        "timer": {"type": "L", "location": "./src/" + timer_file, "code": code(timer_file)},
        "onExecutionTasks": [],
    }


def shared(key, labels, available_in, mapping_file):
    return {
        "key": key, "target": "$self", "triggerType": 0, "versionStrategy": "Minor",
        "labels": label(labels), "availableIn": available_in,
        "onExecutionTasks": [task(mapping_file)],
    }


def subflow(child_key, mapping_file):
    return {
        "type": "S",
        "process": {"key": child_key, "domain": "core", "version": VERSION, "flow": "sys-flows"},
        "mapping": ref(mapping_file),
    }


def envelope(key, flow_type, labels, states, shared_transitions,
             cancel_target, start_target, update_data=None):
    attributes = {
        "type": flow_type,
        "timeout": None,
        "labels": label(labels),
        "functions": [],
        "features": [],
        "extensions": [],
        "sharedTransitions": shared_transitions,
        "cancel": {
            "key": "cancel-" + key,
            "target": cancel_target,
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Cancel " + labels),
        },
        "startTransition": {
            "key": "start-" + key,
            "target": start_target,
            "triggerType": 0,
            "versionStrategy": "Major",
            "labels": label("Start " + labels),
        },
        "states": states,
    }
    if update_data is not None:
        attributes["updateData"] = update_data

    return {
        "key": key,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": VERSION,
        "tags": ["integration-test", "chain-busy", "subflow"],
        "attributes": attributes,
    }


def build():
    # ── A: root ──────────────────────────────────────────────────────────────
    root = envelope(
        "chain-busy-root", "F", "Chain Busy Root",
        [
            state("root-initial", 1, 0, "Root Initial",
                  [auto("auto-root-to-waiting", "root-waiting", "Auto to Root Waiting")],
                  on_entries=[entry_task("RootInitialEntryMapping.csx")]),
            state("root-waiting", 4, 0, "Root Waiting On Middle",
                  [auto("auto-root-to-done", "root-done", "Auto to Root Done")],
                  subflow=subflow("chain-busy-middle", "RootToMiddleSubFlowMapping.csx"),
                  on_entries=[entry_task("RootEntryMapping.csx")],
                  on_exits=[exit_task("RootExitMapping.csx")]),
            state("root-done", 3, 1, "Root Done", []),
            state("root-cancelled", 3, 3, "Root Cancelled", []),
        ],
        # A'nin KENDI shared transition'i: aktif subflow varken bile A karsilar,
        # ForwardToActiveSubflowStep.IsParentSharedTransition true doner ve forward edilmez.
        [shared("root-shared-mark", "Root Shared Mark ($self)",
                ["root-waiting"], "RootSharedMarkMapping.csx")],
        cancel_target="root-cancelled",
        start_target="root-initial",
        update_data=manual("update-root-data", "$self", "Update Root Data",
                           [task("RootUpdateDataMapping.csx")]),
    )

    # ── B: middle ────────────────────────────────────────────────────────────
    middle = envelope(
        "chain-busy-middle", "S", "Chain Busy Middle",
        [
            state("middle-initial", 1, 0, "Middle Initial",
                  [auto("auto-middle-to-waiting", "middle-waiting", "Auto to Middle Waiting")]),
            state("middle-waiting", 4, 0, "Middle Waiting On Leaf",
                  [auto("auto-middle-to-done", "middle-done", "Auto to Middle Done")],
                  subflow=subflow("chain-busy-leaf", "MiddleToLeafSubFlowMapping.csx"),
                  on_entries=[entry_task("MiddleEntryMapping.csx")],
                  on_exits=[exit_task("MiddleExitMapping.csx")]),
            state("middle-done", 3, 1, "Middle Done", []),
            state("middle-cancelled", 3, 3, "Middle Cancelled", []),
        ],
        [shared("middle-shared-mark", "Middle Shared Mark ($self)",
                ["middle-waiting"], "MiddleSharedMarkMapping.csx")],
        cancel_target="middle-cancelled",
        start_target="middle-initial",
    )

    # ── C: leaf ──────────────────────────────────────────────────────────────
    leaf = envelope(
        "chain-busy-leaf", "S", "Chain Busy Leaf",
        [
            # leaf-waiting'e bir auto transition ile GIRILIR. Baslangic state'i yapilsaydi
            # instance oraya olusturma aninda pre-position edilirdi ve onEntry sayaci 0'da
            # kalirdi — o zaman "$self onEntry'yi tekrar calistirmadi" assertion'i 0 -> 0
            # olur, yani hicbir sey kanitlamazdi.
            state("leaf-initial", 1, 0, "Leaf Initial",
                  [auto("auto-leaf-to-waiting", "leaf-waiting", "Auto to Leaf Waiting")],
                  on_entries=[entry_task("LeafInitialEntryMapping.csx")]),
            state("leaf-waiting", 2, 0, "Leaf Waiting For Input",
                  [
                      manual("finish-leaf", "leaf-done", "Finish Leaf",
                             [task("LeafFinishMapping.csx")]),
                      # 30 dk — test suresince atesleme yapmaz, yalnizca ARMED job birakir.
                      scheduled("leaf-expire", "leaf-expired", "Leaf Expire (30m, never fires)",
                                "LeafExpireTimer.csx"),
                  ],
                  on_entries=[entry_task("LeafEntryMapping.csx")],
                  on_exits=[exit_task("LeafExitMapping.csx")]),
            state("leaf-done", 3, 1, "Leaf Done", []),
            state("leaf-expired", 3, 8, "Leaf Expired", []),
            state("leaf-cancelled", 3, 3, "Leaf Cancelled", []),
        ],
        # YALNIZ C'de tanimli: A'ya gonderildiginde A'nin FindSharedTransition'i null doner,
        # istek zincirden asagi C'ye forward edilir.
        [shared("leaf-only-mark", "Leaf Only Mark ($self)",
                ["leaf-waiting"], "LeafOnlySharedMapping.csx")],
        cancel_target="leaf-cancelled",
        start_target="leaf-initial",
        update_data=manual("update-leaf-data", "$self", "Update Leaf Data",
                           [task("LeafUpdateDataMapping.csx")]),
    )

    for name, doc in (("root", root), ("middle", middle), ("leaf", leaf)):
        path = os.path.join(ROOT, "chain-busy-%s.json" % name)
        with open(path, "w") as fh:
            json.dump(doc, fh, indent=2)
            fh.write("\n")
        print("wrote", os.path.relpath(path))


if __name__ == "__main__":
    written = write_sources()
    print("wrote %d csx sources" % len(written))
    build()
    print("\nversion: %s — publish leaf-first, then re-initialize." % VERSION)
