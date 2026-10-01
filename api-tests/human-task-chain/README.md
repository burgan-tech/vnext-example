# human-task-chain — morph-idm-api end to end

## What this covers

`tests/Core.IntegrationTests/Tests/HumanTaskChain` proves **one domain's** answer. This proves the
**client's** answer — the whole path, together:

```
client → morph-idm-api → discovery domain-list → per-domain human-task → leaf descent → leaf
```

It is the only place that path is exercised as a whole, and the only place the aggregation's own
behaviour (domain fan-out, per-domain failure isolation, `domainName` attribution) is checked.

| File | What it is |
|---|---|
| `morph-idm-aggregation-test.py` | Automated, 20 checks, exit code 0/1. Standard library only. |
| `morph-idm-aggregation.http` | The same path hop by hop, for driving by hand when something is wrong. |
| `build-human-task-chain.py` | Generates the scenario's six workflow components. See the [scenario README](../../tests/Core.IntegrationTests/Tests/HumanTaskChain/README.md). |

## Prerequisites

### 1. The four-domain lab

```bash
bash labs/cross-domain/lab.sh images   # runtime images from ../vnext working tree
bash labs/cross-domain/lab.sh up       # core :4201  partner :4211  credit :4221  discovery :4231
```

### 2. morph-idm-api **on the lab's Docker network**

This is not a convenience — it is the only working topology. Discovery hands out
**container-internal** base URLs (`http://vnext-app-core:5000`), because that is what the domains use
to reach each other over Dapr. A morph-idm running on the host cannot resolve those names and every
fan-out call dies with `nodename nor servname provided`; the endpoint still answers **`200` with an
empty list**, so the symptom looks like "no tasks", not like a broken deployment.

```bash
cd ../morph-idm-api/api
docker build -t morph-idm-api:lab -f Dockerfile .

docker run -d --name morph-idm-api --network vnext-development -p 5288:5000 \
  -e "Discovery__BaseUrl=http://vnext-app-discovery:5000" \
  -e "ConnectionStrings__DefaultConnection=Host=vnext-postgres;Port=5432;Database=mocklab;Username=postgres;Password=postgres" \
  morph-idm-api:lab
```

The committed `Discovery:BaseUrl` is already `http://localhost:4231`, which is right for a host-run
process; the override above is what the container needs. The connection string points at the lab's
Postgres — create the database once with
`docker exec vnext-postgres psql -U postgres -c "CREATE DATABASE mocklab;"`.

`Dockerfile` already passes `-p:SkipBuildUI=true`; a host build needs that flag explicitly
(`dotnet build BBT.IdmUtils.sln -p:SkipBuildUI=true`) or it fails on `npm run build`.

## Run

```bash
python3 api-tests/human-task-chain/morph-idm-aggregation-test.py \
    --core http://localhost:4201 \
    --partner http://localhost:4211 \
    --credit http://localhost:4221 \
    --discovery http://localhost:4231 \
    --idm http://localhost:5288
```

All five URLs default to the values above, so the bare command works against a standard lab.

## What it checks

| Step | Assertion |
|---|---|
| 1 | discovery's `domain-list` carries core, partner and credit |
| 2–3 | one chain started per scenario (`hops` 0, 2, 3, 5) plus one `mode=process` root that spawns a SubProcess into partner, all waited to their rest points |
| 4 | each row carries the **leaf's** title (`HT-A` / `HT-C` / `HT-D` / `HT-F step`) and the **root's** workflow |
| 5 | partner and credit list none of this run's roots — an **`S`** child is represented by its root, never listed twice — while partner **does** list the spawned **`P`** SubProcess on its own, addressed by its own `id` and carrying `HT-F step`, the text of the leaf it descended to |
| 6 | a caller holding another role sees none of the chain |
| 7 | the aggregation carries all four titles, every row names its `domainName`, every row is flagged `vnextTask` |
| 8 | the caller's context reaches every domain (`X-VNext-Cache-Override` changes a repeat read from a cache hit into a rebuild; `x-request-id` appears in the domain runtime's own log) and the domains' truncation comes back (body flag, named domains, and the same header on morph-idm's response) |

Step 5 is the one that would catch a selection predicate that started matching `S` children, or a
SubProcess addressed by the business `Key` it inherits from its parent (which would point a client
at the root, in another domain); step 4 catches a descent that stopped early or read the root's text.

**Cache:** every read sends `X-VNext-Cache-Override: true`. The response cache has a 60 s TTL and no
validation query, so polling through it waits out the TTL on a pre-change answer and then reports a
*wrong* list rather than a stale one — a far more misleading failure.

## Pass criterion

`PASSED — 30 checks`, exit code 0. Last run 2026-09-18 against the four-domain lab: 30/30.

## Known environment traps

Both were hit while writing this and both look like "the feature is broken":

- **Discovery's bulk list and its per-domain entries can disagree.** `domain-list` is served from
  `vnext||custom:discovery:domains:active` in Redis while `domain-lookup` reads a per-domain key. A
  registration made while an older discovery package was installed does not invalidate the bulk key,
  so `domain-list` can answer with entries from another environment entirely while `domain-lookup`
  is correct. Symptom: morph-idm fans out to hostnames that do not exist. Fix in the lab:
  `docker exec vnext-redis redis-cli del 'vnext||custom:discovery:domains:active'`.
- **A negative lookup is cached with the same TTL as a positive one.** A domain that was down when
  first looked up stays invisible for the whole TTL (~17 h observed) even after it registers.
  Symptom: `lab.sh verify` reports `X NOT registered` while the registry clearly holds it. Same fix,
  on that domain's key.

- **A state-level SubProcess cannot be started with `sync=true`.** `?sync=true` on a `mode=process`
  start answers `409 conflict.Instance:100031` and leaves the root stranded Busy in `ht-a-spawn`
  with an orphaned child. The post-commit `ContinueParent` continuation re-enters the pipeline with
  `IsPreReserved = false` while the instance is still Busy from the stage that continuation belongs
  to; the async path escapes it only because a job re-entry sets that flag independently. Both the
  script and the `.http` walkthrough start that case asynchronously for this reason. Recorded in
  `TEST-SCENARIOS.md` § Bilinen Kapsam Açıkları.



## Fan-out bounds on both sides (2026-09-18)

The two repositories cap different things, and only their product meets the database. Measured on
this lab, 45-flow core domain, PostgreSQL `max_connections = 100`, **distinct** callers (each has
their own vNext cache key, so none of them is a cache hit and single-flight collapses nothing):

| Concurrent distinct callers | Before | After |
|---:|---|---|
| 20 | 13 → HTTP 500 (`53300 sorry, too many clients already`) | all 200 |
| 40 | 16 → HTTP 500 | all 200, peak 51 connections |
| 80 | — | all 200, peak 54 connections |

What changed, in order of effect:

- **vNext scans every flow in one statement on one connection.** All flows of a domain live in
  schemas of the same database, so the per-flow scans differed only in the `FROM` clause; running
  them as parallel branches bought a pooled connection each to do one sub-millisecond index-only
  scan. `EXPLAIN` over all 44 arms: `Index Only Scan` per arm, `Heap Fetches: 0`, execution 0.92 ms.
- **vNext bounds descents process-wide** (`HumanTaskFunction:MaxConcurrentDescents`).
  `FanoutParallelism` bounds one request; nothing bounded their product.
- **morph-idm's caps are in a different unit and cannot see this.** `GlobalMaxConcurrency` counts
  morph-idm's outbound calls; the scarce resource is vNext-side connections, and the descent hops
  vNext makes between domains are invisible to it entirely. Both sides need their own ceiling.

## Gap worth closing (morph-idm side)

vNext's row carries an additive `id` (the instance's own identifier) precisely because `instanceId`
is the business key and **is not unique** — a SubProcess child inherits its parent's key, so one case
can produce two rows with the same `instanceId`. `HumanTaskDto` does not map it, so the collision the
field exists to resolve is still invisible to the end client. Adding `Id` to the DTO and the
projection would close it; nothing else needs to change.
