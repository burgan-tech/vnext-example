# MockLab Seed Format — Quick Reference

> **Audience**: Anyone adding or editing mock endpoints for the vNext example. Pair with the in-repo workflow/HTTP task files.
> **Goal**: Single source of truth for the MockLab seed JSON layout, rule semantics, Scriban helpers, dapr invocation, and the seed re-import behaviour.

MockLab is the project's canonical mock API (repo: `https://github.com/burgan-tech/mocklab`, currently private; public at RC). The container is started by `docker-compose.yml` (`ghcr.io/burgan-tech/mocklab:latest`, port `3001:5000`) and recursively imports every `*.json` file under `/app/seed` (host path: `etc/docker/config/seed/`).

---

## 1. File layout

One JSON file per business domain. Current set:

```
etc/docker/config/seed/
├── account-opening-collection.json
├── payments-collection.json
├── integration-test-collection.json
└── notification-collection.json
```

**Rule of thumb**: never split a single business domain into multiple collections. Append new mocks to the existing file. (See §7 for the re-import gotcha.)

---

## 2. Collection envelope

```jsonc
{
  "collection": {
    "name": "<domain>",          // unique key in MockLab DB — used by re-import skip logic
    "description": null,
    "color": "#6366f1"
  },
  "folders": [                   // optional — hierarchical grouping inside the admin UI
    { "name": "Accounts", "color": "#22c55e", "parentFolderIndex": null }
  ],
  "mocks": [ /* see §3 */ ]
}
```

---

## 3. Mock object

```jsonc
{
  "httpMethod": "GET",                              // GET | POST | PUT | PATCH | DELETE | HEAD | OPTIONS
  "route": "api/banking/lov/branches",              // no leading slash; MockLab adds it
  "queryString": null,                              // optional literal match (e.g. "?type=savings")
  "requestBody": "",                                // optional literal match for POST/PUT/PATCH body
  "statusCode": 200,                                // default response status
  "responseBody": "<JSON string, Scriban-templated>",
  "contentType": "application/json",                // default MIME
  "description": "Human-readable label",
  "delayMs": 200,                                   // null or 0 = instant; otherwise simulated latency
  "isActive": true,
  "isSequential": false,                            // if true → use sequenceItems instead of rules
  "folderIndex": null,                              // index into collection.folders[] or null
  "rules": [ /* §4 */ ],
  "sequenceItems": [ /* §5 */ ]
}
```

`responseBody` is **always a JSON string** (escaped). Multi-line JSON is `\n`-separated inside the string.

---

## 4. Conditional rules

Rules are evaluated in `priority` ascending order; the first match wins. If no rule matches, the mock's root `statusCode`/`responseBody` is used.

```jsonc
"rules": [
  {
    "conditionField": "query.currency",   // see fields below
    "conditionOperator": "equals",        // see operators below
    "conditionValue": "TRY",
    "statusCode": 200,
    "responseBody": "{\"data\": [ ... ]}",
    "contentType": "application/json",
    "priority": 0,
    "responseHeaders": [ { "key": "X-Currency", "value": "TRY" } ]
  }
]
```

### Condition fields

| Field | Reads from |
|---|---|
| `query.<param>` | Query string parameter |
| `body.<jsonPath>` | Body field (e.g. `body.customer.id`) |
| `header.<HeaderName>` | Request header (case-insensitive) |
| `route.<param>` | Path parameter (`/users/{id}` → `route.id`) |
| `cookie.<name>` | Request cookie |
| `method` | HTTP method literal |
| `path` | Full request path |

### Operators

`equals`, `notEquals`, `contains`, `startsWith`, `endsWith`, `regex`, `exists`, `notExists`, `greaterThan`, `lessThan`.

Strings compare lexicographically; for numeric comparisons (`greaterThan` / `lessThan`) MockLab attempts numeric parse first.

---

## 5. Sequential responses

For retry/rate-limit demos. Set `"isSequential": true` and provide an ordered list; MockLab cycles through them per call.

```jsonc
"isSequential": true,
"sequenceItems": [
  { "order": 0, "statusCode": 503, "responseBody": "{\"error\":\"Service unavailable\"}", "contentType": "application/json" },
  { "order": 1, "statusCode": 503, "responseBody": "{\"error\":\"Service unavailable\"}", "contentType": "application/json" },
  { "order": 2, "statusCode": 200, "responseBody": "{\"orderId\":\"ORD-123\"}",           "contentType": "application/json" }
]
```

Rules and sequence items are mutually exclusive — choose one mode per mock.

---

## 6. Scriban templating

Response bodies and header values pass through a Scriban engine. Helpers take **space-separated
args, no parentheses** (`{{ random_int 1 100 }}`, not `helpers.rand_int(1,100)`). Common helpers:

| Template | Output |
|---|---|
| `{{ guid }}` (or `{{ helpers.guid }}`) | New GUID per request |
| `{{ iso_timestamp }}` / `{{ timestamp }}` | ISO 8601 UTC / Unix timestamp |
| `{{ now_fmt 'yyyy-MM-dd' }}` | Current date, formatted |
| `{{ random_int 85 100 }}` | Random integer in range |
| `{{ random_float }}` / `{{ random_double 1 99 }}` | Random float / double in range |
| `{{ random_string 8 }}` / `{{ random_alpha_numeric 12 }}` | Random string / alphanumeric |
| `{{ random_string 24 "0123456789" }}` | N-char string from a custom set (e.g. IBAN tail) |
| `{{ request.body.amount }}` | Echo a field from the parsed JSON request body |
| `{{ request.query["code"] }}` | Echo a query parameter (bracket syntax) |
| `{{ request.headers["X-Request-Id"] }}` | Echo a request header |
| `{{ request.route.id }}` | Echo a route parameter |

> **Verify against the live guide.** The helper set evolves — confirm names at
> `https://github.com/burgan-tech/mocklab/blob/master/docs/user-guide.md` (or via Context7).
> Wrong forms seen in older docs: `helpers.guid()`, `helpers.now()`, `helpers.rand_int(..)`,
> `random_account_number`, `random_number_string`, `request.json.X` — all invalid.

Inside JSON string responses, template tokens are typed-aware — numbers stay numbers when used as values:

```jsonc
"responseBody": "{ \"score\": {{ random_int 70 100 }} }"
```

---

## 7. Re-import behaviour (gotcha)

MockLab imports each seed file on startup, **keyed by `collection.name`**. If a collection with the same name already exists in the DB, the file is **skipped entirely** — newly-added mocks are not picked up.

Two ways to apply changes after editing a seed:

```bash
# (A) Full reset — drops the DB volume and re-imports everything
docker compose down -v
docker compose up -d mocklab
docker logs mocklab | grep -i "import\|seed"

# (B) Live edit via the admin API (no restart)
curl -X POST http://localhost:3001/_admin/mocks \
  -H "Content-Type: application/json" \
  -d @new-mock.json
# Then mirror the change in the seed file for the next clean start.
```

Always prefer (A) for permanent changes and (B) for quick exploration.

---

## 8. Calling MockLab from workflows

### 8a. HTTP task (default, type `"6"`)

```jsonc
{
  "key": "get-branches-lov",
  "domain": "core",
  "flow": "sys-tasks",
  "version": "1.0.0",
  "attributes": {
    "type": "6",
    "config": {
      "url": "http://localhost:3001/api/banking/lov/branches",
      "method": "GET",
      "headers": { "Accept": "application/json" },
      "body": {},
      "timeoutSeconds": 10,
      "validateSsl": true
    }
  }
}
```

This is the convention for all existing tasks in `core/Tasks/{domain}/`.

### 8b. Dapr service invocation (optional alternative)

A `mocklab-dapr` sidecar runs alongside MockLab (`docker-compose.yml`), with app-id `mocklab` and dapr-http port `3500`. Any task can reach MockLab through the sidecar:

```http
GET http://localhost:3500/v1.0/invoke/mocklab/method/api/banking/lov/branches?currency=TRY
```

To make a workflow task use dapr invocation, use the `dapr-service` task subtype (see `/docs/components/tasks/dapr-service` in the vNext docs) instead of plain HTTP. The mock route stays the same.

---

## 9. Example — LOV with cascade and lookup with per-key rule

Pulled from `etc/docker/config/seed/account-opening-collection.json` for reference:

```jsonc
// LOV — cascade by query.currency
{
  "httpMethod": "GET",
  "route": "api/banking/lov/branches",
  "statusCode": 200,
  "responseBody": "{\"data\":[ /* full fallback list */ ]}",
  "rules": [
    { "conditionField": "query.currency", "conditionOperator": "equals", "conditionValue": "TRY",
      "statusCode": 200, "responseBody": "{\"data\":[ /* TRY branches */ ]}", "priority": 0 },
    { "conditionField": "query.currency", "conditionOperator": "equals", "conditionValue": "USD",
      "statusCode": 200, "responseBody": "{\"data\":[ /* USD branches */ ]}", "priority": 1 }
    // ...
  ]
}

// Lookup — per-code response, default 404
{
  "httpMethod": "GET",
  "route": "api/banking/lookup/branches",
  "statusCode": 404,
  "responseBody": "{\"error\":{\"code\":\"branch_not_found\"}}",
  "rules": [
    { "conditionField": "query.code", "conditionOperator": "equals", "conditionValue": "1001",
      "statusCode": 200, "responseBody": "{\"data\":{ /* Kadıköy detail */ }}", "priority": 0 }
    // ...
  ]
}
```

This pattern drives the `x-lov` (cascade) and `x-lookup` examples in the account-opening schemas — see [`view-author-guide.md`](./view-author-guide.md) §2.3–2.4 for the schema-side contract.
