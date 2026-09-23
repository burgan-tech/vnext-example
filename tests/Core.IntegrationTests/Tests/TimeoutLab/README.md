# Timeout Lab

The workflow-level `timeout`, end to end: published to the client while it is pending, and honoured
when it fires — including the case where the deadline comes from a parent's
`subFlow.overrides.timeout` rather than the instance's own definition.

| Class | What it checks |
|---|---|
| `TimeoutLabTests` | The state body's `timeout` block on a root flow and on a SubFlow child, and that the instance actually lands on the `target` it published. |

## Why it exists

Two holes, one fixture.

**The deadline was invisible.** The runtime armed a workflow timeout, persisted the exact instant on
the `InstanceJob` row, and fired it correctly — but no read surface carried it, so a client could not
draw a countdown or say "auto-cancels at HH:MM". That is the ask in
[vnext-client-sdk-core#59](https://github.com/burgan-tech/vnext-client-sdk-core/issues/59).

**A subflow override was accepted, stamped, and then ignored.** A parent can supply its child's
deadline through `subFlow.overrides.timeout`. That override was read exactly once — when the job was
armed. When the job fired, the handler read the **child's own** definition, found no timeout and
returned. A child declaring `"timeout": null` therefore had a deadline that was scheduled and could
never arrive. `subflow-orchestration` ships that exact shape and no test had ever covered it.

Both are now answered by one resolver (`InstanceMetadataExtensions.ResolveEffectiveTimeout`) that the
arm, the fire path and the state read all call — so the deadline a client is shown is, by
construction, the one the runtime will act on. These tests are what stops those three drifting apart
again.

## Why a new flow

Nothing in this repo exercised a workflow timeout at all, and both authored durations are `PT15M` —
far too long to watch inside a test run. The lab flows use `PT20S`.

> **Do not shorten `subflow-orchestration-parent.json`'s `PT15M`.** Now that the runtime honours the
> override, a short duration there would start cancelling that scenario's children mid-suite.

## The flows

```
timeout-lab-root                     timeout-lab-parent
  root-waiting ──(manual)──▶ ...       parent-initial ──(auto)──▶ parent-subflow
      │                                                              │  SubFlow S
      │  timeout PT20S                                               │  overrides.timeout PT20S
      ▼                                                              ▼
  root-timedout                                            timeout-lab-child
                                                             child-waiting   ("timeout": null)
                                                                 │
                                                                 ▼
                                                             child-timedout
```

Both waiting states are deliberately inert: nothing but the deadline moves the instance, so a failing
test has one possible cause.

## How to run

```bash
VNEXT_BASE_URL=http://localhost:4201 dotnet test tests/Core.IntegrationTests --filter "FullyQualifiedName~TimeoutLab"
```

Requires the flows to be published (`wf domain use core && wf sync`) against a locally built runtime.

## Success criteria

1. A parked instance's state body carries `timeout: { key, target, executeAtUtc }` with a future,
   `Z`-designated UTC instant.
2. When the deadline passes, the instance reaches exactly the `target` it published.
3. The block disappears once the instance is terminal — **without** waiting for the asynchronous
   `cancel-cleanup` chain to close the job row.
4. A child running under its parent's override reports **the override's** `key`/`target`, not its own
   (absent) definition, and is pulled to that target.
5. A parent does not inherit its child's deadline: the block describes the polled instance only.

## What it caught on its first run

The scenario failed the first time it was executed, and the failure was not in the fixture.

`CreateTransitionRecordStep` resolves the virtual `$timeout` key into the audit record's transition
key at **order 20**, through `Workflow.ResolveWellKnownKey` — which *throws*
`TimeoutNotConfiguredForWorkflowException` when the workflow declares no timeout of its own. For a
child running on a parent's override that is exactly the shape, so the pipeline died three steps
before `ApplyTimeoutStateStep` (38), where the fix lived.

`SetBusy` (19) had already committed by then, so the measured symptom was worse than "the deadline
does not fire": the child was left **Busy in its waiting state forever**, with its job row already
marked processed — no deadline and no way back. A fourth call site now resolves through the shared
effective-timeout resolver; `ResolveWellKnownKey` was left alone, since it is a definition-level
method with no instance in scope.

Evidence from the database rather than the test summary: `child-timedout` / `C`, audit key
`child-abandoned` (the **parent's** override key), fired ~54 ms after the armed `ExecuteAt`.

## Deliberately NOT asserted

- **That activity resets the deadline.** It does not. `timer.reset` is required by the schema and read
  nowhere in the runtime, so the duration is an absolute budget from instance start, whatever the
  definition declares. Tracked as `workflow-timeout-reset-not-implemented` in
  `vnext-meta/known-issues.json`.
- **Timing precision.** The waits allow generous slack over `PT20S`; this scenario proves the deadline
  fires and where it lands, not how punctual the scheduler is.
- **The parent's own resume behaviour** after the child times out — that is `subflow-orchestration`'s
  subject, not this one.
