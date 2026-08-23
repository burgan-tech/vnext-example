#!/usr/bin/env python3
"""
script-perf-lab fixture uretici (csx -> gomulu/NAT component JSON'lari).

    python3 core/Workflows/script-perf-lab/build-script-perf-lab.py --nonce 1

Uretilenler:
  core/Mappings/script-perf-lab/perf-chunk-helper.json   (sys-mappings, encoding NAT)
  core/Mappings/script-perf-lab/perf-stamp-helper.json   (sys-mappings, encoding NAT)
  core/Tasks/script-perf-lab/script-perf-task.json       (type 7, stage script)
  core/Tasks/script-perf-lab/perf-item-http-task.json    (type 6, fan-out child -> MockLab)
  core/Tasks/script-perf-lab/script-perf-fanout-task.json(type 21, inline fan-out)
  core/Workflows/script-perf-lab/script-perf-lab.json    (type F)
  core/Workflows/script-perf-lab/src/*.csx

Amac: script-agirlikli TEK bir akis uzerinde Katman 0 metriklerinin (compile-hit sabiti,
scripts.helpers cok uyeli seti, instance-data append zinciri, FanOutTask inline dal klonu)
makro baseline'ini almak. Bkz. docs/superpowers/specs/2026-08-23-script-perf-katman0-design.md.

Uc mekanizma:
  (a) soguk cache   -> --nonce N, HER StageMapping{K}.csx kaynaginin basina `// nonce: N`
                       basar. Cache key kaynak hash'i oldugu icin her yeni nonce YENI bir key
                       uretir; runtime restart'ina gerek kalmaz. TUM stage'ler ayni nonce'u
                       tasidigindan soguk olcum ilk dokunuslarin HEPSINI kapsar.
  (b) helper seti   -> iki helper (perf-chunk-helper, perf-stamp-helper) YALNIZ workflow'un
                       kendi `scripts.helpers` alaninda bildirilir (A7 cok uyeli yol).
  (c) buyuyen veri  -> her stage kendi onEntry ScriptTask'inda instance data'ya chunkKb
                       boyutunda bir chunk merge eder (delta-only). N. stage'e kadar dokuman
                       linear buyur, append maliyeti (JsonData.Merge/NormalizedJson) kareselle-
                       sir (B9 profili).

`definitions/publish` ayni key+version'i ICERIK DEGISSE DE 409 ile reddeder. Bu yuzden surum
nonce'a BAGLIDIR (varsayilan 1.0.<nonce>): nonce'u surumu artirmadan bumplarsan yeni kaynak
runtime'a HIC ULASMAZ, runtime eski script'i servis etmeye devam eder ve "soguk cache" sessizce
saglanmamis olur. Ayni nedenle workflow + iki task + iki helper hepsi TEK versiyon dizisiyle
uretilir (chunk helper 1.0.1 — icerik degisti, 409 bayat-helper tuzagina karsi bump; stamp 1.0.0;
helper icerigi bu script'te degismiyor).
"""

import argparse
import os

ROOT = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(ROOT, "src")
MAPPING_ROOT = os.path.normpath(os.path.join(ROOT, "..", "..", "Mappings", "script-perf-lab"))
MAPPING_SRC = os.path.join(MAPPING_ROOT, "src")
TASK_ROOT = os.path.normpath(os.path.join(ROOT, "..", "..", "Tasks", "script-perf-lab"))

WORKFLOW_KEY = "script-perf-lab"

CHUNK_HELPER = {"key": "perf-chunk-helper", "version": "1.0.1", "domain": "core", "flow": "sys-mappings"}
STAMP_HELPER = {"key": "perf-stamp-helper", "version": "1.0.0", "domain": "core", "flow": "sys-mappings"}
SCRIPT_TASK = {"key": "script-perf-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}
FANOUT_TASK = {"key": "script-perf-fanout-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}
ITEM_HTTP_TASK = {"key": "perf-item-http-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks"}

TAGS = ["integration-test", "script-perf-lab", "performance-baseline"]

STAGE_TEMPLATE = '''// nonce: __NONCE__
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using Perf.Helpers;

/// <summary>
/// Stage __N__: instance data'ya chunkKb boyutunda deterministik chunk merge eder (delta-only).
/// chunkKb start body'den okunur; helper'lar (A7) chunk + stamp uretir. chunk: kb adet ~1KB
/// node'dan olusan bir liste -- tek buyuk string DEGIL, B9'un per-node maliyetini
/// (NormalizedJson / per-object SerializeToElement) tetiklemek icin dugum-zengin.
/// </summary>
public class StageMapping__N__ : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var inst = context.Instance.Data as IDictionary<string, object>;
        var chunkKb = 4;
        if (inst != null && inst.TryGetValue("chunkKb", out var raw) && raw != null)
        {
            if (int.TryParse(raw.ToString(), out var parsed) && parsed > 0)
            {
                chunkKb = parsed;
            }
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        var stage = (IDictionary<string, object>)new ExpandoObject();
        stage["stamp"] = PerfStampHelper.Stage(__N__, context.Instance.Id.ToString());
        stage["chunk"] = PerfChunkHelper.Build(__N__, chunkKb);
        target["stage__N__"] = stage;
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
'''


def write_sources(nonce, stages):
    """Her stage icin ayri StageMapping{N}.csx yazar; hepsi ayni nonce'u tasir (soguk faz
    TUM stage'lerin ilk dokunusunu kapsasin diye)."""
    os.makedirs(SRC, exist_ok=True)
    written = []
    for n in range(1, stages + 1):
        body = STAGE_TEMPLATE.replace("__NONCE__", str(nonce)).replace("__N__", str(n))
        path = os.path.join(SRC, "StageMapping%d.csx" % n)
        with open(path, "w") as fh:
            fh.write(body)
        written.append(path)
    return written


def code(name):
    """Workflow src/ altindaki bir .csx'i base64 gomer (onEntry/rule referanslari icin)."""
    import base64
    with open(os.path.join(SRC, name), "rb") as fh:
        return base64.b64encode(fh.read()).decode()


def ref(name):
    return {"location": "./src/" + name, "code": code(name)}


def label(text):
    return [{"language": "en-US", "label": text}]


def task(mapping_file, task_def, order=1):
    """onEntry/onExecute hook girisi. Her stage AYRI transition'dan girildigi icin (auto
    transition state'e girerken calisir), ayni SCRIPT_TASK tanimi tum stage'lerde guvenle
    yeniden kullanilabilir -- (TransitionId, TaskId) journal tekillemesi transition bazinda
    calisir, ayni transition icinde IKI KEZ kullanilmadikca cakisma olusmaz."""
    return {
        "order": order,
        "task": dict(task_def),
        "mapping": ref(mapping_file),
    }


def state(key, state_type, sub_type, labels, transitions, on_entries=None):
    return {
        "key": key,
        "stateType": state_type,
        "subType": sub_type,
        "versionStrategy": "Major",
        "labels": label(labels),
        "view": None,
        "subFlow": None,
        "onEntries": on_entries or [],
        "onExits": [],
        "transitions": transitions,
    }


def auto(key, target, labels):
    return {
        "key": key, "target": target, "triggerType": 1, "versionStrategy": "Minor",
        "labels": label(labels), "rule": ref("AlwaysTrueRule.csx"), "onExecutionTasks": [],
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
        "tags": TAGS,
        "attributes": attributes,
    }


def helper_component(const, display_name, source_file):
    """race-lab helper_component() deseni: encoding NAT, kod DUZ METIN (base64 degil)."""
    with open(os.path.join(MAPPING_SRC, source_file)) as fh:
        source = fh.read()

    return {
        "key": const["key"],
        "version": const["version"],
        "flow": const["flow"],
        "domain": const["domain"],
        "flowVersion": "1.0.0",
        "tags": TAGS,
        "attributes": {
            "name": display_name,
            "location": "./src/" + source_file,
            "code": source,
            "encoding": "NAT",
        },
    }


def script_task_component():
    """fanout-stamp-before-task.json emsali: type 7 (ScriptTask), config bos -- islev tamamen
    mapping'te (StageMapping{N}.csx)."""
    return {
        "key": SCRIPT_TASK["key"],
        "version": SCRIPT_TASK["version"],
        "domain": SCRIPT_TASK["domain"],
        "flow": SCRIPT_TASK["flow"],
        "flowVersion": "1.0.0",
        "tags": TAGS + ["script"],
        "attributes": {"type": "7", "config": {}},
    }


def item_http_task_component(http_timeout):
    """fan-out-documents/process-document-task.json ile AYNI sekil: URL'de API_BASEURL
    placeholder + mevcut MockLab route'u (api/fan-out/documents/process) -- yeni mock
    YAZILMAZ, mevcutu yeniden kullanir. timeoutSeconds --http-timeout'tan gelir -- sabit
    birakilirsa --item-timeout'u SESSIZCE golgeler (HTTP client kendi timeout'unda item'i
    kesip FanOutTask'in item deadline'i hic devreye girmeden `isSuccess=false` uretir)."""
    return {
        "key": ITEM_HTTP_TASK["key"],
        "version": ITEM_HTTP_TASK["version"],
        "domain": ITEM_HTTP_TASK["domain"],
        "flow": ITEM_HTTP_TASK["flow"],
        "flowVersion": "1.0.0",
        "tags": TAGS + ["http", "mocklab", "fan-out-inner-task"],
        "attributes": {
            "type": "6",
            "config": {
                "url": "API_BASEURL/api/fan-out/documents/process",
                "method": "POST",
                "headers": {"Content-Type": "application/json"},
                "body": {"source": "script-perf-lab"},
                "timeoutSeconds": http_timeout,
                "validateSsl": True,
            },
        },
    }


def fanout_task_component(dop, item_timeout, batch_timeout):
    """fan-out-documents-task.json ile ayni sekil (type 21, inline mode, allSettled join)."""
    return {
        "key": FANOUT_TASK["key"],
        "version": FANOUT_TASK["version"],
        "domain": FANOUT_TASK["domain"],
        "flow": FANOUT_TASK["flow"],
        "flowVersion": "1.0.0",
        "tags": TAGS + ["fan-out", "task-type-21", "all-settled"],
        "attributes": {
            "type": "21",
            "config": {
                "mode": "inline",
                "itemsPath": "$.fanoutItems",
                "itemAlias": "item",
                "task": {
                    "key": ITEM_HTTP_TASK["key"],
                    "domain": ITEM_HTTP_TASK["domain"],
                    "flow": ITEM_HTTP_TASK["flow"],
                    "version": ITEM_HTTP_TASK["version"],
                },
                "execution": {
                    "maxDegreeOfParallelism": dop,
                    "itemTimeoutSeconds": item_timeout,
                    "batchTimeoutSeconds": batch_timeout,
                },
                "join": {"policy": "allSettled", "resultKey": "perfItemResults", "ordered": True},
            },
        },
    }


def write_json(path, document):
    import json
    with open(path, "w") as fh:
        json.dump(document, fh, indent=2)
        fh.write("\n")
    print("wrote", os.path.relpath(path, os.path.join(ROOT, "..", "..", "..")))


def build(version, stages, dop, item_timeout, batch_timeout, http_timeout):
    states = [
        state("perf-initial", 1, 0, "Perf Initial",
              [auto("auto-to-stage-1", "perf-stage-1", "Auto to Stage 1")]),
    ]

    for n in range(1, stages + 1):
        target = "perf-stage-%d" % (n + 1) if n < stages else "perf-fanout"
        states.append(
            state("perf-stage-%d" % n, 2, 0, "Perf Stage %d" % n,
                  [auto("auto-stage-%d-to-next" % n, target, "Auto Stage %d to Next" % n)],
                  on_entries=[task("StageMapping%d.csx" % n, SCRIPT_TASK)])
        )

    states.append(
        state("perf-fanout", 2, 0, "Perf Fan-Out",
              [auto("auto-to-done", "perf-done", "Auto to Done")],
              on_entries=[{
                  "order": 1,
                  "task": dict(FANOUT_TASK),
                  "mapping": ref("FanOutItemMapping.csx"),
              }])
    )
    states.append(state("perf-done", 3, 1, "Perf Done", []))
    states.append(state("perf-cancelled", 3, 3, "Perf Cancelled", []))

    workflow = envelope(
        WORKFLOW_KEY, "F", "Script Perf Lab",
        states,
        cancel_target="perf-cancelled",
        start_target="perf-initial",
        version=version,
        scripts={"helpers": [CHUNK_HELPER, STAMP_HELPER]},
    )

    os.makedirs(MAPPING_ROOT, exist_ok=True)
    os.makedirs(TASK_ROOT, exist_ok=True)

    write_json(os.path.join(MAPPING_ROOT, "perf-chunk-helper.json"),
               helper_component(CHUNK_HELPER, "PerfChunkHelper", "PerfChunkHelper.csx"))
    write_json(os.path.join(MAPPING_ROOT, "perf-stamp-helper.json"),
               helper_component(STAMP_HELPER, "PerfStampHelper", "PerfStampHelper.csx"))
    write_json(os.path.join(TASK_ROOT, "script-perf-task.json"), script_task_component())
    write_json(os.path.join(TASK_ROOT, "perf-item-http-task.json"),
               item_http_task_component(http_timeout))
    write_json(os.path.join(TASK_ROOT, "script-perf-fanout-task.json"),
               fanout_task_component(dop, item_timeout, batch_timeout))
    write_json(os.path.join(ROOT, "%s.json" % WORKFLOW_KEY), workflow)


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--nonce", type=int, default=1,
                        help="her StageMapping{N}.csx kaynaginin basina basilir; her yeni "
                             "deger TUM stage'ler icin SOGUK cache key uretir")
    # Surum nonce'a BAGLI (varsayilan 1.0.<nonce>) ve bu bir kolaylik degil, zorunluluk:
    # `definitions/publish` ayni key+version'i ICERIK DEGISSE DE 409 ile reddeder. Nonce'u
    # surumu artirmadan bumplarsan yeni kaynak runtime'a HIC ULASMAZ, runtime eski script'i
    # servis etmeye devam eder ve "soguk cache" sessizce saglanmamis olur.
    parser.add_argument("--version", default=None,
                        help="workflow component surumu (varsayilan: 1.0.<nonce>)")
    parser.add_argument("--stages", type=int, default=10,
                        help="perf-stage-1..N sayisi (B9 O(n^2) append profili derinligi); "
                             "48'e esit/uzeri reddedilir (MaxChainDepth=50 auto-chain siniri)")
    parser.add_argument("--fanout-dop", type=int, default=4,
                        help="script-perf-fanout-task execution.maxDegreeOfParallelism")
    parser.add_argument("--item-timeout", type=int, default=20,
                        help="script-perf-fanout-task execution.itemTimeoutSeconds -- "
                             "--http-timeout BUNDAN KUCUKSE veya esitse item deadline'ina hic "
                             "ULASILMADAN HTTP client kendi timeout'unda item'i keser (sessiz "
                             "golgeleme); --http-timeout'u bunun UZERINDE tut")
    parser.add_argument("--batch-timeout", type=int, default=120,
                        help="script-perf-fanout-task execution.batchTimeoutSeconds")
    parser.add_argument("--http-timeout", type=int, default=10,
                        help="perf-item-http-task attributes.config.timeoutSeconds (inner "
                             "HTTP client timeout'u) -- --item-timeout'u golgelememesi icin "
                             "ondan BUYUK tutulmali")
    args = parser.parse_args()

    if args.stages >= 48:
        parser.error(
            "--stages %d >= 48 reddedildi: auto-chain zinciri start + %d stage hop'u + fanout "
            "+ done olarak MaxChainDepth=50 auto-chain sinirina carpar ya da onu asar. Daha "
            "kucuk bir --stages secin." % (args.stages, args.stages))

    version = args.version or "1.0.%d" % args.nonce

    written = write_sources(args.nonce, args.stages)
    print("wrote %d stage csx sources (nonce=%s, stages=%s)" % (len(written), args.nonce, args.stages))
    build(version, args.stages, args.fanout_dop, args.item_timeout, args.batch_timeout, args.http_timeout)
    print("\nversion: %s -- publish helpers -> tasks -> workflow, then re-initialize." % version)


if __name__ == "__main__":
    main()
