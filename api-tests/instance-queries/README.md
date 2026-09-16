# Instance query regression (#933 / #934)

## Purpose

Verify that caller-controlled filter values remain literal JSON string values through
HTTP parsing, `AttributeConditionBuilder`, parameterized PostgreSQL JSONB containment,
and instance listing. Added 2026-09-15 for [vnext issue #934](https://github.com/burgan-tech/vnext/issues/934).

The script publishes a uniquely named task-free workflow, starts twelve instances through
the API (`start → ready/Active`), queries them, and checks the latest PostgreSQL rows.
All writes use the API. SQL is read-only. The critical path is instance-list filter
compilation and repository execution; no external HTTP task or MockLab is needed.

## Run

Use Python 3, Docker CLI, and a dedicated runtime built from the vnext working tree.
Run DbMigrator and publish that domain's system flows first. Do not target a released
runtime image or another team's domain. The script leaves its unique workflow/data for
evidence inspection and requires explicit connection parameters.

```bash
python3 api-tests/instance-queries/test_instance_queries.py \
  --base-url http://localhost:6201 --domain core \
  --pg-container issue934-postgres --database Issue934 \
  --output /tmp/issue934-results.json
```

## Assertions and interpretation

- Seven payloads × two paths (`name`, `profile.name`) × two operators (`eq`, `ne`)
  × two formats (GraphQL JSON, legacy): 56 exact-result-set assertions.
- Payloads cover quotes, backslashes, invalid JSON escapes, newline/tab, extra properties,
  duplicate keys, and SQL-looking text. Decoy records detect changed filter semantics.
- Five invalid-input/total-size assertions require HTTP 400-class rejection.
- Two PostgreSQL assertions require exact unchanged values/properties and all twelve
  instances Active.
- Eighteen value-limit assertions cover 1000 accepted / 1001 rejected, per-element
  membership/range limits, combined list lengths above 1000, logical/nested conditions,
  and aggregation envelopes. Total: 81 assertions. Any failure returns exit code 1.
- Every HTTP request records a trace ID; confirm the run in OpenObserve and PostgreSQL.

For the value-limit change alone, append `--value-limits-only` to run 25 checks. This
retains the malformed-input, total-size, and PostgreSQL assertions while omitting the
pre-existing injection/newline compatibility matrix.

## Baseline before wiring ValidateValue: 2026-09-15

Runtime `3000c6071c869f1b28c745231c04ee527a57fe7d`, branch
`feature/instance-data-performance`, isolated API `http://localhost:6201`:

- **59/63 assertions passed; four failed.** All four failures are legacy-format queries
  containing a newline: HTTP 400 `filter.unrecognizedFormat`. `LegacyFilterPattern` uses
  `.` without `RegexOptions.Singleline`. GraphQL JSON supports the same values correctly.
  The failures remain visible; the test does not silently accept a rejection as a successful match.
- No JSON injection reproduced. The other 52 payload/operator/path/format combinations
  returned exactly the expected IDs; all stored values/properties were unchanged.
- A 1001-character value was accepted and matched in both formats. The independent
  5000-character total-filter limit rejected oversized inputs in both formats.
- OpenObserve: all 75 request trace IDs found, 2687 spans, 108 logs, no filter database
  error spans or Error-level logs. Successful GET server spans (54): average 2.509 ms,
  p95 4.402 ms, maximum 10.320 ms. These are small-fixture observations, not load benchmarks.
- One setup-only `42P01` span probes a not-yet-created migration-history table during
  new workflow publication; publication succeeds and all later filter SQL succeeds.

This is a targeted review of #934, not a platform-wide security assessment. Aggregation,
authorization bypass, other operators, and load/resource-exhaustion behavior are outside this test.

## ValidateValue implementation: 2026-09-15

On the same branch plus the value-limit working-tree changes, `--value-limits-only`
passed **25/25** checks. Both formats accept 1000-character values and reject 1001 with
HTTP 400, including oversized `in`/`nin`/`between` elements. Lists whose individual
values fit still execute when their combined length exceeds 1000. Nested/logical
conditions and a grouping envelope cannot bypass the limit. All twelve persisted
records remain unchanged and Active; the limit is on filter operands, not stored data.

OpenObserve confirmed all **36/36 request traces**, 1136 spans and 79 logs. All twelve
value-length failures logged `filter.valueTooLong`. The seventeen rejected requests
(also including existing malformed/total-size checks) generated **zero PostgreSQL
spans**. Successful GET spans (six requests): average 23.661 ms, p95/max 115.939 ms;
this small cold-process sample is not a performance comparison. No filter DB errors
or Error-level logs occurred. The original four legacy-newline compatibility cases
are unchanged and still remain in the default full matrix.

## HTTP pagination, ordering and tie-breaks (PR #987)

Added 2026-09-16 for [vnext PR #987](https://github.com/burgan-tech/vnext/pull/987)
and issue #933. `--pagination-only` extends the same task-free HTTP fixture instead of
publishing a second workflow design. The state sketch remains `start → ready/Active`;
no transition, timer, external task or concurrent write runs while pages are read.
The critical path is HTTP query binding → ordered SQL identity page → page hydration
→ HATEOAS links. The companion `pagination_checks.py` uses the authored values and
API-returned IDs as its ordering oracle, not the runtime's first-page order.

```bash
python3 api-tests/instance-queries/test_instance_queries.py \
  --pagination-only --base-url http://localhost:6201 --domain core \
  --pg-container issue934-postgres --database Issue934 \
  --output /tmp/instance-pagination-results.json
```

Use the same locally built runtime/system-flow prerequisites as above. This mode and
`--value-limits-only` are mutually exclusive. It adds a `rank` attribute to the twelve
API-created rows: three groups of four equal values, so three-row pages cut through ties.
All writes still go through HTTP; PostgreSQL access remains read-only.

### Pass criteria

- **Eight orderings × three wire forms:** status ASC/DESC (all rows tie), explicit ID
  DESC, key ASC/DESC, attribute rank ASC/DESC, and multi-field status/rank ordering.
  Wire forms are bare GraphQL, legacy, and the existing request-envelope format.
- Exact ordered IDs on every page, including the exact last page and the following
  empty page; no duplicates or missing rows across a traversal.
- Equal primary sort values resolve by **ID ASC**, including descending primary sorts.
  The explicit ID DESC case must retain the caller's direction.
- Repeated second-page reads are identical with no intervening writes.
- Page sizes 1, 4, 5, 12 and 20 cover singleton, exact multiples, a partial final page,
  an exact fit and a page larger than the result set. A no-match filter yields an empty
  first page.
- The public `hasNext` signal is **`links.next`**, a relative URL string or an empty
  string, not a `hasNext` response property. Its presence and page/pageSize parameters
  must agree with the expected remaining rows.
- Following the returned next URLs verbatim must preserve filter and ordering across
  three pages of an eight-row subset, independently for all three wire forms.
- Hydrated attributes equal the original API inputs. All twelve latest PostgreSQL
  records retain exact values and Active status.

The mode currently runs **611 assertions**. Any false assertion or unexpected HTTP
response fails the command. Results include every request trace ID, expected/actual
page IDs, next URLs and persisted rows for independent inspection.

### Verified result and limits

On runtime `549722fe8f7fe62c6b1bea0c94a9087fdcc327d1`, branch
`feature/instance-data-performance`, .NET SDK 10.0.200, isolated API `:6201` and
PostgreSQL database `Issue934`: **611/611 passed**. All five host projects were rebuilt;
Orchestration/Execution/Inbox/Outbox ran as local binaries and DbMigrator completed.

OpenObserve confirmed **192/192 HTTP request traces**, **4709 spans** and **218 logs**.
All 192 requests returned HTTP 200. The 179 list GETs produced 179 identity-selection
queries with SQL `ORDER BY`/`LIMIT`/`OFFSET` and 149 selected-ID hydration queries
(empty pages need no hydration). No list DB error spans or Error-level logs occurred.
A setup-only `42P01` span checked migration history before the unique workflow schema
was created; publication then succeeded. Successful GET server spans: average
**2.555 ms**, p95 **4.159 ms**, maximum **10.334 ms**. These are a small correctness
fixture's timings, not a JSON-versus-index performance comparison.

The current parser recognizes request envelopes through a `groupBy`/`aggregations`
key. The list-only envelope in this test explicitly uses **`groupBy: null`**. Two
separate exploratory requests established the existing limitations: an envelope with
only `filter` + `orderBy` returns 400 (`filter.unknownOperator`), and `groupBy: {}`
returns 400 (`groupBy` requires at least one field). They were confirmed in runtime
logs/traces and are not presented as supported pagination shapes or fixed by this test.

Scope: default `IdentityPaging=true` / `LatestJoin=true`, JSON attribute expressions,
no prepared attribute indexes, no concurrent modifications between page reads. This
pins stable ordering for an unchanged data set; offset pagination does not promise a
snapshot across concurrent writes. It does not benchmark indexes or cover authorization,
aggregation-result pagination, or routing kill-switch configurations.
