# SubflowOrchestration — integration tests

Two test classes over the same three-level reference chain
(`subflow-orchestration-parent` → `-child` → `-grandchild`, domain `core`).

| Class | What it checks |
|---|---|
| `SubflowOrchestrationTests` | The chain itself: the collect gate, subflow start, the full lifecycle unwind, the parent's `$self` shared transition, `updateData` data-only short-circuit, cancel. |
| `SubStateRelayTests` | The parent's **`effectiveState`** — how far a descendant's state travels up the chain, and the ordering guard that protects it. |

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
