#!/usr/bin/env python3
"""
script-race-lab fixture uretici (csx -> base64 gomulu component JSON'lari).

    python3 core/Workflows/script-race-lab/build-script-race-lab.py --nonce 1

Uretilenler:
  core/Mappings/script-race-lab/race-helper.json      (sys-mappings, encoding NAT)
  core/Workflows/script-race-lab/script-race-lab-parent.json  (type F, scripts.helpers ILE)
  core/Workflows/script-race-lab/script-race-lab-child.json   (type S, tam otomatik)
  core/Workflows/script-race-lab/src/*.csx

Amac: subflow output mapping'in PAYLASILAN AssemblyLoadContext'e derlenmesini saglayip,
N paralel completion ile ayni assembly adinin ikinci kez yuklenmesini tetiklemek.

Uc kosul (bkz. docs/superpowers/specs/2026-08-18-script-race-lab-design.md):
  (a) soguk cache   -> --nonce N, RaceOutputMapping.csx'e `// nonce: N` basar. Cache key
                       kaynak hash'i oldugu icin her yeni nonce YENI bir key uretir; runtime
                       restart'ina gerek kalmaz.
  (b) paylasilan ALC-> helper YALNIZ PARENT'ta bildirilir. SubflowOutputMappingService output
                       mapping'i `flowScripts: parentWorkflow.Scripts` ile derler; helper'i
                       child'a koymak bu yolu etkilemez ve yarisi imkansiz kilar.
  (c) es zamanlilik -> parent ve child'da hic manuel adim yok; start'tan sonra completion
                       kendiliginde gelir, N paralel start N completion'i kumelestirir.

--filler, output mapping'in emit maliyetini belirler (yaris penceresi = emit suresi).
"""

import argparse
import base64
import json
import os

ROOT = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(ROOT, "src")
MAPPING_ROOT = os.path.normpath(os.path.join(ROOT, "..", "..", "Mappings", "script-race-lab"))
MAPPING_SRC = os.path.join(MAPPING_ROOT, "src")

PARENT_KEY = "script-race-lab-parent"
CHILD_KEY = "script-race-lab-child"
HELPER_KEY = "race-helper"

# Helper surumu workflow surumunden AYRI: helper'i bump etmek YENI bir helper set ve YENI bir
# load context demektir. Nonce mekanizmasi bunu gerektirmez, o yuzden sabit.
HELPER_VERSION = "1.0.0"

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

OUTPUT_MAPPING_HEAD = '''// nonce: __NONCE__
// UYARI: bu dosya build-script-race-lab.py tarafindan URETILIR. Elle duzenlemeyin.
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using Acme.Helpers;

/// <summary>
/// script-race-lab subflow output mapping.
/// <para>
/// Iki isi var: (1) helper'i CAGIRMAK - boylece mapping helper set'in paylasilan
/// AssemblyLoadContext'ine derlenir; (2) KASITLI OLARAK GENIS olmak - yaris penceresi Roslyn
/// emit suresidir, dolayisiyla Filler* uyeleri pencereyi olcülebilir sekilde genisletir.
/// </para>
/// <para>
/// Referans materyal DEGILDIR. Bir fixture'dir; boyutu bilerek verilmistir.
/// </para>
/// </summary>
public class RaceOutputMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic subInput = new ExpandoObject();
        if (data != null && HasProperty(data, "testId"))
        {
            subInput.testId = data.testId;
        }

        LogInformation("RaceOutputMapping: prepared sub input");
        return Task.FromResult(new ScriptResponse { Data = subInput });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
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

        var body = context.Body as IDictionary<string, object>;
        if (body != null)
        {
            foreach (var kv in body)
            {
                target[kv.Key] = kv.Value;
            }
        }

        var testId = target.TryGetValue("testId", out var raw) && raw != null ? raw.ToString() : null;

        // Helper cagrisi bu satirdir: mapping'i paylasilan load context'e baglar.
        target["raceStamp"] = RaceHelper.Stamp(testId);
        target["raceCompleted"] = true;

        LogInformation("RaceOutputMapping: stamped " + target["raceStamp"]);
        return Task.FromResult(new ScriptResponse { Data = merged });
    }
'''

# public: kullanilmayan private uye uyarisi/analizci suprizi olmasin.
FILLER_TEMPLATE = '''
    /// <summary>Emit maliyetini artiran dolgu (__INDEX__). Cagrilmaz.</summary>
    public static string Filler__INDEX__(IEnumerable<string> source)
    {
        var builder = new StringBuilder();
        foreach (var item in source.Where(x => x != null && x.Length % __MOD__ == 0)
                                   .Select(x => $"__INDEX__:{x.ToUpperInvariant()}")
                                   .OrderBy(x => x, StringComparer.Ordinal)
                                   .Take(__TAKE__))
        {
            builder.Append(item).Append(';');
        }

        return builder.ToString();
    }
'''


def write_sources(nonce, filler):
    os.makedirs(SRC, exist_ok=True)
    written = []

    rule_path = os.path.join(SRC, "AlwaysTrueRule.csx")
    with open(rule_path, "w") as fh:
        fh.write(ALWAYS_TRUE_RULE)
    written.append(rule_path)

    body = OUTPUT_MAPPING_HEAD.replace("__NONCE__", str(nonce))
    for index in range(1, filler + 1):
        body += (FILLER_TEMPLATE
                 .replace("__INDEX__", str(index))
                 .replace("__MOD__", str((index % 7) + 2))
                 .replace("__TAKE__", str(index + 1)))
    body += "}\n"

    mapping_path = os.path.join(SRC, "RaceOutputMapping.csx")
    with open(mapping_path, "w") as fh:
        fh.write(body)
    written.append(mapping_path)

    return written


def code(name):
    with open(os.path.join(SRC, name), "rb") as fh:
        return base64.b64encode(fh.read()).decode()


def ref(name):
    return {"location": "./src/" + name, "code": code(name)}


def label(text):
    return [{"language": "en-US", "label": text}]


def state(key, state_type, sub_type, labels, transitions, subflow=None):
    return {
        "key": key,
        "stateType": state_type,
        "subType": sub_type,
        "versionStrategy": "Major",
        "labels": label(labels),
        "view": None,
        "subFlow": subflow,
        "onEntries": [],
        "onExits": [],
        "transitions": transitions,
    }


def auto(key, target, labels):
    return {
        "key": key, "target": target, "triggerType": 1, "versionStrategy": "Minor",
        "labels": label(labels), "rule": ref("AlwaysTrueRule.csx"), "onExecutionTasks": [],
    }


def subflow(child_key, version):
    return {
        "type": "S",
        "process": {"key": child_key, "domain": "core", "version": version, "flow": "sys-flows"},
        "mapping": ref("RaceOutputMapping.csx"),
    }


def envelope(key, flow_type, labels, states, cancel_target, start_target, version, scripts=None):
    attributes = {
        "type": flow_type,
        "timeout": None,
        "labels": label(labels),
        "functions": [],
        "features": [],
        "extensions": [],
        "sharedTransitions": [],
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
    if scripts is not None:
        attributes["scripts"] = scripts

    return {
        "key": key,
        "flow": "sys-flows",
        "flowVersion": "1.0.0",
        "domain": "core",
        "version": version,
        "tags": ["integration-test", "script-race-lab", "subflow"],
        "attributes": attributes,
    }


def helper_component():
    with open(os.path.join(MAPPING_SRC, "RaceHelper.csx")) as fh:
        source = fh.read()

    return {
        "key": HELPER_KEY,
        "version": HELPER_VERSION,
        "flow": "sys-mappings",
        "domain": "core",
        "flowVersion": "1.0.0",
        "tags": ["integration-test", "script-race-lab"],
        "attributes": {
            "name": "RaceHelper",
            "location": "./src/RaceHelper.csx",
            "code": source,
            "encoding": "NAT",
        },
    }


def write_json(path, document):
    with open(path, "w") as fh:
        json.dump(document, fh, indent=2)
        fh.write("\n")
    print("wrote", os.path.relpath(path, os.path.join(ROOT, "..", "..", "..")))


def build(version):
    parent = envelope(
        PARENT_KEY, "F", "Script Race Lab Parent",
        [
            state("race-initial", 1, 0, "Race Initial",
                  [auto("auto-race-to-subflow", "race-subflow", "Auto to Race SubFlow")]),
            state("race-subflow", 4, 0, "Race Waiting On Child",
                  [auto("auto-race-to-done", "race-done", "Auto to Race Done")],
                  subflow=subflow(CHILD_KEY, version)),
            state("race-done", 3, 1, "Race Done", []),
            state("race-cancelled", 3, 3, "Race Cancelled", []),
        ],
        cancel_target="race-cancelled",
        start_target="race-initial",
        version=version,
        # Yarisin (b) kosulu. YALNIZ parent'ta.
        scripts={
            "helpers": [
                {"key": HELPER_KEY, "version": HELPER_VERSION, "domain": "core", "flow": "sys-mappings"}
            ]
        },
    )

    child = envelope(
        CHILD_KEY, "S", "Script Race Lab Child",
        [
            state("child-initial", 1, 0, "Child Initial",
                  [auto("auto-child-to-done", "child-done", "Auto to Child Done")]),
            state("child-done", 3, 1, "Child Done", []),
            state("child-cancelled", 3, 3, "Child Cancelled", []),
        ],
        cancel_target="child-cancelled",
        start_target="child-initial",
        version=version,
    )

    write_json(os.path.join(MAPPING_ROOT, "race-helper.json"), helper_component())
    write_json(os.path.join(ROOT, "%s.json" % CHILD_KEY), child)
    write_json(os.path.join(ROOT, "%s.json" % PARENT_KEY), parent)


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--nonce", type=int, default=1,
                        help="output mapping kaynagina basilir; her yeni deger SOGUK cache key uretir")
    parser.add_argument("--version", default="1.0.0", help="workflow component surumu")
    parser.add_argument("--filler", type=int, default=60,
                        help="output mapping'e eklenen dolgu uye sayisi = emit maliyeti")
    args = parser.parse_args()

    written = write_sources(args.nonce, args.filler)
    print("wrote %d csx sources (nonce=%s, filler=%s)" % (len(written), args.nonce, args.filler))
    build(args.version)
    print("\nversion: %s — publish child-first, then re-initialize." % args.version)


if __name__ == "__main__":
    main()
