# SubflowOrchestration — integration tests

Two test classes over the same three-level reference chain
(`subflow-orchestration-parent` → `-child` → `-grandchild`, domain `core`).

| Class | What it checks |
|---|---|
| `SubflowOrchestrationTests` | The chain itself: the collect gate, subflow start, the full lifecycle unwind, the parent's `$self` shared transition, `updateData` data-only short-circuit, cancel. |
| `SubStateRelayTests` | The parent's **`effectiveState`** — how far a descendant's state travels up the chain, and the ordering guard that protects it. |
| `SubflowStatusProjectionTests` | The **status** half of the same projection, the conditional GET that carries it, and the **interaction** signal a descendant raises: what a long-polling client observes from its own 202 until the chain completes. |

---

## `SubStateRelayTests`

### What it checks

A descendant's state change reaches **every** ancestor's `effectiveState`, with the correct value,
and a late or duplicate delivery can never move it backwards.

### Why it exists

`InstanceSubStateChangedEvent` used to reach the parent only through
outbox → Dapr pub/sub → Inbox worker → `sub/state`. On preprod (2026-09-08, instance `6aa217b9…`)
that path took **6 min 30 s** when the Dapr Redis pub/sub connection pool was exhausted. Council
session `2026-09-08-substate-postcommit-relay` (Chair-approved 2026-09-09) added a post-commit relay
for the event, and the implementation extended it in two ways that these tests exist to pin:

1. **Depth ≥ 2.** The relay in `TransitionRunner` only sees events raised in *that* hop's unit of
   work. When a parent is itself a subflow, applying a state change raises the grandparent's event
   inside `SubflowStateService`'s own UoW — invisible to the runner. So that service became a
   **second relay call site**, and the fast path now walks the whole ancestor chain.
2. **The ordering guard became sound.** Both delivery paths land in the same service, so duplicates
   are routine. `SubflowStateService` was the only parent-mutation path that took no lock, which made
   its `SubFlowStateChangedAt` read-check-write a real TOCTOU: two deliveries could both read the
   same stamp, both pass the check, and the OLDER one land last. It now takes the same per-sub-item
   lock (`vnext:{domain}:{flow}:{parentId}:sub:{subId:N}`) the three terminal paths take.

### The chain and the critical step

```
parent (parent-subflow-state)          currentState  = parent-subflow-state
  └─ child (child-manual-state)        effectiveState = child-manual-state      ← one hop
       └─ grandchild (grandchild-initial)
                                        effectiveState = grandchild-initial     ← TWO hops, the critical one
```

`currentState` and `effectiveState` are different fields. The parent never leaves
`parent-subflow-state`; only `effectiveState` follows the chain down.

`EffectiveState_FollowsTheGrandchild_AcrossTwoLevels` is the critical test: the value has to cross
two propagation hops. `AStaleSubStateDelivery_DoesNotDowngradeTheParent` is the guard's test — it
posts to the internal `sub/state` endpoint by hand, because that is the only way to control
`changedAt`, which is what the guard compares on.

### What is deliberately NOT asserted

**Latency.** These tests assert the *guarantees*; the propagation would eventually be correct through
the outbox path alone, just slower. A single-sample timing assertion in a functional test is noise,
not evidence. The fast path is verified from APM instead — see below.

### How to run

Prerequisites: the runtime under test built from the `vnext` working tree and running
(`cd ../vnext/etc/docker && ./run-docker.sh up core`), MockLab up (`docker compose up -d` in this
repo's root). `VNEXT_BASE_URL` is already committed in `test.runsettings`.

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings --filter "FullyQualifiedName~SubStateRelayTests" -v minimal
```

### Pass criterion

All 5 green. Any failure of the two stale/fresh tests means the ordering guard changed behaviour and
must be investigated before merge — those are the ones protecting against a silent downgrade.

### Verifying the fast path itself (APM)

The relay is visible as a `PostCommit.EventRelay` span. Against the local stack
(Elasticsearch `http://localhost:9200`, Kibana `http://localhost:5601` → Observability → APM):

```bash
curl -s "http://localhost:9200/traces-apm*/_search" -H 'content-type: application/json' -d '{
  "size": 20, "sort": [{"@timestamp": "desc"}],
  "query": {"bool": {"filter": [{"term": {"span.name": "PostCommit.EventRelay"}}]}},
  "_source": ["@timestamp","trace.id","span.id","parent.id","span.duration.us","labels"]}'
```

Expected tags: `vnext_event_name=InstanceSubStateChangedEvent`, `vnext_relay_route=local|remote`,
`vnext_delivery_role=relay`, `vnext_relay_outcome=relayed`.

The depth ≥ 2 walk shows up as relay spans **nested inside each other** within one trace — each level
applying the change and handing the next level's event straight back to the dispatcher. Measured on
the local stack, 2026-09-09 (three-level `chain-busy` flow):

```
SubFlow.Start/core/chain-busy-leaf
└─ PostCommit.EventRelay                        14.6 ms  relayed
   └─ SubFlow.StateChange/core/chain-busy-middle  14.2 ms
      └─ PostCommit.EventRelay                    6.7 ms  relayed
         └─ SubFlow.StateChange/core/chain-busy-root  6.3 ms
```

Same-domain relay hops measured 6.4–14.6 ms in-process. Note these are single-machine dev numbers
with no broker latency or replica contention; they are not a production percentile.

### Known limits

- The depth ≥ 2 test would also pass on the outbox-only path, just more slowly — it is a correctness
  and regression guard, not proof that the relay fired. The APM span above is that proof.
- `InstancesCorrelations.ModifiedAt` is **not** refreshed when only the sub-state columns are
  written, so it cannot be used as an "applied at" timestamp for a DB-side latency probe. Use the
  APM spans.

---

## `SubflowStatusProjectionTests`

### What it checks

A client polling a parent sees the chain's **status** move the moment the chain moves — on the
unconditional poll and, above all, on the conditional one it is actually long-polling with.

### Why it exists

Preprod, 2026-09-10, trace `0dbc92d9a7b2015c6daef80fff232272` (instance
`7212ed29-09da-48f6-a645-0ba97353a652`, runtime 0.0.92): an async transition on a parent inside an
active subflow answered 202, and the client's next `GET …/functions/state` returned the
**pre-transition body** — `status: "A"`, the old state, and the transition it had just accepted
still listed. The client followed that body's `hasView: true`, asked for the view, and got a 404
(`View definition not found for state kyc-main-form`). The served body had been built **142 seconds**
earlier.

Two defects behind it:

1. **Nothing the accept writes was visible to the poller's validation.** The accept reserves the
   chain correctly — the leaf goes Active → Busy under the status lock before the 202 commits — but
   a parent holding an open SubFlow correlation is Busy for that subflow's whole lifetime by design,
   its correlation rows are untouched, and `SubFlowStateChangedAt` moves only when the child reports
   a state change. The parent's state fingerprint was therefore bit-identical before and after, and
   the active-subflow snapshot cached against it (`#928`) stayed valid across exactly the transition
   it must not survive. Fixed by `Instance.EffectiveStatus` — the client-visible status, folded into
   `InstanceStateFingerprint` and the ETag material, maintained by the busy walk on the way down.
2. **The release had no way up when the state did not change.** A `$self` shared transition (here
   `shared-child-mark`) brings the child to rest in the state it started in; `ChangeState` arms
   nothing when previous == new, so the rest point published nothing and every ancestor the accept
   had stamped Busy stayed Busy — with no later event to correct it. The settlement's own
   Busy→Active CAS is now a second reason to publish, and the notification carries the sub-item's
   status plus a per-instance sequence that orders it without a wall clock.

The TTL that was supposed to bound the staleness does not exist in practice: Aether's Dapr cache
provider truncates a sub-second TTL to `0` seconds and then omits the metadata entirely, so the
"500 ms" snapshot is written with no expiry at all. Correctness here rests on the fingerprint, not
on a TTL.

### The chain and the critical step

```
parent  parent-subflow-state   (SubFlow state, Busy for the child's lifetime)
 └─ child  child-manual-state  (Active, waiting for a human)   ← every test starts here
     ├─ proceed-to-subflow  → child-subflow-state → grandchild grandchild-initial
     └─ shared-child-mark   → $self, same state, status B→A    ← the status-only rest point
```

The critical step is **the poll immediately after the 202**. A warm cache entry is the precondition,
not an accident: `WarmTheCacheAndTakeTheEtagAsync` makes sure a response built before the accept is
sitting in the state-function cache, which is the only condition under which the defect appears.

### How to run

Prerequisites: the runtime under test built from the `vnext` working tree and running
(`cd ../vnext/etc/docker && ./run-docker.sh up core`). MockLab is **not** needed — these flows have
no HTTP tasks. `VNEXT_BASE_URL` is already committed in `test.runsettings`.

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings --filter "FullyQualifiedName~SubflowStatusProjectionTests" -v minimal
```

### Pass criterion

All 11 green. Five of them are the regression guards and were verified red against a runtime built
from `master` (`e1205b82`) on 2026-09-11, immediately before and after the same test run on the
fixed runtime:

| Test | On `master` |
|---|---|
| `ThePollRightAfterAn202_SeesBusy_AndNoLongerOffersTheAcceptedTransition` | **red** — `Expected: "B", Actual: "A"` (the preprod defect, reproduced locally) |
| `AClientHoldingTheIdleEtag_IsNotToldNotModified_AfterItAcceptsATransition` | **red** — `Expected: OK, Actual: NotModified` |
| `AStatusOnlyEpisode_MovesTheEtagForALongPoller` | **red** — `Expected: OK, Actual: NotModified` |
| `AcknowledgingOnTheParent_ResumesThePausedChild` | **red** — the chain never comes back to Active for a poller |
| `TheFallbackWindow_ResumesTheChain_WhenNobodyAcknowledges` | **red** — same |

### The interaction signal

Four of the eleven cover `interaction.longPoll` from the seat that matters: the client polls the
**parent** and never learns which level it is talking to, so a signal declared two levels down has
to arrive in the parent's own body with an ack href addressed to the instance the client is
holding.

`child-interaction-state` was added to the child flow for this (`enter-interaction` from
`child-manual-state`, `leave-interaction` back; `terminate: true`, `fallbackTimeoutSeconds: 10`).
Both flows were patch-bumped to 1.0.1 — publishing is version-immutable — and the parent's subflow
reference moved with them.

What they pin:

- The signal is **independent of the status**. The pipeline pauses on entry, so the chain is Busy
  while the acknowledgement is pending — and Busy is exactly when the client must stop polling and
  render, not when it should keep waiting. One single response is asserted for `terminateLongPoll`,
  the fallback window, the ack href and `status: "B"`.
- A long-poller asleep on the idle ETag is **woken** by the interaction state. A 304 there would
  make the client wait through the very state whose purpose is to tell it to stop.
- Acknowledging **on the parent** resumes the paused child two levels down.
- The fallback window resumes it when nobody acknowledges — a client that walked away cannot
  strand the chain.

The last two were red on `master`, for the reason this whole change exists: the child resumes and
settles **in the state it was already in**, so nothing was published, the parent's fingerprint never
moved, and the poller kept being served a Busy body with no later event able to correct it. The
long-poll interaction path is therefore the most user-visible instance of the missing release.

### What is deliberately NOT asserted

- **The column.** `EffectiveStatus` is fingerprint material and is never served; asserting it would
  pin an implementation detail the runtime intends to keep private. Everything here is asserted
  through what a client can see: the state function's `status`, its `transitions` list and its ETag.
- **Latency.** Same reasoning as `SubStateRelayTests`: these are guarantees, not percentiles.

### Known limits

- The two tests that assert only "the chain returns to Active"
  (`TheChainReturnsToActive_…`, `AnEpisodeThatChangesNoState_StillReleasesTheChain`) also pass on
  `master`, because the observed status is served from a LIVE descent whenever the cache misses.
  They pin the behaviour, not the fix — the ETag variants are the regression guards.
- `RunAcceptedAsync` cannot be used on a chain: it waits for the addressed instance to leave Busy,
  and a parent with an open correlation never does. These tests use a local `AcceptAsync` plus an
  observed-state/status wait.
