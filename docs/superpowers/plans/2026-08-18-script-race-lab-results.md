# script-race-lab — Measurement Results

**Date:** 2026-08-18
**Fixture:** `script-race-lab` (parent + child, both fully automatic; parent declares `scripts.helpers`)
**Harness:** `api-tests/script-race-lab/race-load.py --parallel 30` (stdlib driver; JMeter is not installed on this machine, so `jmeter/tests/script-race-lab.jmx` was authored but not executed)
**Runtime:** local orchestration host on `http://localhost:4201`, single process
**Constant across every run:** `--filler 60`, `--parallel 30` unless stated

## Verdict

The reproduction is confirmed on the pre-fix runtime, and it is **broader than the original report**:
the same cold-compile race fires at three different script call sites, with three different
consequences — one of them silent.

| Run | Runtime | Version / nonce | Cache at run start | Result |
|---|---|---|---|---|
| A | master (pre-fix) | 1.0.2 / nonce 2 | **cold, incl. the auto-transition rule** | **28/30 stranded `B`** in `race-initial`, 1 `C`, **zero incidents** |
| B | master (pre-fix) | 1.0.3 / nonce 3 | rule warm, **mapping cold** | **15/30 `F`** with the full signature, 15 `C` |
| C | master (pre-fix) | 1.0.4 / nonce 4, `--parallel 1` | mapping cold | **1/1 `C`** in 2.9 s — control |
| D | fix branch | 1.0.1 / nonce 1 | **warm** (smoke test compiled it first) | 156/156 `C` — see caveat below |
| E | fix branch | pending | cold, 30-way as first touch | **not yet run** |

## Run A — the silent failure mode

30 starts, dispatch spread 1.7 ms, accept window 794 ms. After the 180 s budget: 28 instances still
`B` in `race-initial`, one completed.

The stranded instances carry **no incident, no active correlation, and `modifiedAt == createdAt`** —
they were created (pre-positioned into `race-initial`, flipped `B` by the accept) and never written
to again. Only **1** child instance exists for this version, so 29 of 30 parents never started their
subflow at all.

The first script the pipeline compiles here is the auto transition's rule (`AlwaysTrueRule.csx`, step
90). One caller won that compile; the other 29 lost it and their work never committed. Nothing
surfaced: no `F`, no incident, no error code. An operator watching this flow sees instances that are
simply Busy forever.

This is worse than the reported symptom, because the reported symptom is at least visible.

## Run B — the reported failure mode, at two call sites

With the rule now warm and only the mapping cold, the race moves downstream and faults 15 of 30
parents. Two distinct error codes appear, from two distinct compilations:

```
Instance:100030 | SubFlow    | race-subflow |                       |
  SubFlow output mapping failed for parent instance '…':
  Could not load file or assembly 'Script_27968EC63254FC85, Version=0.0.0.0, …'.
  Assembly with same name is already loaded

Instance:100023 | PostCommit | race-subflow | auto-race-to-subflow  |
  SubFlow 'script-race-lab-child' input mapping failed:
  Could not load file or assembly 'Script_A16C22313A945CB8, Version=0.0.0.0, …'.
  Assembly with same name is already loaded
```

`Instance:100030` is the originally reported fault. `Instance:100023` — the subflow **input** mapping,
compiled when the parent enters the SubFlow state — was not in the original report and fails the
parent just as permanently.

**Independent confirmation that this host is the pre-fix build:** both assembly names are 16 hex
characters, i.e. master's `Script_{cacheKey[..16]}`. The fix widens that to the full cache key, so a
fixed host cannot produce a 16-character script assembly name.

## Run C — the control

Same runtime, same cold mapping, **one** start: completes in 2.9 s. The fixture is sound; the failure
is purely a function of concurrent first-compiles. Nothing about the flow definition is broken.

## Run D and the caveat that forced Run E

The fixed runtime completed 156/156 instances across five 30-way runs and one smoke test — but every
one of those runs was at nonce 1, and the **smoke test compiled that source first**. The compile cache
is process-lifetime, so those runs found a warm entry and never entered the race window at all. The
result is real but proves far less than it appears: it shows the fixed runtime handles 30-way
concurrency on warm scripts, not that it survives a cold one.

Run E is therefore required, and must mirror Run B exactly: a fresh nonce whose **first** touch is the
30-way load run, with no smoke test in between. Expected outcome: 30/30 `C`, no fault, and — if any
diagnostic surfaces — assembly names carrying the full cache key rather than 16 characters.

## Findings worth carrying back to the runtime repo

1. **The race is not confined to subflow output mapping.** Any script compiled for the first time
   under concurrency in a helper-declaring flow is exposed: the auto-transition rule (Run A), the
   subflow input mapping and the subflow output mapping (Run B). The fix is at the right layer —
   `CSharpEvaluator` — which is why it covers all three; but the incident report and any release note
   should not describe this as an output-mapping bug.
2. **The auto-transition-rule variant strands instances with no diagnostic at all.** No `F`, no
   incident, `modifiedAt` untouched. Whatever path swallows that failure is worth a separate look:
   an instance that cannot be distinguished from a healthy Busy one is invisible to monitoring.
3. **A script's own `using` directives are discarded** — `CompileAndLoad` calls `WithUsings(...)`,
   replacing them with `ScriptEngine.DefaultUsings` plus the helper namespaces. `System.Text` is not
   in that set, so `StringBuilder` in a mapping fails with `CS0246` — surfacing only as a faulted
   instance's incident, never at validate time. This cost a debug cycle while building the fixture and
   will cost every script author one.
4. **`definitions/publish` answers 409 for the same key+version even when the content differs.**
   A changed mapping published under an unchanged version never reaches the runtime, silently. The
   generator now derives `--version` from `--nonce` for this reason.
