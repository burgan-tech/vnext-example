#!/usr/bin/env python3
"""Generates the script-plugin-lab components from their .csx sources.

Run from anywhere:  python3 core/Functions/script-plugin-lab/build-script-plugin-lab.py

Writes:
  core/Tasks/script-plugin-lab/spl-script.json            ScriptTask (type 7) both functions run
  core/Mappings/script-plugin-lab/spl-db-helper.json      sys-mappings helper wrapping the plugin
  core/Functions/script-plugin-lab/spl-direct-query.json  helper-less mapping → plugin
  core/Functions/script-plugin-lab/spl-helper-query.json  mapping → helper → plugin

Bump VERSION when anything changes: publish is version-immutable (409 Instance:100002).
"""
import base64
import json
import pathlib

VERSION = "1.0.0"
PLUGIN_ASSEMBLY = "ScriptPluginLab.Sql"

core = pathlib.Path(__file__).resolve().parents[2]
functions = core / "Functions" / "script-plugin-lab"
mappings = core / "Mappings" / "script-plugin-lab"
tasks = core / "Tasks" / "script-plugin-lab"


def ref(key, flow):
    return {"key": key, "domain": "core", "version": VERSION, "flow": flow}


def envelope(key, flow, tags, attributes):
    return {
        "key": key,
        "version": VERSION,
        "domain": "core",
        "flow": flow,
        "flowVersion": "1.0.0",
        "tags": ["integration-test", "script-plugin-lab", *tags],
        "attributes": attributes,
    }


def b64(path):
    return base64.b64encode(path.read_bytes()).decode("ascii")


def write(path, component):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(component, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print("wrote", path.relative_to(core.parent))


def function(key, label, source, helpers):
    scripts = {"allowedAssemblies": [PLUGIN_ASSEMBLY]}
    if helpers:
        scripts = {"helpers": [ref("spl-db-helper", "sys-mappings")], **scripts}
    return envelope(key, "sys-functions", ["function", "script-plugin"], {
        "scope": "D",
        "rawResponse": False,
        "labels": [
            {"language": "en-US", "label": f"Script Plugin Lab — {label}"},
            {"language": "tr-TR", "label": f"Script Plugin Lab — {label}"},
        ],
        "task": {
            "order": 1,
            "task": ref("spl-script", "sys-tasks"),
            "mapping": {
                "location": f"./src/{source}",
                "code": b64(functions / "src" / source),
                "scripts": scripts,
            },
        },
    })


write(tasks / "spl-script.json", envelope("spl-script", "sys-tasks", ["script-task"], {
    "type": "7",
    "config": {},
}))

write(mappings / "spl-db-helper.json", envelope("spl-db-helper", "sys-mappings", ["helper", "script-plugin"], {
    "name": "SplDb",
    "location": "./src/SplDbHelper.csx",
    "code": (mappings / "src" / "SplDbHelper.csx").read_text(encoding="utf-8"),
    "encoding": "NAT",
}))

write(functions / "spl-direct-query.json",
      function("spl-direct-query", "Direct Query (helper-less)", "SplDirectQueryMapping.csx", helpers=False))
write(functions / "spl-helper-query.json",
      function("spl-helper-query", "Helper Query", "SplHelperQueryMapping.csx", helpers=True))
