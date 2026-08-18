# script-race-lab — Script ALC Double-Compile Race Reproduction — Design

**Date:** 2026-08-18
**Repo:** `vnext-example` (domain `core`)
**Related runtime work:** `vnext` branch `fix/script-alc-double-compile-race`,
spec `vnext/docs/superpowers/specs/2026-08-17-script-alc-double-compile-race-design.md`

## 1. Purpose

Produce a runnable, self-contained flow in `vnext-example` that reproduces the
`Script_<hash> ... Assembly with same name is already loaded` failure on a pre-fix runtime and
proves it is gone on the fixed runtime. The reproduction must be usable from both a load tool
(JMeter) and the CI-runnable integration suite.

Success is a pair of observations, not a single green test:

- **Pre-fix runtime:** at least one parent instance ends in status `F` carrying
  `Instance:100030` with an inner `FileLoadException` naming `Script_…`.
- **Fixed runtime:** every parent instance ends in status `C` and its data carries the value the
  output mapping computed through the helper.

## 2. Why a purpose-built flow

The failure needs three conditions to intersect simultaneously
(`2026-08-17-script-alc-double-compile-race-design.md` §1):

| Condition | How this design supplies it |
|---|---|
| **(a) Cold compile cache** | Cache key is the hash of the mapping source. The generator stamps a `// nonce: N` line into the output mapping, so bumping `N` yields a cold key without restarting the runtime. A restart also works and stays supported. |
| **(b) Shared `AssemblyLoadContext`** | The **parent** workflow declares `attributes.scripts.helpers`. `SubflowOutputMappingService.ApplyAsync` compiles the subflow output mapping with `flowScripts: parentWorkflow.Scripts`, so the helper set's singleton-lifetime context is the load context — the parent, not the child, must carry the helper. |
| **(c) Concurrent completions of distinct instances** | Parent and child are fully automatic: a `POST start` drives the parent into its SubFlow state, the child runs to its finish state under its own power, and the completion lands within a second or two. N parallel starts therefore cluster N completions inside the emit window. |

Existing flows were considered and rejected for (c): `subflow-orchestration` gates the parent behind
N `updateData` calls plus manual descendant steps, `future-pay/loan-disbursement` and
`contract-signing` need several steps before the subflow opens. Driving 30 instances through those
gates makes the load test the dominant cost and the clustering unreliable. A minimal lab flow keeps
the repro's variables to two knobs (N, emit cost) and leaves existing flows' semantics untouched.

## 3. Components

All paths relative to `vnext-example/`. Both workflow JSONs are generated — the mapping code is
base64-embedded, exactly as `core/Workflows/chain-busy/build-chain-busy.py` does it, and that
script is the structural template for the generator.

| Path | Role |
|---|---|
| `core/Mappings/script-race-lab/race-helper.json` + `src/RaceHelper.csx` | The global helper. `namespace Acme.Helpers; public static class RaceHelper` with one pure function (`Stamp(string)`), `encoding: "NAT"`, `flow: "sys-mappings"`. Its only structural job is to make the parent declare a helper set. |
| `core/Workflows/script-race-lab/script-race-lab-parent.json` | `type: "F"`, `attributes.scripts.helpers: [race-helper]`. States: `race-initial (1)` →auto→ `race-subflow (4)` →auto→ `race-done (3)`. |
| `core/Workflows/script-race-lab/script-race-lab-child.json` | `type: "S"`. States: `child-initial (1)` →auto→ `child-done (3, subType Success)`. No manual step, no task. |
| `core/Workflows/script-race-lab/src/RaceOutputMapping.csx` | `ScriptBase, ISubFlowMapping`. `InputHandler` passes `testId` down. `OutputHandler` merges the child body into instance data and writes `raceStamp = RaceHelper.Stamp(...)` plus `raceCompleted = true`. Deliberately wide (many `using` directives, enough body) so a single emit costs hundreds of milliseconds — the race window **is** the emit duration. |
| `core/Workflows/script-race-lab/src/AlwaysTrueRule.csx` | `ScriptBase, IConditionMapping` returning true; the auto transitions' rule. Copied from the chain-busy fixture rather than shared, matching how the existing fixtures keep their own copy. |
| `core/Workflows/script-race-lab/build-script-race-lab.py` | Generator. `--nonce N` (default 1) stamps `// nonce: N` into `RaceOutputMapping.csx` before base64 embedding; `--version` bumps the component version when a redeploy needs a new definition version. Writes both workflow JSONs and the `src/*.csx` files. |

**No Tasks component.** The reproduction needs the subflow output mapping and nothing else; the
mapping's own write into instance data is what proves the helper resolved and the mapping ran. Adding
`onEntry`/`onExecute` tasks would add components without adding evidence.

## 4. Tests

### 4.1 Integration (`tests/Core.IntegrationTests/Tests/ScriptRaceLab/ScriptRaceLabTests.cs`)

Extends `WorkflowTestBase`, following `SubflowOrchestrationTests`' shape.

- `Smoke_SingleInstance_CompletesAndStampsTheHelperValue` — one start, wait for `race-done`/`C`,
  assert `raceStamp` is present. Proves the flow and the helper wiring independently of concurrency.
- `ParallelStarts_AllComplete_WithoutAnAssemblyLoadFault` — `Task.WhenAll` over **N = 30** starts,
  then wait each to settle. Asserts every instance reached `C`. For any instance in `F`, the failure
  message includes that instance's incident text so the repro signature (`Instance:100030`,
  `already loaded`, `Script_…`) is visible in the test output rather than only in runtime logs.

The second test is the regression guard on the fixed runtime **and** the repro driver on a pre-fix
runtime — the same test, opposite expectations, distinguished only by which runtime it runs against.

### 4.2 Load (`jmeter/tests/script-race-lab.jmx`)

Follows `jmeter/tests/workflow-test.jmx`: User Defined Variables for host/port/domain, a thread group
of **30 threads, ramp-up 0, 1 loop**, `POST` start → JSON extractor for `id` → `GET .../functions/state`
inside a While controller polling until status is terminal, and a Response Assertion that fails the
sample on `"status":"F"`. Results land in `jmeter/results/` as the existing plan does.

JMeter carries the real load profile; the xUnit test carries the assertion that survives in CI. Both
are needed because neither alone gives both properties.

## 5. Run protocol

1. Generate and publish: `python3 core/Workflows/script-race-lab/build-script-race-lab.py --nonce <N>`,
   then `npm run validate` and publish the domain to the runtime.
2. **Pre-fix run:** build/run the runtime from `master`, restart it, publish, then run the JMeter plan
   and the xUnit race test. Expect `F` instances with `Instance:100030`.
3. **Fixed run:** build/run the runtime from `fix/script-alc-double-compile-race`, restart, publish the
   same nonce, re-run both. Expect all `C`.
4. Record both outcomes in `vnext/docs/superpowers/` alongside the runtime spec.

The publish mechanism for the manually-run local runtime on port 4201 is confirmed as the first
implementation step. The integration suite does not need it — `VNextTestEnvironment.EnableDomainPublish`
already uploads the domain through the SDK publisher during `InitializeAsync`.

## 6. Risks and limits

- **The race is probabilistic.** If a run does not trigger it, the two knobs are N (thread/task count)
  and the emit cost of `RaceOutputMapping.csx`. Both are single-value changes; neither requires
  restructuring the flow.
- **The evaluator cache is per process.** Locally that is one process, so a triggered race is
  unambiguous. Across replicas each process races independently, which makes a multi-replica run a
  weaker, not stronger, signal — measure on a single instance.
- **One cold window per nonce per process.** After the first successful compile the entry is warm for
  the process lifetime, so a second run against the same nonce and the same process proves nothing.
  Bump the nonce or restart between runs.
- **The lab flow is a fixture, not an example of good design.** The output mapping is deliberately
  oversized. It is tagged `integration-test` / `script-race-lab` like the other fixtures so it is not
  mistaken for reference material.

## 7. Decisions log

- **Purpose-built flow over an existing one.** Rejected `subflow-orchestration`,
  `future-pay/loan-disbursement` and `contract-signing`: each needs multiple steps before the subflow
  opens, which makes 30-way completion clustering unreliable and the load plan complex. §2.
- **Nonce in the mapping source over restart-only.** The cache key is the source hash, so a stamped
  nonce buys a cold key for the price of one line, and restart remains available.
- **Helper on the parent, not the child.** `SubflowOutputMappingService` compiles with
  `parentWorkflow.Scripts`; a helper declared on the child would leave `loadContext` null on the path
  under test and the race could not occur.
- **Both JMeter and xUnit.** JMeter alone gives no CI assertion; xUnit alone gives no load-tool
  artifact. The cost of the second one is one file.
- **No sabotage flag in production code.** The pre-fix/fixed comparison is made by building the two
  runtimes, not by adding a switch that disables the fix.
- **No Tasks component.** The mapping's own data write is the evidence; a counter task would add a
  component without adding proof. §3.
