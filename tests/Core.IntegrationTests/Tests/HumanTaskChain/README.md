# human-task-chain

## What it checks

The `human-task` domain function lists a waiting task **under the root's identity** while taking its
**authorization decision and its text from the leaf** — however many SubFlow levels and domain
boundaries separate the two. Plus the invariant that makes the list correct in the first place: when
a blocking SubFlow finishes, the parent's whole effective projection resets, so the finished task
leaves the list and a long-poller wakes up.

## Why it exists

`GET /{domain}/functions/human-task` is what morph-idm-api fans out over every registered domain to
build a banker's task list. Before this scenario it had **no integration test of any kind**, while
carrying an authorization filter, a subflow descent and an unbounded fan-out.

Four defects were found and fixed in the runtime change this scenario was written for (vnext,
2026-09-17):

| Defect | Symptom |
|---|---|
| `EffectiveStateSubType` had no reset writer anywhere | A parent whose child had been in a Human state was offered as an open task **forever**, to every caller, on an instance that had already moved on |
| The reset that did exist was not guarded by `!HasActiveSubFlow` | With two open correlations, the state half fell back to the parent while the status half still described the surviving child |
| The descent went exactly **one** level and resolved the child's definition against the **caller's** domain | A task two levels down was authorized against the wrong state; a cross-domain child resolved to nothing and silently removed its whole instance from the list |
| A level cancelled mid-subflow kept a live child's `'A'` in the raw `EffectiveStatus` | Cancelled cases were filterable, and would have been listable, as open work |

## The chain

```
ht-a (core, F) ─▶ ht-b (core, S) ─▶ ht-c (core, S) ─▶ ht-d (partner, S) ─▶ ht-e (credit, S) ─▶ ht-f (credit, S)
```

Depth is **data, not definition**. The start payload carries `hops`; every descent decrements it and
the `descend` rule fires only while it is positive, so one family covers every shape:

| `hops` | Leaf | What it exercises |
|---:|---|---|
| 0 | `ht-a` | the root is its own leaf — the shape an always-descend implementation gets wrong |
| 1 | `ht-b` | one level — used by the projection-reset tests |
| 2 | `ht-c` | **Senaryo 1**: three levels, one domain |
| 3 | `ht-d` | **Senaryo 2**: one domain boundary |
| 5 | `ht-f` | **Senaryo 3**: two boundaries, the second crossed by the *partner* runtime |

Every level writes its **own** `humanTask.title`. That is the critical part: a test asserting
`title == "HT-F step"` proves the text came from the leaf and not from the root's own data, which is
exactly what the old implementation got wrong.

The leaf's `approve` transition grants only `ht-approver`, so a caller holding a different role must
not see the row — which proves the filter evaluated the **leaf's** transition set.

The components are generated; edit the generator, not the JSON:

```bash
python3 api-tests/human-task-chain/build-human-task-chain.py
python3 labs/cross-domain/encode-scripts.py core/Workflows/human-task-chain \
    partner/Workflows/human-task-chain credit/Workflows/human-task-chain
```

## How to run

Senaryo 3 needs three business domains, so this scenario extended the cross-domain lab with a fourth
domain, `credit` (offset 20 → `:4221`).

```bash
# vnext runtime images from the working tree, then the lab
cd ../vnext-example
bash labs/cross-domain/lab.sh images
bash labs/cross-domain/lab.sh up          # core :4201  partner :4211  credit :4221  discovery :4231

dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~HumanTaskChain" -v minimal
```

`test.runsettings` carries `VNEXT_PARTNER_BASE_URL` and `VNEXT_CREDIT_BASE_URL`. When either is
unset the scenario that needs it **skips** rather than fails, so the same suite still runs against a
single-domain stack — Senaryo 1 and both projection-reset tests need only `core`.

## The client's side of the path

This suite proves one domain's answer. The path a client actually takes — morph-idm-api reading
discovery's `domain-list` and merging every domain's answer — is covered by
[`api-tests/human-task-chain/`](../../../../api-tests/human-task-chain/README.md): a Python check
(20 assertions) and a hand-driven `.http` walkthrough, both against the same lab. That README also
carries the two discovery-cache traps that make a working feature look broken.

## Pass criterion

- Senaryo 1/2/3: exactly one row for the started root, `workflow == "ht-a"`, and `title` equal to the
  **leaf's** text (`HT-C` / `HT-D` / `HT-F step`).
- A caller without `ht-approver` gets no row for that instance.
- After approving through the root, the instance **leaves** the list, and a long-poll held on the
  pre-completion ETag turns from `304` into `200`.

## Several human states in one flow

A definition can declare several human states, and two instances of it can be waiting in different
ones at the same time. The definition says nothing about which of them matters — **only the
instance's effective position does**:

| Instance | Where it rests | Listed with |
|---|---|---|
| started with `hops = 0` | `ht-a-human`, the root's own state | `HT-A step` |
| started with `hops = 2` | `ht-c-human`, two levels down | `HT-C step` |

Both are rows of the same workflow, addressed by their own root, carrying different text.
`TwoInstancesOfOneFlowAreListedByWhereEachIsEffectivelyWaiting` pins exactly that.

## The spawned SubProcess

`ht-a` also declares `ht-a-spawn`, a plain intermediate state (`stateType: 2`, no `subFlow` block)
reached when the start payload has `mode == "process"`. Its outgoing `ht-a-spawned` transition
carries an `onExecutionTasks` entry for `ht-a-spawn-process` — a `SubProcessTask` (task `type: "14"`)
targeting `ht-d` (partner) — whose mapping (`SpawnProcessMapping.csx`) reads `processHops` off the
instance data, calls `sub.SetSync(false)` to keep the spawn fire-and-forget, and hands the executor
the same `hops`/`testId`/`humanTask` payload the old subflow mapping built. `processHops` drives how
far that SubProcess descends on its own, the same way `hops` drives the root's chain.

A state can only start a SubFlow (`subFlow.type: "S"`); a SubProcess is started by its own task, not
by the state. A `state.subFlow.type: "P"` block — the shape this scenario used before — is rejected
at publish by the runtime's `WorkflowValidator`, so the SubProcessTask wiring above is the only valid
way to fire a SubProcess today.

A SubProcess is an **independent flow**, so it is listed by the domain that owns it and behaves like
a root there:

```
core                          partner                      credit
ht-a  (root, ht-a-human)      ht-d  (SubProcess)  ─▶  ht-e  ─▶  ht-f
  └─ core's row: "HT-A step"    └─ partner's row: "HT-F step"
```

Two separate rows, in two separate domains' answers — morph-idm fans out over both and shows both.
The SubProcess's row is addressed by its **own `id`**, never by `instanceId`'s usual business key:
`SubflowStarter` copies the parent's `Key` onto the child, so following that key would land a client
on the root instead. `ASpawnedSubProcessIsListedOnItsOwnAndDescendsLikeARoot` pins both halves — the
identity and the fact that the SubProcess descended two further levels to get its text.

## Authorization

A wrong answer here is a banker seeing another banker's case, and the failure is invisible in the
response by construction — an unauthorized row is simply absent. Every authorization test is
therefore a **pair**: something the caller must see and something the same caller must not, from the
same data in the same call. A test that only asserts absence cannot tell "correctly filtered" from
"the descent dropped it", which is exactly how this scenario's first negative test turned out to be
vacuous.

### The gate is the leaf state's `queryRoles`

The list answers *"which human tasks am I responsible for?"* — a **visibility** question. Which button
is offered is settled later, when the client opens the instance. Each level's human state therefore
declares `queryRoles`, and those are what the list is decided by:

| Grant | Pins |
|---|---|
| `ht-approver` allow | the broad role, granted at **every** level |
| `{level}-approver` allow | **only** that level — a caller holding just `ht-f-approver` sees a chain resting on `ht-f` and not one resting on `ht-c`, which is what proves the decision came from the LEAF |
| `ht-blocked` deny | DENY refuses whatever else the caller carries |

The levels still author transition `roles` too, with the same three grants. They no longer influence
the list — they govern what the client is **offered on open**, which is the other half of the split
and is asserted on the state function instead.

### The parent's overrides are live, and each is asserted on its own surface

`ht-d`'s subflow state overrides `ht-e`:

```json
"overrides": {
  "states":      { "ht-e-human":   { "queryRoles": [ xd-override-only allow, ht-blocked deny ] } },
  "transitions": { "ht-e-approve": { "roles":      [ xd-override-only allow ] } }
}
```

Authored on the `ht-d → ht-e` link for two reasons: no other test rests on `ht-e`, so no existing
scenario changes meaning; and that link crosses a **domain boundary** (partner → credit), so the
stamp has to survive a cross-domain start to be readable at the leaf at all.

| Override | Asserted on | Test |
|---|---|---|
| `states` | the **human-task list** — `xd-override-only` sees the chain resting on `ht-e`, `ht-approver` (which `ht-e` itself grants) does not, and a level the parent did not override still answers to its own grants | `AParentsStampedStateOverrideNarrowsTheChildsVisibility` |
| `states` + DENY | the list — `xd-override-only,ht-blocked` is refused | `ADenyInTheStampedStateOverrideRefuses` |
| `transitions` | the **overriding parent's state function** — the roles `ht-e` itself grants are no longer offered `ht-e-approve`, while the identical un-overridden shape one level up still offers `ht-d-approve` to exactly those roles | `AParentsTransitionOverrideStillGovernsWhatTheLeafOffers` |

Splitting the two across surfaces is the point: visibility is the list's question, actionability is
the screen's, and asserting both on one surface would hide a regression in the other.

**Both halves of one `overrides` block now behave the same on every surface.** The first version of
this change applied the transition override on the state function's subflow bubbling but not the
state override, so a caller holding only `xd-override-only` was refused at `ht-d` while the
transition narrowing was clearly in force. The stamped state override is now read inside
`TransitionAuthorizationManager.IsQueryAllowedAsync`, the single queryRoles gate behind every read
surface, so the parent's narrowing applies wherever the child is reached from. Measured on `ht-d`:

```
x-roles: xd-override-only              → state=ht-e-human, transitions=[ht-e-approve]
x-roles: ht-approver                   → state=ht-d-subflow, transitions=[]     (replaced, not merged)
x-roles: ht-e-approver                 → state=ht-d-subflow, transitions=[]
x-roles: xd-override-only,ht-blocked   → state=ht-d-subflow, transitions=[]     (DENY wins)
```

### DENY refuses whatever else the caller carries

`authorized = AllowGroup AND DenyGroup`: allows OR among themselves, denies AND among themselves, and
the deny group is evaluated first. One breached deny refuses outright.

This reverses what this scenario pinned a day earlier. The rule used to be applied per caller role
inside a loop that returned on the first role that was allowed, so a deny for role B was never reached
once role A had matched an allow — `[approver, blocked]` passed, and a deny grant was unusable as a
block. `ADenyGrantRefusesWhateverElseTheCallerCarries` pins the new rule in both orders, because
"evaluated first" is what makes the answer independent of how the caller's roles happen to be listed.

### The response cache is part of the authorization surface

`TheResponseCacheDoesNotServeOneCallersListToAnother` is the only test here that deliberately does
**not** send `X-VNext-Cache-Override`. Every other one does, so none of them exercises the cached
path at all — and this is the one place where a cache-key mistake stops being a stale answer and
becomes a data leak. It warms the entry for the broad role, then reads as a different caller inside
the TTL, then reads again as the first caller to prove the second call neither evicted nor poisoned
the entry.

## Why some rows have no title

A row's text is whatever the **leaf** carries in `humanTask.title` / `.description`. A flow that
never writes that block produces a correctly-listed row with empty text — `cross-domain-lab`'s
`xd-parent` is the example in this repo: its `xd-child` has a `subType: 6` state (`child-review`) and
no `humanTask` data anywhere, so the rows are real waiting tasks with nothing to display. An empty
title means "the flow wrote no task text", never "the descent failed" — a failed descent drops the
row and is counted (`vnext.humantask.dropped`, `WorkflowLogs` 20450-20456).

## Polling and the response cache

Every wait loop and every assertion sends **`X-VNext-Cache-Override: true`**. The function's response
cache has a 60 s TTL and no validation query, so a test that polls through it waits out the TTL on a
pre-change answer and then reports a wrong list rather than a stale one — which is a different, and
misleading, failure. Reading fresh is also what a client is expected to do at a moment it cannot
tolerate the TTL, so the header is exercised end to end here rather than only in unit tests.

## Known limits

- The chain uses no HTTP tasks, so MockLab is not required.
- `hops` is trusted as given; there is no upper bound in the definition. The runtime's own
  `HumanTaskFunction:MaxDescentDepth` (default 10) is what bounds the descent, and a chain longer
  than that is reported as unresolved rather than as "no task here".
- Senaryo 3 is the only test that needs `credit`. If the lab is brought up without it, that one test
  skips and the other five still prove the same-domain and one-boundary paths.
- **The SubProcess test no longer needs to start the root asynchronously.** The old shape —
  `ht-a-spawn` as a state-level SubProcess (`subFlow.type: "P"`) followed by an automatic transition
  — could not be started with `sync=true`: the post-commit `ContinueParent` continuation re-entered
  the pipeline with `IsPreReserved = false` while the instance was still Busy from the very stage that
  continuation belonged to, so admission rejected it with `conflict.Instance:100031` and the parent
  was stranded in the spawn state with an orphaned child. That shape is now rejected outright at
  publish time by the runtime's `WorkflowValidator` (a state may only start a SubFlow, never a
  SubProcess), and the fix — spawning `ht-d` through the `ht-a-spawn-process` SubProcessTask on the
  `ht-a-spawned` transition instead of through the state — does not re-enter the pipeline the same
  way, so the sync-path 409 no longer applies and the async-only workaround is no longer needed.
