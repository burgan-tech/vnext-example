# script-race-lab Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a fully automatic parent/child fixture in `vnext-example` whose parent declares a global script helper, so N parallel starts drive N concurrent subflow output-mapping compilations into one shared `AssemblyLoadContext` — reproducing `Script_<hash> … Assembly with same name is already loaded` on a pre-fix runtime and proving it gone on the fixed one.

**Architecture:** Two generated workflow JSONs (`script-race-lab-parent`, `script-race-lab-child`) plus one `sys-mappings` helper component. A Python generator base64-embeds the `.csx` sources exactly as `core/Workflows/chain-busy/build-chain-busy.py` does, and stamps a `// nonce: N` line into the output mapping so each run can get a cold compile cache without restarting the runtime. Evidence is collected two ways: an xUnit test in the existing integration suite (CI-runnable assertion) and a JMeter plan (real load profile).

**Tech Stack:** vNext component JSON (schema 0.0.52), C# scripting (`ScriptBase`, `ISubFlowMapping`, `IConditionMapping`), Python 3 generator, xUnit + `VNext.Testing.Sdk` 0.0.6, JMeter 5.6.

**Spec:** `docs/superpowers/specs/2026-08-18-script-race-lab-design.md`

---

## File Structure

All paths relative to `/Users/U0B006/Documents/repos/burgan-tech/vnext-example`.

| Path | Responsibility |
|---|---|
| `core/Mappings/script-race-lab/src/RaceHelper.csx` | The global helper class. One pure function. |
| `core/Mappings/script-race-lab/race-helper.json` | `sys-mappings` envelope for the helper (generated, `encoding: "NAT"`). |
| `core/Workflows/script-race-lab/src/RaceHelper.csx` | **Not created.** The helper lives only under `Mappings/`. |
| `core/Workflows/script-race-lab/src/AlwaysTrueRule.csx` | Rule for both auto transitions. |
| `core/Workflows/script-race-lab/src/RaceOutputMapping.csx` | `ISubFlowMapping`. Calls the helper; carries the nonce and the filler bulk that sets the emit cost. |
| `core/Workflows/script-race-lab/script-race-lab-parent.json` | Generated. `type: "F"`, declares `scripts.helpers`. |
| `core/Workflows/script-race-lab/script-race-lab-child.json` | Generated. `type: "S"`, fully automatic. |
| `core/Workflows/script-race-lab/build-script-race-lab.py` | Generator: writes the three `.csx` files and the three JSONs. Knobs: `--nonce`, `--version`, `--filler`. |
| `api-tests/script-race-lab/publish.py` | Publishes helper → child → parent, then `re-initialize`. Needed for JMeter runs; the integration suite publishes itself. |
| `tests/Core.IntegrationTests/Tests/ScriptRaceLab/ScriptRaceLabTests.cs` | Smoke test + parallel race test. |
| `jmeter/tests/script-race-lab.jmx` | 30-thread, ramp-0 load plan. |

Two files are deliberately **not** created: no `core/Tasks/script-race-lab/` component (the output mapping's own data write is the evidence) and no shared rule library (each fixture keeps its own `AlwaysTrueRule.csx`, matching chain-busy).

---

## Task 1: Confirm the environment before writing anything

**Files:** none (verification only)

- [ ] **Step 1: Confirm the runtime answers**

Run:

```bash
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:4201/api/v1/definitions/re-initialize
```

Expected: `200`. Anything else means the local runtime on 4201 is not up — stop and tell the user; every later task depends on it.

- [ ] **Step 2: Confirm which runtime build is running**

Run:

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext && git rev-parse --abbrev-ref HEAD
```

Record the answer. Task 8 needs to know whether the running host is the fixed branch (`fix/script-alc-double-compile-race`) or `master`. Do **not** rebuild anything yet.

---

## Task 2: The failing smoke test

Write the test before the fixture exists, so the first run proves the test actually exercises the runtime rather than passing vacuously.

**Files:**
- Create: `tests/Core.IntegrationTests/Tests/ScriptRaceLab/ScriptRaceLabTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ScriptRaceLab;

/// <summary>
/// script-race-lab: a fixture for the script-assembly double-compile race.
/// <para>
/// The parent declares <c>scripts.helpers</c>, so its subflow output mapping compiles into the
/// helper set's shared, singleton-lifetime <c>AssemblyLoadContext</c>
/// (<c>SubflowOutputMappingService</c> compiles with <c>parentWorkflow.Scripts</c>). Parent and
/// child are fully automatic, so N parallel starts land N completions — N compilations of the
/// same mapping under the same assembly name — inside one emit window.
/// </para>
/// <para>
/// On a pre-fix runtime the losers throw <c>FileLoadException</c>, the mapping fails, and the
/// parent is faulted permanently. On the fixed runtime every parent completes.
/// </para>
/// </summary>
public class ScriptRaceLabTests : WorkflowTestBase
{
    private const string Parent = "script-race-lab-parent";

    /// <summary>
    /// Starts that must overlap for the race to be possible. The knob to turn if a run does not
    /// trigger it — the other is the filler bulk in RaceOutputMapping.csx.
    /// </summary>
    private const int ParallelStarts = 30;

    public ScriptRaceLabTests(VNextTestEnvironment environment) : base(environment) { }

    private Task<string> StartOneAsync(string tag) =>
        StartAsync(Parent, new { testId = $"{tag}-{Guid.NewGuid():N}"[..24] });

    [Fact]
    public async Task Smoke_SingleInstance_CompletesAndCarriesTheHelperStamp()
    {
        var parentId = await StartOneAsync("smoke");

        await WaitForInstanceStateAsync(Parent, parentId, "race-done", timeout: TimeSpan.FromSeconds(90));

        var (state, status) = await GetInstanceStateAsync(Parent, parentId);
        Assert.Equal("race-done", state);
        Assert.Equal("C", status);

        var attributes = await GetAttributesAsync(Parent, parentId);
        Assert.True(attributes.TryGetProperty("raceStamp", out var stamp),
            $"the output mapping did not run or did not reach the helper — {await DescribeAsync(Parent, parentId)}");
        Assert.StartsWith("race:", stamp.GetString());
    }
}
```

- [ ] **Step 2: Run it and verify it fails**

Run:

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example/tests/Core.IntegrationTests && VNEXT_BASE_URL=http://localhost:4201 dotnet test --filter "FullyQualifiedName~ScriptRaceLabTests.Smoke"
```

Expected: FAIL. The workflow does not exist yet, so the start call fails (404 / definition-not-found from `StartInstanceAsync`). A PASS here would mean the test is not talking to the runtime — investigate before continuing.

- [ ] **Step 3: Commit**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example
git add tests/Core.IntegrationTests/Tests/ScriptRaceLab/ScriptRaceLabTests.cs
git commit -m "test(script-race-lab): failing smoke test for the race fixture"
```

---

## Task 3: The helper component

**Files:**
- Create: `core/Mappings/script-race-lab/src/RaceHelper.csx`

The JSON envelope is written by the generator in Task 4, so this task only creates the source.

- [ ] **Step 1: Write the helper**

```csharp
using System;

namespace Acme.Helpers;

/// <summary>
/// Global helper for the script-race-lab fixture.
/// <para>
/// Its body is irrelevant; its existence is the point. A workflow that declares
/// <c>scripts.helpers</c> makes every script it compiles share the helper set's
/// singleton-lifetime AssemblyLoadContext, and a shared context cannot hold two assemblies with
/// the same simple name — which is the collision the fixture reproduces.
/// </para>
/// </summary>
public static class RaceHelper
{
    /// <summary>Deterministic stamp, so a test can assert the helper really resolved.</summary>
    public static string Stamp(string testId) => "race:" + (testId ?? "none");
}
```

- [ ] **Step 2: Commit**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example
git add core/Mappings/script-race-lab/src/RaceHelper.csx
git commit -m "feat(script-race-lab): add the global script helper source"
```

---

## Task 4: The generator

**Files:**
- Create: `core/Workflows/script-race-lab/build-script-race-lab.py`

- [ ] **Step 1: Write the generator**

```python
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
```

- [ ] **Step 2: Run the generator**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example && python3 core/Workflows/script-race-lab/build-script-race-lab.py --nonce 1
```

Expected output names 2 csx sources and 3 JSON files. Confirm `core/Workflows/script-race-lab/src/RaceOutputMapping.csx` starts with `// nonce: 1` and that `core/Workflows/script-race-lab/script-race-lab-parent.json` contains `"scripts"` while the child JSON does not:

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example && head -1 core/Workflows/script-race-lab/src/RaceOutputMapping.csx && grep -c '"scripts"' core/Workflows/script-race-lab/script-race-lab-parent.json && grep -c '"scripts"' core/Workflows/script-race-lab/script-race-lab-child.json || true
```

Expected: `// nonce: 1`, then `1`, then `0`.

- [ ] **Step 3: Validate the components against the schema**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example && npm run validate
```

Expected: PASS with no errors for `script-race-lab`. If the schema rejects a field, fix the generator (not the emitted JSON) and re-run Step 2 — the JSONs are generated artifacts.

- [ ] **Step 4: Commit**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example
git add core/Workflows/script-race-lab core/Mappings/script-race-lab
git commit -m "feat(script-race-lab): generate the parent/child race fixture"
```

---

## Task 5: Publish and make the smoke test pass

**Files:**
- Create: `api-tests/script-race-lab/publish.py`

- [ ] **Step 1: Write the publisher**

```python
#!/usr/bin/env python3
"""
script-race-lab bilesenlerini lokal runtime'a publish eder ve cache'i yeniler.

    python3 api-tests/script-race-lab/publish.py

Sira ONEMLI: helper -> child -> parent. Parent'in `scripts.helpers` referansi ile subFlow
`process` referansi publish aninda cozulur; tersi sirada referans bulunamaz.

Integration suite bunu KENDISI yapar (VNextTestEnvironment.EnableDomainPublish). Bu script
JMeter kosulari ve elle dogrulama icindir.
"""

import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

BASE = "http://localhost:4201/api/v1"
REPO = Path(__file__).resolve().parents[2]

COMPONENTS = [
    REPO / "core" / "Mappings" / "script-race-lab" / "race-helper.json",
    REPO / "core" / "Workflows" / "script-race-lab" / "script-race-lab-child.json",
    REPO / "core" / "Workflows" / "script-race-lab" / "script-race-lab-parent.json",
]


def http(method, url, body=None):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(url, data=data, method=method,
                                     headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.status, response.read().decode()
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode()


def main():
    for path in COMPONENTS:
        document = json.loads(path.read_text())
        status, response = http("POST", "%s/definitions/publish" % BASE, document)
        if status in (200, 201):
            print("  published %s v%s" % (document["key"], document["version"]))
        elif status == 409:
            print("  %s zaten publish edilmis (409)" % document["key"])
        else:
            print("  ! %s publish HTTP %s: %s" % (document["key"], status, response))
            return 1

    http("GET", "%s/definitions/re-initialize" % BASE)
    print("  re-initialize ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 2: Publish**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example && python3 api-tests/script-race-lab/publish.py
```

Expected: three `published …` lines then `re-initialize ok`.

- [ ] **Step 3: Run the smoke test — it must now pass**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example/tests/Core.IntegrationTests && VNEXT_BASE_URL=http://localhost:4201 dotnet test --filter "FullyQualifiedName~ScriptRaceLabTests.Smoke"
```

Expected: PASS. If the instance faults instead, the assertion message carries the incident text — read it before changing anything. Two likely causes and their fixes:

- *sandbox rejected an API in the mapping* — remove the offending `using`/call from `OUTPUT_MAPPING_HEAD` or `FILLER_TEMPLATE` in the generator and re-run Task 4 Step 2.
- *helper type not found (`Acme.Helpers` unresolved)* — the parent's `scripts.helpers` entry does not match the published helper's `key`/`version`/`flow`; compare `race-helper.json` with the `scripts` block in the parent JSON.

- [ ] **Step 4: Commit**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example
git add api-tests/script-race-lab/publish.py
git commit -m "test(script-race-lab): add the local-runtime publisher"
```

---

## Task 6: The parallel race test

**Files:**
- Modify: `tests/Core.IntegrationTests/Tests/ScriptRaceLab/ScriptRaceLabTests.cs`

- [ ] **Step 1: Add the race test**

Append inside the class, after `Smoke_SingleInstance_CompletesAndCarriesTheHelperStamp`:

```csharp
    [Fact]
    public async Task ParallelStarts_AllComplete_WithoutAnAssemblyLoadFault()
    {
        // The starts must OVERLAP: the race window is one Roslyn emit of the output mapping, and
        // only completions that arrive while the cache entry is still cold can collide.
        var ids = await Task.WhenAll(
            Enumerable.Range(0, ParallelStarts).Select(index => StartOneAsync($"race{index:D2}")));

        // Wait on the state, not the status: a parent holding an open SubFlow correlation is Busy
        // by design for the child's whole lifetime.
        await Task.WhenAll(ids.Select(async id =>
            await WaitUntilAsync(
                async () => TerminalStatuses.Contains((await GetInstanceStateAsync(Parent, id)).Status),
                $"{Parent}/{id} never settled",
                TimeSpan.FromSeconds(180))));

        var faulted = new List<string>();
        foreach (var id in ids)
        {
            var (_, status) = await GetInstanceStateAsync(Parent, id);
            if (status == "F") faulted.Add(await DescribeAsync(Parent, id));
        }

        Assert.True(faulted.Count == 0,
            $"{faulted.Count}/{ParallelStarts} parents faulted. On a pre-fix runtime this is the " +
            "reproduction — expect Instance:100030 with an inner FileLoadException naming " +
            "'Script_…' and 'Assembly with same name is already loaded'. Faulted instances:" +
            Environment.NewLine + string.Join(Environment.NewLine, faulted));

        // Every survivor must also have actually run the mapping — an all-C run where the mapping
        // silently did nothing would prove nothing.
        foreach (var id in ids)
        {
            var attributes = await GetAttributesAsync(Parent, id);
            Assert.True(attributes.TryGetProperty("raceStamp", out _),
                $"instance completed without the output mapping's stamp — {await DescribeAsync(Parent, id)}");
        }
    }
```

- [ ] **Step 2: Run it**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example/tests/Core.IntegrationTests && VNEXT_BASE_URL=http://localhost:4201 dotnet test --filter "FullyQualifiedName~ScriptRaceLabTests.ParallelStarts"
```

Expected on the **fixed** runtime: PASS. Expected on a **pre-fix** runtime: FAIL, listing faulted instances. Either outcome is a valid result of this step — record which runtime was running (Task 1 Step 2) with the outcome.

Note: this run warms the compile cache for nonce 1 in that process. Task 8 bumps the nonce for each subsequent measurement.

- [ ] **Step 3: Commit**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example
git add tests/Core.IntegrationTests/Tests/ScriptRaceLab/ScriptRaceLabTests.cs
git commit -m "test(script-race-lab): assert 30 parallel completions never fault"
```

---

## Task 7: The JMeter plan

**Files:**
- Create: `jmeter/tests/script-race-lab.jmx` (copied from `jmeter/tests/workflow-test.jmx`, then edited)

- [ ] **Step 1: Copy the existing plan**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example && cp jmeter/tests/workflow-test.jmx jmeter/tests/script-race-lab.jmx
```

- [ ] **Step 2: Edit the copy — plan-level changes**

In `jmeter/tests/script-race-lab.jmx`:

1. `TestPlan` `testname` → `Script Race Lab Load Test`; its `TestPlan.comments` →
   `script-race-lab parent: 30 concurrent starts so N subflow output-mapping compilations land in one emit window. Fails a sample when the instance ends Faulted.`
2. In `User Defined Variables`, change the defaults so the plan opens the race window rather than a steady load:
   - `users` → `${__P(users,30)}`
   - `rampup` → `${__P(rampup,0)}`
   - `loops` → `${__P(loops,1)}`
   Leave `base.url` as is (`${__P(base.url,http://host.docker.internal:4201)}`).
3. `ThreadGroup` `testname` → `Race Starters`.

- [ ] **Step 3: Edit the copy — sampler changes**

1. Delete the three transition samplers and their `hashTree` siblings, by `testname`:
   `2. select-demand-deposit`, `3. submit-account-details`, `4. confirm-account-opening`, plus
   the `T1 Headers` / `T2 Headers` / `T3 Headers` elements and the `Poll 2 - Reset Counter` /
   `Poll 3 - Reset Counter` / `While poll 2` / `While poll 3` controllers that belong to them.
   What remains is: setup preprocessor → start sampler → extractors → one poll loop → reports.
2. Point the start sampler at the fixture. Keep the existing `Setup Random Vars` preprocessor (it
   already sets `key`, `requestId`, `status`, `pollCount`, `protocol`, `host`, `port`) and keep the
   `Start Headers` element as is. In the `1. Start Instance` sampler change only the body argument
   and the path — the sampler addresses the host through the `protocol`/`host`/`port` vars, so the
   path must stay a path, not a full URL:

```xml
                <stringProp name="Argument.value">{"testId":"jm-${__threadNum}-${requestId}"}</stringProp>
```

```xml
          <stringProp name="HTTPSampler.path">/api/v1/core/workflows/script-race-lab-parent/instances/start?sync=false</stringProp>
```
3. Point the poll sampler at the parent's state function — replace the `GET state` sampler's path:

```xml
          <stringProp name="HTTPSampler.path">/api/v1/core/workflows/script-race-lab-parent/instances/${instanceId}/functions/state</stringProp>
```
4. Change the poll loop's exit condition. The existing plan waits for `A`; this fixture runs to
   completion, so it must wait for a terminal status instead, with a cap high enough for a start →
   subflow → completion round trip. Rename the `While` controller to
   `While status != C && status != F && pollCount < 60` and set its condition — same `__groovy`
   form the plan already uses:

```xml
          <stringProp name="WhileController.condition">${__groovy(!"C".equals(vars.get("status")) &amp;&amp; !"F".equals(vars.get("status")) &amp;&amp; (vars.get("pollCount") as Integer) &lt; 60)}</stringProp>
```
5. Add a Response Assertion as the last child of the `GET state` sampler's `hashTree`, so a faulted
   instance fails the sample:

```xml
          <ResponseAssertion guiclass="AssertionGui" testclass="ResponseAssertion" testname="Not Faulted" enabled="true">
            <collectionProp name="Asserion.test_strings">
              <stringProp name="faulted">"status":"F"</stringProp>
            </collectionProp>
            <stringProp name="Assertion.test_field">Assertion.response_data</stringProp>
            <boolProp name="Assertion.assume_success">false</boolProp>
            <intProp name="Assertion.test_type">6</intProp>
            <stringProp name="Assertion.custom_message">instance FAULTED — expected Instance:100030 / 'Assembly with same name is already loaded' in the runtime log</stringProp>
          </ResponseAssertion>
```

   `test_type` 6 is "Substring, not-contains".

- [ ] **Step 4: Verify the plan parses and runs**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example && jmeter -n -t jmeter/tests/script-race-lab.jmx -Jbase.url=http://localhost:4201 -Jusers=2 -Jrampup=0 -Jloops=1 -l jmeter/results/script-race-lab-smoke.jtl
```

Expected: JMeter completes with `summary = 2 in …` and no `Err:` count above zero. A parse error means an edited element is malformed — fix the XML. If `jmeter` is not on PATH, report that to the user rather than installing it.

- [ ] **Step 5: Run the real load profile**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example && jmeter -n -t jmeter/tests/script-race-lab.jmx -Jbase.url=http://localhost:4201 -l jmeter/results/script-race-lab.jtl
```

Expected: 30 samples per step. Record the error count.

- [ ] **Step 6: Commit**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example
git add jmeter/tests/script-race-lab.jmx
git commit -m "test(script-race-lab): add the 30-thread JMeter race plan"
```

---

## Task 8: Prove the reproduction, then prove the fix

This task produces the evidence the whole plan exists for. It needs the `vnext` runtime rebuilt twice, so it runs last.

**Files:**
- Create: `docs/superpowers/plans/2026-08-18-script-race-lab-results.md`

- [ ] **Step 1: Ask the user to bring up the pre-fix runtime**

The runtime is started by the user, not by this plan. Tell them exactly what is needed:

> Stop the runtime on 4201, check out `master` in `/Users/U0B006/Documents/repos/burgan-tech/vnext`, and start the orchestration host again (`dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host`). Tell me when it is up.

Do not check out or rebuild the `vnext` repo yourself — the user's working tree there has uncommitted changes.

- [ ] **Step 2: Cold-start the pre-fix measurement**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example && python3 core/Workflows/script-race-lab/build-script-race-lab.py --nonce 2 && python3 api-tests/script-race-lab/publish.py
```

Expected: the generator reports `nonce=2` and the publisher reports three published components. A new nonce means a cold cache key even though the process has already compiled nonce 1.

- [ ] **Step 3: Run both harnesses against the pre-fix runtime**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example/tests/Core.IntegrationTests && VNEXT_BASE_URL=http://localhost:4201 dotnet test --filter "FullyQualifiedName~ScriptRaceLabTests.ParallelStarts"
```

Expected: FAIL with faulted instances listed. Then:

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example && python3 core/Workflows/script-race-lab/build-script-race-lab.py --nonce 3 && python3 api-tests/script-race-lab/publish.py && jmeter -n -t jmeter/tests/script-race-lab.jmx -Jbase.url=http://localhost:4201 -l jmeter/results/script-race-lab-prefix.jtl
```

Expected: non-zero error count.

If **neither** harness produces a fault, the race did not trigger. Turn one knob and repeat with a fresh nonce, in this order:

1. `--filler 200` (widens the emit window — the highest-leverage knob).
2. `ParallelStarts` 30 → 60 in the test, `-Jusers=60` for JMeter.

Record every attempt in the results doc, including the ones that did not trigger — a knob value that failed to reproduce is a finding, not a wasted run.

- [ ] **Step 4: Capture the runtime-side evidence**

Ask the user for the orchestration host's log lines around the failures, and confirm they contain
`Instance:100030`, `FileLoadException`, and `Assembly with same name is already loaded`. That trio is
the reproduction signature; a fault without it is a different bug and must be investigated before
continuing.

- [ ] **Step 5: Ask the user to bring up the fixed runtime**

> Stop the runtime, check out `fix/script-alc-double-compile-race`, start the orchestration host again. Tell me when it is up.

- [ ] **Step 6: Re-run both harnesses on the fixed runtime**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example && python3 core/Workflows/script-race-lab/build-script-race-lab.py --nonce 4 && python3 api-tests/script-race-lab/publish.py
cd tests/Core.IntegrationTests && VNEXT_BASE_URL=http://localhost:4201 dotnet test --filter "FullyQualifiedName~ScriptRaceLab"
```

Expected: both tests PASS.

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example && python3 core/Workflows/script-race-lab/build-script-race-lab.py --nonce 5 && python3 api-tests/script-race-lab/publish.py && jmeter -n -t jmeter/tests/script-race-lab.jmx -Jbase.url=http://localhost:4201 -l jmeter/results/script-race-lab-fixed.jtl
```

Expected: zero errors.

Use the **same** `--filler` and `ParallelStarts` values that reproduced the fault in Step 3. Comparing a fixed run at a lower setting than the pre-fix run proves nothing.

- [ ] **Step 7: Write the results document**

Create `docs/superpowers/plans/2026-08-18-script-race-lab-results.md` with, for each of the two
runtimes: the git ref, the nonce, `--filler` and `ParallelStarts` values, the faulted count out of 30,
the JMeter error count, and the runtime log signature. State plainly whether the reproduction
triggered and whether the fix removed it.

- [ ] **Step 8: Commit**

```bash
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example
git add docs/superpowers/plans/2026-08-18-script-race-lab-results.md jmeter/results
git commit -m "docs(script-race-lab): record the pre-fix reproduction and the fixed run"
```

---

## Notes for the implementer

- **The generated JSONs are artifacts.** Never hand-edit `script-race-lab-*.json` or
  `race-helper.json`; change `build-script-race-lab.py` and re-run it. The same rule holds for
  `src/AlwaysTrueRule.csx` and `src/RaceOutputMapping.csx` — both are written by the generator.
  `core/Mappings/script-race-lab/src/RaceHelper.csx` is the one hand-written source.
- **The helper belongs to the parent.** If a step tempts you to move `scripts.helpers` to the child
  or to add it to both, stop: the output mapping compiles with `parentWorkflow.Scripts`, so the child's
  declaration has no effect on the path under test and the race becomes impossible.
- **One cold window per nonce per process.** A second run at the same nonce against the same host
  process proves nothing — the entry is warm. Every measurement in Task 8 gets a fresh nonce.
- **Do not add a switch that disables the fix.** The comparison is made by running two runtime builds.
