#!/usr/bin/env python3
"""Generates the view-load-data-lab components (two flows, one view).

Run from the repo root: python3 core/Workflows/view-load-data-lab/build-view-load-data-lab.py
The scenario asserts that the state function's transitions[].view.loadData is the loadData declared
on each transition's view binding (burgan-tech/vnext#1266). Bump VERSION on any change — publish is
version-immutable.
"""
import base64
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[3]
LAB = "view-load-data-lab"
VERSION = "1.0.0"
TAGS = ["integration-test", LAB, "views"]
SRC = ROOT / "core/Workflows" / LAB / "src"


def labels(tr, en):
    return [{"language": "tr-TR", "label": tr}, {"language": "en-US", "label": en}]


def ref(key, flow, version=VERSION):
    return {"key": key, "domain": "core", "version": version, "flow": flow}


def script(name):
    source = (SRC / name).read_text(encoding="utf-8")
    return {"location": f"./src/{name}", "code": base64.b64encode(source.encode("utf-8")).decode("ascii")}


def state(key, state_type, sub_type, tr, en, **extra):
    s = {
        "key": key, "stateType": state_type, "subType": sub_type, "versionStrategy": "Major",
        "labels": labels(tr, en),
        "view": None, "subFlow": None, "onEntries": [], "onExits": [], "transitions": [],
    }
    s.update(extra)
    return s


def transition(key, target, tr, en, **extra):
    t = {"key": key, "target": target, "triggerType": 0, "versionStrategy": "Minor",
         "labels": labels(tr, en)}
    t.update(extra)
    return t


def component(key, flow, attributes, version=VERSION):
    return {"key": key, "version": version, "domain": "core", "flow": flow,
            "flowVersion": "1.0.0", "tags": TAGS, "attributes": attributes}


def write(path, doc):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


FORM = ref(f"{LAB}-form-view", "sys-views")


def bound(load_data):
    """A single-entry view binding; None leaves loadData undeclared."""
    binding = {"view": FORM}
    if load_data is not None:
        binding["loadData"] = load_data
    return binding


child = component(f"{LAB}-child", "sys-flows", {
    "_comment": "Child of view-load-data-lab. Its own transitions declare loadData false and true; the parent polled during the subflow window passes these entries through, so they must arrive with the child's declarations.",
    "type": "S",
    "labels": labels("loadData Lab — alt akış", "loadData Lab — child"),
    "functions": [], "features": [], "extensions": [],
    "startTransition": transition("start-vld-child", "child-review", "Alt akışı başlat", "Start child"),
    "states": [
        state("child-review", 1, 6, "Alt İnceleme", "Child Review", transitions=[
            transition("child-load-false", "child-finished", "Boş form", "Empty form", view=bound(False)),
            transition("child-load-true", "child-finished", "Dolu form", "Filled form", view=bound(True)),
        ]),
        state("child-finished", 3, 1, "Alt Akış Tamamlandı", "Child Finished"),
    ],
})

root = component(LAB, "sys-flows", {
    "_comment": "Every transition of vld-review binds the same form view with a different loadData declaration: false, true, undeclared, no view at all, and two entries whose first says false. vld-shared-note (shared, loadData true) is available both in vld-review and in the subFlow state vld-sub, so it is listed as a parent-added entry while the child runs.",
    "type": "F",
    "labels": labels("loadData Lab", "loadData Lab"),
    "functions": [], "features": [], "extensions": [],
    "startTransition": transition("start-vld", "vld-review", "Başlat", "Start"),
    "sharedTransitions": [
        transition("vld-shared-note", "$self", "Not ekle", "Add note",
                   view=bound(True), availableIn=["vld-review", "vld-sub"]),
    ],
    "states": [
        state("vld-review", 1, 6, "İnceleme", "Review", transitions=[
            transition("vld-load-false", "vld-done", "Bilgi iste", "Request info", view=bound(False)),
            transition("vld-load-true", "vld-done", "Belge ekle", "Add document", view=bound(True)),
            transition("vld-load-undeclared", "vld-done", "Beyansız", "Undeclared", view=bound(None)),
            transition("vld-no-view", "vld-done", "Alan güncelle", "Update fields"),
            transition("vld-multi", "vld-done", "Çoklu", "Multi", view={"views": [
                {"rule": script("ViewLoadDataLabFirstEntryRule.csx"), "view": FORM, "loadData": False},
                {"view": FORM, "loadData": True},
            ]}),
            transition("vld-ask-sub", "vld-sub", "Alt akışa gönder", "Send to child flow"),
        ]),
        state("vld-sub", 4, 0, "Alt Akışta", "In Child Flow",
              subFlow={"type": "S", "process": ref(f"{LAB}-child", "sys-flows"),
                       "mapping": script("ViewLoadDataLabSubFlowMapping.csx")}),
        state("vld-done", 3, 1, "Bitti", "Done"),
    ],
})

view = component(f"{LAB}-form-view", "sys-views", {
    "type": 1, "display": "full-page", "renderer": "pseudo-ui",
    "labels": labels("Form", "Form"),
    "content": {"$schema": "https://amorphie.io/meta/view-vocabulary/1.0",
                "view": {"type": "Text", "content": {"en": "form", "tr": "form"},
                         "variant": "headlineMedium"}},
})

write(ROOT / "core/Workflows" / LAB / f"{LAB}.json", root)
write(ROOT / "core/Workflows" / LAB / f"{LAB}-child.json", child)
write(ROOT / "core/Views" / LAB / f"{LAB}-form-view.json", view)
print("written")
