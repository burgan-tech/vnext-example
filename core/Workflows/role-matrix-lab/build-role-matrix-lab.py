#!/usr/bin/env python3
"""
role-matrix-lab akisini uretir (csx -> base64 gomulu workflow + function JSON).

    python3 core/Workflows/role-matrix-lab/build-role-matrix-lab.py

Uretilenler:
  core/Workflows/role-matrix-lab/role-matrix-lab.json
  core/Workflows/role-matrix-lab/src/*.csx            (sayac mapping'leri; elle yazilanlar korunur)
  core/Functions/role-matrix-lab/role-matrix-summary.json

Bu akis TEK BIR SEYI olcer: yetkilendirme yuzeylerinin birbiriyle tutarli olup olmadigini.
Davranissal bir pipeline testi degil — her state, her transition ve her alan, farkli bir
rol kombinasyonunu gorunur kilmak icin secilmistir:

  * ROOT queryRoles allowlist'tir  -> `viewer` hicbir yerde okuyamaz.
  * `review` state'inin KENDI queryRoles'u vardir ve root'u EZER; icinde `maker` DENY'dir.
    Yani `maker` akisi baslatabilir ama `review`e dustugunde artik state function'i okuyamaz.
    Root ALLOW + state DENY birlesimi, state'in kazandigini kanitlar.
  * `escalated` state'i yalnizca `auditor`a acilir -> `approver` bile 403 alir.
  * `record-note` shared transition'i AND daraltmasini olcer:
      transition.roles      = maker  ALLOW, approver ALLOW
      availableIn[review]   = approver ALLOW
    => `intake`te maker VE approver gorur; `review`de YALNIZ approver gorur.
  * `reject` deny-only bir sete sahiptir (blacklist): ALLOW grant yoktur, dolayisiyla
    ACIKCA reddedilmeyen herkes gorur. `approve` ise allowlist'tir. Iki zit semantik
    ayni state'te yan yana durur.
  * `escalate` predefined `$InstanceStarter` grant'i kullanir -> caller kimligine baglidir,
    role header'ina degil. morph-idm provider'i acildiginda bu grant'in hala calismasi,
    rol setinin nereden geldiginin predefined grant'leri etkilemedigini gosterir.
  * Master schema'da `decisionNote` (DENY tasiyan set) ve `auditTrail` (tek ALLOW'lu
    allowlist) x-roles ile korunur -> alan bazli budama data function'dan okunabilir.

DIKKAT — task journal'i `(TransitionId, TaskId)` uzerinden tekildir; ayni transition icinde
ayni task TANIMINI iki kez kullanirsan ikincisi sessizce atlanir. Bu yuzden hook basina ayri
task tanimi var: onEntry -> role-matrix-entry-task, onExecute -> role-matrix-exec-task.
"""

import base64
import json
import os

ROOT = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(ROOT, "src")
REPO = os.path.abspath(os.path.join(ROOT, "..", "..", ".."))
FUNCTION_DIR = os.path.join(REPO, "core", "Functions", "role-matrix-lab")

VERSION = "1.0.0"

ENTRY_TASK = {"key": "role-matrix-entry-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}
EXEC_TASK = {"key": "role-matrix-exec-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}
SUMMARY_TASK = {"key": "role-matrix-summary-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}

MASTER_SCHEMA = {"key": "role-matrix-master", "domain": "core", "version": "1.0.0", "flow": "sys-schemas"}
DECISION_SCHEMA = {"key": "role-matrix-decision", "domain": "core", "version": "1.0.0", "flow": "sys-schemas"}
REVIEW_VIEW = {"key": "role-matrix-review-view", "domain": "core", "version": "1.0.0", "flow": "sys-views"}
SUMMARY_FUNCTION = {"key": "role-matrix-summary", "domain": "core", "version": "1.0.0", "flow": "sys-functions"}

# ── roller ───────────────────────────────────────────────────────────────────
MAKER = "morph-idm.maker"
APPROVER = "morph-idm.approver"
AUDITOR = "morph-idm.auditor"
VIEWER = "morph-idm.viewer"

# ─────────────────────────────────────────────────────────────────────────────
# .csx sablonlari — sayaclar. Elle yazilan mapping'ler (SeedCaseMapping, DecisionMapping)
# bu sablondan URETILMEZ, oldugu gibi okunur.
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

COUNTERS = [
    ("IntakeEntryMapping", "intakeEntries", "intake state'inin onEntry sayaci."),
    ("ReviewEntryMapping", "reviewEntries", "review state'inin onEntry sayaci."),
    ("RecordNoteMapping", "noteMarks", "record-note ($self shared transition) sayaci."),
    ("UpdateDataMapping", "updateDataMarks", "updateData well-known transition sayaci."),
]


def write_counters():
    os.makedirs(SRC, exist_ok=True)
    written = []
    for cls, counter, desc in COUNTERS:
        body = (COUNTER_TEMPLATE
                .replace("__CLASS__", cls)
                .replace("__COUNTER__", counter)
                .replace("__DESC__", desc))
        with open(os.path.join(SRC, cls + ".csx"), "w") as fh:
            fh.write(body)
        written.append(cls + ".csx")
    return written


# ─────────────────────────────────────────────────────────────────────────────
# yardimcilar
# ─────────────────────────────────────────────────────────────────────────────

def code(path):
    with open(path, "rb") as fh:
        return base64.b64encode(fh.read()).decode()


def ref(name):
    return {"location": "./src/" + name, "code": code(os.path.join(SRC, name))}


def label(text):
    return [{"language": "en-US", "label": text}]


def grants(*pairs):
    """grants((MAKER, 'allow'), (VIEWER, 'deny')) -> roleGrant listesi."""
    return [{"role": role, "grant": grant} for role, grant in pairs]


def task(mapping_file, task_def, order=1):
    return {"order": order, "task": dict(task_def), "mapping": ref(mapping_file)}


def entry_task(mapping_file):
    return task(mapping_file, ENTRY_TASK)


def exec_task(mapping_file):
    return task(mapping_file, EXEC_TASK)


def manual(key, target, labels, roles=None, tasks=None, schema=None,
           view=None, available_in=None):
    transition = {
        "key": key,
        "target": target,
        "triggerType": 0,
        "versionStrategy": "Minor",
        "labels": label(labels),
        "onExecutionTasks": tasks or [],
    }
    if roles is not None:
        transition["roles"] = roles
    if schema is not None:
        transition["schema"] = dict(schema)
    if view is not None:
        transition["view"] = view
    if available_in is not None:
        transition["availableIn"] = available_in
    return transition


def state(key, state_type, sub_type, labels, transitions,
          query_roles=None, on_entries=None, view=None):
    node = {
        "key": key,
        "stateType": state_type,
        "subType": sub_type,
        "versionStrategy": "Major",
        "labels": label(labels),
        "view": view,
        "subFlow": None,
        "onEntries": on_entries or [],
        "onExits": [],
        "transitions": transitions,
    }
    if query_roles is not None:
        node["queryRoles"] = query_roles
    return node


def view_ref(component, load_data=True):
    """
    Tek view'li (kuralsiz) form. `loadData: true` bilincli: view function'i cevabin yaninda
    instance datasini da doner, boylece alan bazli x-roles budamasinin view yuzeyinde de
    uygulanip uygulanmadigi tek cagriyla gorulur.
    """
    return {"view": dict(component), "loadData": load_data}


# ─────────────────────────────────────────────────────────────────────────────
# akis
# ─────────────────────────────────────────────────────────────────────────────

def build_workflow():
    states = [
        # intake — KENDI queryRoles'u YOK, root'a duser. Root allowlist oldugu icin
        # maker/approver/auditor okur, viewer okuyamaz.
        state(
            "intake", 1, 0, "Intake",
            [
                manual("submit-for-review", "review", "Submit For Review",
                       roles=grants((MAKER, "allow"), (APPROVER, "allow"), (VIEWER, "deny"))),
            ],
            on_entries=[entry_task("IntakeEntryMapping.csx")],
        ),

        # review — state queryRoles ROOT'U EZER ve maker'i DENY eder.
        # Ayni state'te allowlist (approve), blacklist (reject), predefined ($InstanceStarter)
        # ve grant'siz (open-review-note) transition'lar yan yana durur.
        state(
            "review", 2, 6, "Review",
            [
                manual("approve", "approved", "Approve",
                       roles=grants((APPROVER, "allow")),
                       tasks=[exec_task("DecisionMapping.csx")],
                       schema=DECISION_SCHEMA),
                manual("reject", "rejected", "Reject",
                       roles=grants((AUDITOR, "deny")),
                       tasks=[exec_task("DecisionMapping.csx")],
                       schema=DECISION_SCHEMA),
                manual("escalate", "escalated", "Escalate",
                       roles=grants(("$InstanceStarter", "allow"))),
                manual("open-review-note", "$self", "Open Review Note"),
            ],
            query_roles=grants((APPROVER, "allow"), (AUDITOR, "allow"), (MAKER, "deny")),
            on_entries=[entry_task("ReviewEntryMapping.csx")],
            view=view_ref(REVIEW_VIEW),
        ),

        # escalated — tek ALLOW'lu allowlist: yalniz auditor okur, approver bile 403 alir.
        state(
            "escalated", 2, 4, "Escalated",
            [
                manual("resolve-escalation", "approved", "Resolve Escalation",
                       roles=grants((AUDITOR, "allow"))),
            ],
            query_roles=grants((AUDITOR, "allow")),
        ),

        state("approved", 3, 1, "Approved", []),
        state("rejected", 3, 2, "Rejected", []),
        state("cancelled", 3, 7, "Cancelled", []),
        state("exited", 3, 3, "Exited", []),
    ]

    shared_transitions = [
        # AND daraltmasi: transition maker+approver'a acik, ama `review`de availableIn
        # entry'si yalniz approver'a izin veriyor -> `review`de maker goremez.
        {
            "key": "record-note",
            "target": "$self",
            "triggerType": 0,
            "versionStrategy": "Minor",
            "labels": label("Record Note ($self)"),
            "roles": grants((MAKER, "allow"), (APPROVER, "allow")),
            "availableIn": [
                "intake",
                {"state": "review", "roles": grants((APPROVER, "allow"))},
            ],
            "onExecutionTasks": [exec_task("RecordNoteMapping.csx")],
        },
    ]

    attributes = {
        "type": "F",
        "timeout": None,
        "labels": label("Role Matrix Lab"),
        "functions": [dict(SUMMARY_FUNCTION)],
        "features": [],
        "extensions": [],
        "schema": dict(MASTER_SCHEMA),
        # Root allowlist. viewer hicbir ALLOW almadigi icin her state'te 403 alir —
        # state'i kendi queryRoles'uyla ezmedigi surece.
        "queryRoles": grants((MAKER, "allow"), (APPROVER, "allow"), (AUDITOR, "allow")),
        "sharedTransitions": shared_transitions,
        "cancel": manual("cancel-role-matrix", "cancelled", "Cancel Role Matrix Lab",
                         roles=grants((MAKER, "allow"), (APPROVER, "allow")),
                         available_in=["intake", "review"]),
        "updateData": manual("update-role-matrix-data", "$self", "Update Role Matrix Data",
                             roles=grants((MAKER, "allow")),
                             tasks=[exec_task("UpdateDataMapping.csx")]),
        "exit": manual("exit-role-matrix", "exited", "Exit Role Matrix Lab",
                       roles=grants((AUDITOR, "allow")),
                       available_in=[{"state": "review", "roles": grants((AUDITOR, "allow"))}]),
        "startTransition": manual("start-role-matrix", "intake", "Start Role Matrix Lab",
                                  tasks=[exec_task("SeedCaseMapping.csx")]),
        "states": states,
    }

    return {
        "key": "role-matrix-lab",
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": VERSION,
        "tags": ["integration-test", "role-matrix-lab", "authorization", "roles", "query-roles"],
        "attributes": attributes,
    }


def build_function():
    mapping_path = os.path.join(FUNCTION_DIR, "src", "RoleMatrixSummaryMapping.csx")
    return {
        "key": "role-matrix-summary",
        "version": VERSION,
        "domain": "core",
        "flow": "sys-functions",
        "flowVersion": "1.0.0",
        "tags": ["integration-test", "role-matrix-lab", "authorization", "function"],
        "attributes": {
            "scope": "I",
            "rawResponse": False,
            "labels": [
                {"language": "en-US", "label": "Role Matrix Summary"},
                {"language": "tr-TR", "label": "Rol Matrisi Ozeti"},
            ],
            # Bu grant seti ARTIK CALISTIRMAYI ENGELLEMEZ. Custom function cagrisinda
            # runtime rol denetimi yapmaz; `roles` yalnizca `authorize?functionKey=...`
            # tarafindan okunur. Fixture'in amaci tam olarak bu ayrimi gorunur kilmak:
            # ayni caller icin CALISTIRMA 200, AUTHORIZE 403 doner.
            "roles": grants((APPROVER, "allow"), (AUDITOR, "allow"), (VIEWER, "deny")),
            "task": {
                "order": 1,
                "task": dict(SUMMARY_TASK),
                "mapping": {
                    "location": "./src/RoleMatrixSummaryMapping.csx",
                    "code": code(mapping_path),
                },
            },
        },
    }


def main():
    counters = write_counters()

    workflow_path = os.path.join(ROOT, "role-matrix-lab.json")
    with open(workflow_path, "w") as fh:
        json.dump(build_workflow(), fh, indent=2, ensure_ascii=False)
        fh.write("\n")

    function_path = os.path.join(FUNCTION_DIR, "role-matrix-summary.json")
    with open(function_path, "w") as fh:
        json.dump(build_function(), fh, indent=2, ensure_ascii=False)
        fh.write("\n")

    print("uretildi:")
    print("  " + os.path.relpath(workflow_path, REPO))
    print("  " + os.path.relpath(function_path, REPO))
    for name in counters:
        print("  core/Workflows/role-matrix-lab/src/" + name)


if __name__ == "__main__":
    main()
