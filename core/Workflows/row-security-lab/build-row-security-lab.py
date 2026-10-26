#!/usr/bin/env python3
"""
row-security-lab fixture'larini uretir (vnext#1107, DECISION-2026-10-23-row-security).

    python3 core/Workflows/row-security-lab/build-row-security-lab.py

Uretilenler:
  core/Schemas/row-security-lab/rs-lab-schema.json        (master; `protected` alani x-roles tasir)
  core/Schemas/row-security-lab/rs-lab-schema-plain.json  (master; ayni alanlar, x-roles YOK)
  core/Workflows/row-security-lab/rs-*.json               (hedef akislar + rs-reader)
  core/Tasks/row-security-lab/rs-*.json                   (GetInstances / GetInstance / DirectTrigger)
  partner/Schemas/row-security-lab/rs-xd-schema.json
  partner/Workflows/row-security-lab/rs-xd-target.json    (Definition A, partner domain'inde)

Hedef akislarin hepsi ayni sekildedir: start -> active ─rs-close→ closed, updateData `rs-update`
($self). Fark yalniz `attributes.rowSecurity` ve master schema'dadir:

  rs-a         Definition A: branch-staff → branchCode eq x-branch-code · hq-auditor (filtresiz)
  rs-a-xr      rs-a + x-roles'lu master schema (M-11: tek rol cozumu iki yuzeyde paylasilir)
  rs-b         Definition B: A + { roles: [$InstanceStarter] }
  rs-s         yalniz kimlik kurali ($InstanceStarter) + hq-auditor (seed okumasi icin)
  rs-behalf    $InstanceStarter (status eq A) + $InstanceBehalfOfStarter + hq-auditor
  rs-params    her parametre bicimi AYRI bir role baglidir (rs-p-*): bir cagiran tek kurala eslesir
               (1.0.1: rs-p-query-br `in` oldu, rs-p-createdby-starter eklendi — Task 11C P-10 / P-22)
  rs-plain     rowSecurity YOK (C-6 / N-1 kontrolu)
  rs-plain-xr  rowSecurity YOK, x-roles'lu schema (N-3)
  rs-versioned 1.0.0: hq-auditor + branch-staff → branchCode eq x-branch-code
               (1.0.1 = hq-auditor (filtresiz) + branch-staff → region eq x-region; dosyada degil,
               RowSecurityLabTestBase.PublishVersionedV101Async bu dosyadan uretip yayinlar)
  rs-reader    rowSecurity YOK; GetInstances / GetInstance / DirectTrigger task'larini govdeden surer

Her opt-in akista bir `hq-auditor` kurali vardir: K1'de `default` yok, deny varsayilan; seed okumasi
(SeedAsync 7 id'yi listeleyerek dogrular) hq-auditor ile yapilir.

Bilerek x-filterOperators: `ne` sorgu token'idir, sema adi `neq`'dir (SchemaFilterContext.ToSchemaOperator).
"""

import base64
import json
import os

ROOT = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(ROOT, "src")
REPO = os.path.abspath(os.path.join(ROOT, "..", "..", ".."))
CORE_SCHEMAS = os.path.join(REPO, "core", "Schemas", "row-security-lab")
CORE_TASKS = os.path.join(REPO, "core", "Tasks", "row-security-lab")
PARTNER_SCHEMAS = os.path.join(REPO, "partner", "Schemas", "row-security-lab")
PARTNER_WORKFLOWS = os.path.join(REPO, "partner", "Workflows", "row-security-lab")

VERSION = "1.0.0"
TAGS = ["integration-test", "row-security-lab"]

H = "$.context.Headers."
Q = "$.context.QueryParameters."
CU = "$.context.CurrentUser."


def label(text):
    return [{"language": "en-US", "label": text}]


def write(path, document):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(document, handle, indent=2, ensure_ascii=False)
        handle.write("\n")


def csx(name):
    with open(os.path.join(SRC, name), "rb") as handle:
        code = base64.b64encode(handle.read()).decode("ascii")
    return {"location": f"./src/{name}", "code": code}


# ── schemas ──────────────────────────────────────────────────────────────────

def schema(key, domain, with_x_roles, description):
    properties = {
        "testId": {"type": "string", "x-filterOperators": ["eq"]},
        # eq is served by the attributes GIN index; in/nin/ne on it log RS-W2a (no x-indexed) — on purpose.
        "branchCode": {"type": "string", "x-filterOperators": ["eq", "neq", "in", "nin", "isNull"]},
        "amount": {"type": "number", "x-indexed": True,
                   "x-filterOperators": ["eq", "gt", "gte", "lt", "lte", "between"]},
        "region": {"type": "string", "x-indexed": True, "x-filterOperators": ["eq", "in"]},
        "owner": {"type": "string", "x-filterOperators": ["eq"]},
        "positionCode": {"type": "string", "x-filterOperators": ["eq"]},
        "touchedBy": {"type": "string"},
        "protected": {"type": "string"},
    }
    if with_x_roles:
        properties["protected"]["x-roles"] = [{"role": "rs-protected-reader", "grant": "allow"}]
    return {
        "key": key,
        "version": VERSION,
        "domain": domain,
        "flow": "sys-schemas",
        "flowVersion": "1.0.0",
        "tags": TAGS + ["master"],
        "attributes": {
            "type": "master",
            "labels": label(description),
            "schema": {
                "$schema": "https://json-schema.org/draft/2020-12/schema",
                "$id": f"urn:vnext:res:schema:{domain}:{key}",
                "title": description,
                "type": "object",
                "required": ["testId"],
                "properties": properties,
            },
        },
    }


# ── row-security rule sets ───────────────────────────────────────────────────

HQ = {"roles": ["hq-auditor"]}
BRANCH = {"roles": ["branch-staff"], "filter": {"attributes": {"branchCode": {"eq": H + "x-branch-code"}}}}
DEF_A = [BRANCH, HQ]
DEF_B = DEF_A + [{"roles": ["$InstanceStarter"]}]


def param_rule(role, filter_):
    return {"roles": [role], "filter": filter_}


def attr(field, operator, value):
    return {"attributes": {field: {operator: value}}}


# One role per parameter shape: a caller holding rs-p-X matches exactly that rule, so "none" proves
# that no fallback widened anything (Task 5B / 11C P-rows).
PARAMS = [
    HQ,
    param_rule("rs-p-header-eq", attr("branchCode", "eq", H + "x-branch-code")),
    param_rule("rs-p-query-branch", attr("branchCode", "eq", Q + "branch")),
    # P-10: `in` over a multi-valued query key without split (element-wise), not `eq` (1.0.1).
    param_rule("rs-p-query-br", attr("branchCode", "in", {"$param": Q + "br"})),
    param_rule("rs-p-in-elements", attr("branchCode", "in", [H + "x-b1", H + "x-b2"])),
    param_rule("rs-p-in-literal", attr("branchCode", "in", [H + "x-b1", "BR-999"])),
    param_rule("rs-p-in-split", attr("branchCode", "in", {"$param": H + "x-branches", "split": ","})),
    param_rule("rs-p-in-param", attr("branchCode", "in", {"$param": H + "x-branches"})),
    param_rule("rs-p-nin-elements", attr("branchCode", "nin", [H + "x-excl-1", H + "x-excl-2"])),
    param_rule("rs-p-ne", attr("branchCode", "ne", H + "x-branch-code")),
    param_rule("rs-p-between", attr("amount", "between", [H + "x-min", H + "x-max"])),
    param_rule("rs-p-ge-number", attr("amount", "ge", H + "x-min")),
    param_rule("rs-p-not-in", {"not": attr("branchCode", "in", [H + "x-b1", H + "x-b2"])}),
    param_rule("rs-p-not-eq", {"not": attr("branchCode", "eq", H + "x-branch-code")}),
    param_rule("rs-p-and-or", {"and": [
        attr("branchCode", "eq", H + "x-branch-code"),
        {"or": [attr("region", "eq", H + "x-region"), attr("amount", "ge", H + "x-min")]},
    ]}),
    param_rule("rs-p-region", attr("region", "eq", H + "x-region")),
    param_rule("rs-p-position", attr("positionCode", "eq", CU + "Position")),
    param_rule("rs-p-actor", attr("owner", "eq", CU + "ActorUserName")),
    param_rule("rs-p-username", attr("owner", "eq", CU + "UserName")),
    # P-22 (1.0.1): the $InstanceStarter shorthand inside a STATIC-role rule (createdBy = ActorUserName).
    param_rule("rs-p-createdby-starter", {"createdBy": {"eq": "$InstanceStarter"}}),
]

# rs-params is published once per version (immutable); 1.0.1 fixed rs-p-query-br and added
# rs-p-createdby-starter for Task 11C.
PARAMS_VERSION = "1.0.1"

VERSIONED_100 = [HQ, BRANCH]


# ── workflows ────────────────────────────────────────────────────────────────

def target_workflow(key, domain, schema_key, row_security, description, version=VERSION):
    attributes = {
        "type": "F",
        "labels": label(description),
        "schema": {"key": schema_key, "domain": domain, "version": VERSION, "flow": "sys-schemas"},
        "functions": [],
        "features": [],
        "extensions": [],
    }
    if row_security is not None:
        attributes["rowSecurity"] = {"rules": row_security}
    attributes.update({
        "startTransition": {
            "key": "start", "target": "active", "triggerType": 0, "versionStrategy": "Major",
            "labels": label("Start"),
        },
        "updateData": {
            "key": "rs-update", "target": "$self", "triggerType": 0, "versionStrategy": "Minor",
            "labels": label("Update data"),
        },
        "states": [
            {
                "key": "active", "stateType": 1, "subType": 0, "versionStrategy": "Major",
                "labels": label("Active"), "view": None, "subFlow": None, "onEntries": [], "onExits": [],
                "transitions": [{
                    "key": "rs-close", "target": "closed", "triggerType": 0, "versionStrategy": "Minor",
                    "labels": label("Close"), "onExecutionTasks": [],
                }],
            },
            {
                "key": "closed", "stateType": 3, "subType": 1, "versionStrategy": "Major",
                "labels": label("Closed"), "view": None, "subFlow": None, "onEntries": [], "onExits": [],
                "transitions": [],
            },
        ],
    })
    return {
        "key": key,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": domain,
        "version": version,
        "tags": TAGS,
        "attributes": attributes,
    }


def task_ref(key):
    return {"key": key, "domain": "core", "version": VERSION, "flow": "sys-tasks"}


def reader_transition(key, task_key, mapping_file, text):
    return {
        "key": key, "target": "reader-ready", "triggerType": 0, "versionStrategy": "Minor",
        "labels": label(text),
        "onExecutionTasks": [{"order": 1, "task": task_ref(task_key), "mapping": csx(mapping_file)}],
    }


def reader_workflow():
    return {
        "key": "rs-reader",
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": VERSION,
        "tags": TAGS + ["get-instances", "get-instance", "direct-trigger"],
        "attributes": {
            "type": "F",
            "labels": label("Row Security Lab Reader"),
            "functions": [],
            "features": [],
            "extensions": [],
            "startTransition": {
                "key": "start", "target": "reader-ready", "triggerType": 0, "versionStrategy": "Major",
                "labels": label("Start"),
            },
            "states": [
                {
                    "key": "reader-ready", "stateType": 1, "subType": 0, "versionStrategy": "Major",
                    "labels": label("Ready"), "view": None, "subFlow": None, "onEntries": [], "onExits": [],
                    "transitions": [
                        reader_transition("read-list", "rs-get-instances", "RsReaderListMapping.csx",
                                          "GetInstances (body-driven, optional header mapping)"),
                        reader_transition("read-one", "rs-get-instance", "RsReaderGetMapping.csx",
                                          "GetInstance (body-driven, optional header mapping)"),
                        reader_transition("trigger-one", "rs-direct-trigger", "RsReaderTriggerMapping.csx",
                                          "DirectTrigger by id or key (body-driven, optional header mapping)"),
                        {
                            "key": "reader-close", "target": "reader-done", "triggerType": 0,
                            "versionStrategy": "Minor", "labels": label("Close"), "onExecutionTasks": [],
                        },
                    ],
                },
                {
                    "key": "reader-done", "stateType": 3, "subType": 1, "versionStrategy": "Major",
                    "labels": label("Done"), "view": None, "subFlow": None, "onEntries": [], "onExits": [],
                    "transitions": [],
                },
            ],
        },
    }


def task(key, task_type, config, extra_tags):
    return {
        "key": key,
        "version": VERSION,
        "domain": "core",
        "flow": "sys-tasks",
        "flowVersion": "1.0.0",
        "tags": TAGS + extra_tags,
        "attributes": {"type": task_type, "config": config},
    }


def main():
    write(os.path.join(CORE_SCHEMAS, "rs-lab-schema.json"),
          schema("rs-lab-schema", "core", True, "Row Security Lab master (x-roles on protected)"))
    write(os.path.join(CORE_SCHEMAS, "rs-lab-schema-plain.json"),
          schema("rs-lab-schema-plain", "core", False, "Row Security Lab master (no x-roles)"))
    write(os.path.join(PARTNER_SCHEMAS, "rs-xd-schema.json"),
          schema("rs-xd-schema", "partner", False, "Row Security Lab cross-domain master"))

    plain, xr = "rs-lab-schema-plain", "rs-lab-schema"
    flows = {
        "rs-a": (plain, DEF_A, "Row Security Lab A (Definition A)"),
        "rs-a-xr": (xr, DEF_A, "Row Security Lab A with x-roles schema"),
        "rs-b": (plain, DEF_B, "Row Security Lab B (Definition B)"),
        "rs-s": (plain, [HQ, {"roles": ["$InstanceStarter"]}], "Row Security Lab S (identity rule only)"),
        "rs-behalf": (plain, [HQ, {"roles": ["$InstanceStarter"], "filter": {"status": {"eq": "A"}}},
                              {"roles": ["$InstanceBehalfOfStarter"]}], "Row Security Lab behalf-of"),
        "rs-params": (plain, PARAMS, "Row Security Lab parameter shapes"),
        "rs-plain": (plain, None, "Row Security Lab plain (no rowSecurity)"),
        "rs-plain-xr": (xr, None, "Row Security Lab plain with x-roles schema"),
        "rs-versioned": (plain, VERSIONED_100, "Row Security Lab versioned (1.0.0 branch rule)"),
    }
    versions = {"rs-params": PARAMS_VERSION}
    for key, (schema_key, rules, description) in flows.items():
        write(os.path.join(ROOT, f"{key}.json"),
              target_workflow(key, "core", schema_key, rules, description, versions.get(key, VERSION)))

    write(os.path.join(ROOT, "rs-reader.json"), reader_workflow())
    write(os.path.join(PARTNER_WORKFLOWS, "rs-xd-target.json"),
          target_workflow("rs-xd-target", "partner", "rs-xd-schema", DEF_A,
                          "Row Security Lab cross-domain target (Definition A)"))

    write(os.path.join(CORE_TASKS, "rs-get-instances.json"),
          task("rs-get-instances", "15",
               {"domain": "core", "flow": "rs-a", "page": 1, "pageSize": 100, "timeoutSeconds": 30},
               ["get-instances"]))
    write(os.path.join(CORE_TASKS, "rs-get-instance.json"),
          task("rs-get-instance", "19", {"domain": "core", "flow": "rs-a", "timeoutSeconds": 30},
               ["get-instance"]))
    write(os.path.join(CORE_TASKS, "rs-direct-trigger.json"),
          task("rs-direct-trigger", "12",
               {"domain": "core", "flow": "rs-a", "transitionName": "rs-update", "sync": True,
                "timeoutSeconds": 30},
               ["direct-trigger"]))


if __name__ == "__main__":
    main()
