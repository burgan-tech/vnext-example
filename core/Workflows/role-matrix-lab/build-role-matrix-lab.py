#!/usr/bin/env python3
"""
role-matrix-lab akisini uretir (csx -> base64 gomulu workflow + function JSON).

    python3 core/Workflows/role-matrix-lab/build-role-matrix-lab.py

Uretilenler:
  core/Workflows/role-matrix-lab/role-matrix-lab.json
  core/Workflows/role-matrix-lab/src/*.csx            (sayac mapping'leri; elle yazilanlar korunur)
  core/Functions/role-matrix-lab/role-matrix-summary.json
  core/Workflows/role-matrix-lab/role-matrix-lab-combinators.json   (2026-10-03, combinator'lar)

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

Combinator'lar (2026-10-03, vnext `feature/role-grant-combinators`) AYRI bir akista,
`role-matrix-lab-combinators`: ana akisin 113 testi transition/alan listelerini birebir okudugu
icin ona dokunulmadi. Bir grant `role` XOR `allOf` XOR `anyOf` tasir; cocuklar yalniz `{role}`.
  * `checking.approve` dort goz: allow maker, deny allOf[maker, $PreviousUser]
  * `draft.queryRoles`: allow allOf[customer, $InstanceStarter]
  * master sema `role-matrix-combinator-master`: iban anyOf[$InstanceStarter, $InstanceBehalfOfStarter]
    + allow corporate-ops (maskeli, corporate-ops muaf); riskNote allow corporate-ops +
    deny allOf[corporate-ops, $InstanceBehalfOfStarter]
Kurulu @burgan-tech/vnext-schema allOf/anyOf'u bilmiyor: `npm run validate` bu iki dosyada
duser (README "Bilinen kisitlar"); SDK dogrudan publish eder.

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
# Function ayri surumlenir: 1.0.1 (2026-10-05) RoleMatrixSummaryMapping'in CS8197 derleme hatasini
# duzeltti (dynamic Headers uzerinde `out var`); 1.0.0 her cagrida 500 donuyordu.
FUNCTION_VERSION = "1.0.1"
# Workflow ayri surumlenir: field-masking lab (2026-09-28) master semayi ve SeedCaseMapping'i degistirdi;
# publish surum-degismezdir (ayni surum farkli icerik -> 409 Instance:100002), function degismedi.
# 1.0.9 (2026-10-05): yalniz SUMMARY_FUNCTION referansi 1.0.1'e cekildi.
WORKFLOW_VERSION = "1.0.9"

ENTRY_TASK = {"key": "role-matrix-entry-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}
EXEC_TASK = {"key": "role-matrix-exec-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}
SUMMARY_TASK = {"key": "role-matrix-summary-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}
SELF_READ_TASK = {"key": "role-matrix-self-read-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}

MASTER_SCHEMA = {"key": "role-matrix-master", "domain": "core", "version": "1.0.3", "flow": "sys-schemas"}
DECISION_SCHEMA = {"key": "role-matrix-decision", "domain": "core", "version": "1.0.0", "flow": "sys-schemas"}
REVIEW_VIEW = {"key": "role-matrix-review-view", "domain": "core", "version": "1.0.0", "flow": "sys-views"}
SUMMARY_FUNCTION = {"key": "role-matrix-summary", "domain": "core", "version": FUNCTION_VERSION, "flow": "sys-functions"}

# ── roller ───────────────────────────────────────────────────────────────────
MAKER = "morph-idm.maker"
APPROVER = "morph-idm.approver"
AUDITOR = "morph-idm.auditor"
VIEWER = "morph-idm.viewer"
# combinator akisi
CUSTOMER = "morph-idm.customer"
CORPORATE_OPS = "morph-idm.corporate-ops"

COMBINATOR_WORKFLOW_VERSION = "1.0.0"
COMBINATOR_SCHEMA = {"key": "role-matrix-combinator-master", "domain": "core", "version": "1.0.0", "flow": "sys-schemas"}

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
        # field-masking lab: bir GetInstanceData (type 13) task'i BU instance'i okur ve korumali alanlari
        # korumasiz alanlara kopyalar. Trigger task okumasi task'in KENDI basliklariyla degerlendirilir
        # (transition'i cagiranin kimligiyle degil); bu mapping baslik vermez, okuma rolsuzdur.
        # Grant yok: cagiran her rolle calistirabilir.
        {
            "key": "mirror-self",
            "target": "$self",
            "triggerType": 0,
            "versionStrategy": "Minor",
            "labels": label("Mirror Protected Fields ($self)"),
            "availableIn": ["intake"],
            "onExecutionTasks": [task("SelfReadMapping.csx", SELF_READ_TASK)],
        },
        # Ayni okuma, ama task'in KENDI basliklarinda auditor rolu var (input binding): kimligi gelistirici verir.
        {
            "key": "mirror-self-auditor",
            "target": "$self",
            "triggerType": 0,
            "versionStrategy": "Minor",
            "labels": label("Mirror Protected Fields as Auditor ($self)"),
            "availableIn": ["intake"],
            "onExecutionTasks": [task("SelfReadAuditorMapping.csx", SELF_READ_TASK)],
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
        "version": WORKFLOW_VERSION,
        "tags": ["integration-test", "role-matrix-lab", "authorization", "roles", "query-roles"],
        "attributes": attributes,
    }


def build_combinator_workflow():
    """
    role-matrix-lab-combinators — allOf / anyOf grant'leri, uc yuzeyde:

      draft     (initial)  queryRoles: allow allOf[customer, $InstanceStarter]
        |  submit           [maker ALLOW]   (bunu yapan, approve icin $PreviousUser olur)
        v
      checking  (human)
        |  approve          [maker ALLOW, deny allOf[maker, $PreviousUser]]   <- dort goz
        v
      approved

    Start transition'inda task yok: start govdesi (caseRef, customerId, iban, riskNote) instance
    datasi olur; alan gorunurlugu master semadan (`role-matrix-combinator-master`) gelir.
    """
    states = [
        state(
            "draft", 1, 0, "Draft",
            [manual("submit", "checking", "Submit", roles=grants((MAKER, "allow")))],
            query_roles=[
                {"grant": "allow", "allOf": [{"role": CUSTOMER}, {"role": "$InstanceStarter"}]},
            ],
        ),
        state(
            "checking", 2, 6, "Checking",
            [
                manual("approve", "approved", "Approve",
                       roles=[
                           {"role": MAKER, "grant": "allow"},
                           {"grant": "deny", "allOf": [{"role": MAKER}, {"role": "$PreviousUser"}]},
                       ]),
            ],
        ),
        state("approved", 3, 1, "Approved", []),
        state("cancelled", 3, 7, "Cancelled", []),
    ]

    attributes = {
        "type": "F",
        "timeout": None,
        "labels": label("Role Matrix Lab (Combinators)"),
        "functions": [],
        "features": [],
        "extensions": [],
        "schema": dict(COMBINATOR_SCHEMA),
        # Root queryRoles YOK: kural yalniz `draft`ta, combinator'la.
        "sharedTransitions": [],
        "cancel": manual("cancel-role-matrix-combinators", "cancelled", "Cancel",
                         roles=grants((MAKER, "allow")), available_in=["draft", "checking"]),
        "startTransition": manual("start-role-matrix-combinators", "draft", "Start Role Matrix Combinators"),
        "states": states,
    }

    return {
        "key": "role-matrix-lab-combinators",
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": COMBINATOR_WORKFLOW_VERSION,
        "tags": ["integration-test", "role-matrix-lab", "authorization", "roles", "combinators"],
        "attributes": attributes,
    }


def build_function():
    mapping_path = os.path.join(FUNCTION_DIR, "src", "RoleMatrixSummaryMapping.csx")
    return {
        "key": "role-matrix-summary",
        "version": FUNCTION_VERSION,
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

    combinator_path = os.path.join(ROOT, "role-matrix-lab-combinators.json")
    with open(combinator_path, "w") as fh:
        json.dump(build_combinator_workflow(), fh, indent=2, ensure_ascii=False)
        fh.write("\n")

    function_path = os.path.join(FUNCTION_DIR, "role-matrix-summary.json")
    with open(function_path, "w") as fh:
        json.dump(build_function(), fh, indent=2, ensure_ascii=False)
        fh.write("\n")

    print("uretildi:")
    print("  " + os.path.relpath(workflow_path, REPO))
    print("  " + os.path.relpath(function_path, REPO))
    print("  " + os.path.relpath(combinator_path, REPO))
    for name in counters:
        print("  core/Workflows/role-matrix-lab/src/" + name)


if __name__ == "__main__":
    main()
