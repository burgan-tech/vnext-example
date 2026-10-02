#!/usr/bin/env python3
"""Generates the display-labels-lab components (flows, view, schemas).

Run from the repo root: python3 core/Workflows/display-labels-lab/build-display-labels-lab.py
Every component carries tr-TR + en-US labels on purpose: the scenario asserts that the state,
view, schema, master and catalog functions hand them to the client in the definition's own
[{ language, label }] form. Bump VERSION on any change — publish is version-immutable.
"""
import base64
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[3]
LAB = "display-labels-lab"
VERSION = "1.0.0"
TAGS = ["integration-test", LAB, "labels"]


def labels(tr, en):
    return [{"language": "tr-TR", "label": tr}, {"language": "en-US", "label": en}]


def ref(key, flow, version=VERSION):
    return {"key": key, "domain": "core", "version": version, "flow": flow}


def state(key, state_type, sub_type, tr=None, en=None, **extra):
    s = {
        "key": key, "stateType": state_type, "subType": sub_type, "versionStrategy": "Major",
        "labels": labels(tr, en) if tr else [],
        "view": None, "subFlow": None, "onEntries": [], "onExits": [], "transitions": [],
    }
    s.update(extra)
    return s


def transition(key, target, tr=None, en=None, **extra):
    t = {"key": key, "target": target, "triggerType": 0, "versionStrategy": "Minor",
         "labels": labels(tr, en) if tr else []}
    t.update(extra)
    return t


def component(key, flow, attributes):
    return {"key": key, "version": VERSION, "domain": "core", "flow": flow,
            "flowVersion": "1.0.0", "tags": TAGS, "attributes": attributes}


def write(path, doc):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


mapping_src = (ROOT / "core/Workflows" / LAB / "src/DisplayLabelsLabSubFlowMapping.csx").read_text(encoding="utf-8")
mapping = {"location": "./src/DisplayLabelsLabSubFlowMapping.csx",
           "code": base64.b64encode(mapping_src.encode("utf-8")).decode("ascii")}

child = component(f"{LAB}-child", "sys-flows", {
    "_comment": "Child of display-labels-lab. Its review state is Human with labels, so a parent polled during the subflow window must describe THIS state (stateSubType human, the child's stateLabels) and pass the child's own transition entries — labels and target — through unchanged.",
    "type": "S",
    "labels": labels("Etiket Lab — alt akış", "Display Labels Lab — child"),
    "functions": [], "features": [], "extensions": [],
    "startTransition": transition("start-dl-child", "child-review", "Alt akışı başlat", "Start child"),
    "states": [
        state("child-review", 1, 6, "Alt İnceleme", "Child Review",
              transitions=[transition("child-done", "child-finished", "Alt işi bitir", "Finish child work")]),
        state("child-finished", 3, 1, "Alt Akış Tamamlandı", "Child Finished"),
    ],
})

root = component(LAB, "sys-flows", {
    "_comment": "Every surface a client renders carries display text: the current state (dl-review, Human), each transition (labelled state transition with a schema, transition into a subFlow state, labelled $self shared transition), each target state, the workflow timeout's target, the state view, the transition schema, the master schema and the declared function (til-cached-echo, already labelled). The timeout is PT10M so it never fires during a run — only its block is asserted.",
    "type": "F",
    "timeout": {"key": "dl-abandoned", "target": "dl-expired", "versionStrategy": "Minor",
                "timer": {"reset": "never", "duration": "PT10M"}},
    "labels": labels("Etiket Lab", "Display Labels Lab"),
    "schema": ref(f"{LAB}-master", "sys-schemas"),
    "functions": [ref("til-cached-echo", "sys-functions", "1.0.1")],
    "features": [], "extensions": [],
    "startTransition": transition("start-dl", "dl-review", "Başlat", "Start"),
    "sharedTransitions": [
        transition("dl-note", "$self", "Not ekle", "Add note", availableIn=["dl-review"]),
    ],
    "states": [
        state("dl-review", 1, 6, "İnceleme", "Review",
              view={"view": ref(f"{LAB}-review-view", "sys-views"), "loadData": False},
              transitions=[
                  transition("dl-approve", "dl-approved", "Onayla", "Approve",
                             schema=ref(f"{LAB}-decision", "sys-schemas")),
                  transition("dl-ask-sub", "dl-sub", "Alt akışa gönder", "Send to child flow"),
              ]),
        state("dl-sub", 4, 0, "Alt Akışta", "In Child Flow",
              subFlow={"type": "S", "process": ref(f"{LAB}-child", "sys-flows"), "mapping": mapping}),
        state("dl-approved", 3, 1, "Onaylandı", "Approved"),
        state("dl-expired", 3, 8, "Süresi Doldu", "Expired"),
    ],
})

view = component(f"{LAB}-review-view", "sys-views", {
    "type": 1, "display": "full-page", "renderer": "pseudo-ui",
    "labels": labels("İnceleme Ekranı", "Review Screen"),
    "content": {"$schema": "https://amorphie.io/meta/view-vocabulary/1.0",
                "view": {"type": "Text", "content": {"en": "review", "tr": "inceleme"},
                         "variant": "headlineMedium"}},
})


def schema(key, tr, en):
    return component(key, "sys-schemas", {
        "type": "schema", "labels": labels(tr, en),
        "schema": {"$schema": "https://json-schema.org/draft/2020-12/schema",
                   "$id": f"urn:vnext:res:schema:core:{key}", "type": "object",
                   "properties": {"comment": {"type": "string", "x-labels": {"tr": "Açıklama", "en": "Comment"}}}},
    })


write(ROOT / "core/Workflows" / LAB / f"{LAB}.json", root)
write(ROOT / "core/Workflows" / LAB / f"{LAB}-child.json", child)
write(ROOT / "core/Views" / LAB / f"{LAB}-review-view.json", view)
write(ROOT / "core/Schemas" / LAB / f"{LAB}-decision.json", schema(f"{LAB}-decision", "Onay Formu", "Approval Form"))
write(ROOT / "core/Schemas" / LAB / f"{LAB}-master.json", schema(f"{LAB}-master", "Etiket Lab Verisi", "Display Labels Lab Data"))
print("written")
