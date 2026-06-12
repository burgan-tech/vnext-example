# AGENTS.md

This file provides guidance to Codex (and any AGENTS.md-compatible agent) when working with code in this repository.

> **Keep `CLAUDE.md` in sync with this file** — both files describe the same rules; `CLAUDE.md` exists for Claude Code. When you change one, update the other.

## Commands

```bash
npm run validate        # Validate all components against schemas (run before every commit)
npm run build           # Build runtime package (default)
npm run build:reference # Build reference package (exports only, for cross-domain usage)
npm run build:runtime   # Explicitly build runtime package
npm test                # Run tests
npm run sync-schema     # Sync schema version from dependencies
```

## Architecture Overview

This is a **vNext domain-driven workflow automation system**. A domain (here: `core`) defines workflows, tasks, views, schemas, functions, and extensions as JSON component files, which are deployed to a vNext Runtime engine.

### Component path resolution (read this before touching components)

**Never hardcode component folders.** Every component path is derived from the repo-root `vnext.config.json`:

```
{paths.componentsRoot}/{paths.<componentType>}/
```

In this repo: `componentsRoot: "core"` + `workflows: "Workflows"` → `core/Workflows/`. In another domain project the root may be `banking`, `payments`, etc. Always read `vnext.config.json` first to resolve:

- **`domain`** — used in component references and API URLs
- **`runtimeVersion`** / **`schemaVersion`** — sync targets
- **`paths.*`** — component folder names
- **`referenceResolution.strictMode`** / **`validateReferenceConsistency`** / **`validateSchemas`** — what `npm run validate` enforces

### Standard component JSON envelope

Every component, regardless of type, follows this shape:

```json
{
  "key": "kebab-case-name",
  "version": "1.0.0",
  "domain": "core",
  "flow": "sys-flows",
  "flowVersion": "1.0.0",
  "tags": ["searchable", "tags"],
  "attributes": { /* type-specific content */ }
}
```

**`flow` values per component type**: `sys-flows` (workflows), `sys-tasks`, `sys-schemas`, `sys-views`, `sys-functions`, `sys-extensions`.

**Cross-component references** use this nested shape:

```json
{ "key": "create-bank-account", "domain": "core", "flow": "sys-tasks", "version": "1.0.0" }
```

### Component creation map

Use this as the first lookup when a user asks to create or modify a component. For deeper detail, follow the **Knowledge access strategy** below.

| Component | `paths` key | Required `attributes` | Detail source |
|-----------|-------------|------------------------|---------------|
| Workflow | `paths.workflows` | `type` (F/S/P/C/...), `startTransition`, `states`, `transitions` | `/docs/components/workflow` |
| View | `paths.views` | `display`, `renderer`, `content` | `/docs/components/view` + `/docs/how-to/view-consept/` |
| Task | `paths.tasks` | `type` (numeric, see below), `config` | `/docs/components/tasks/{subtype}` |
| Schema | `paths.schemas` | `type`, `schema` (JSON Schema draft 2020-12) | `/docs/components/schema` |
| Function | `paths.functions` | `scope` (I/D), `onExecutionTasks[]`, `output` | `/docs/components/functions/{built-in\|custom}` |
| Extension | `paths.extensions` | `type`, `scope`, `task` / `tasks` | `/docs/components/extension` |
| Mapping (`.csx`) | workflow folder `src/` | C# class (PascalCase) | `/docs/components/mappings` |

**Task `type` quick map** (verify against docs before using): `6` = HTTP, `7` = Script, `15` = GetInstances. Other types: trigger, notification, dapr-service, dapr-pubsub, dapr-binding, dapr-http-endpoint, soap — see `/docs/components/tasks/{subtype}`.

### View renderer — ask the user first

When a user wants to create or modify a view, **the first question is always: which renderer?** Options:

- **`pseudo-ui`** (recommended — the platform's official UI SDK)
- `html`, `json`, `markdown`, `url`, `http`, `deeplink`

If `pseudo-ui` is chosen, load the vocabulary and component list **before** producing JSON. **Vocabulary sources, in priority order**:

1. **In-repo author guide**: [`.claude/references/view-author-guide.md`](./.claude/references/view-author-guide.md) — patterns, action model, expression namespace, ForEach, nested Component, antipatterns. This is the always-available primary reference (English).
2. **Renderer repo** (vocabulary source-of-truth): `https://github.com/burgan-tech/vnext-client-view-renderer` — file `vocabularies/view-vocabulary.md`. Currently **private** (request access from the vNext team); will be public at RC. When cloned, read it for every component's required/optional props and platform mapping. If you already have it locally, an agent may have a path hint in its private memory.
3. **Context7** (`mcp__context7__query-docs` with `view-yapisi`, `tasarimci-rehberi`) — online docs fallback.
4. **WebFetch** `https://burgan-tech.github.io/vnext-docs/docs/how-to/view-consept/view-yapisi` — last resort.

pseudo-ui views use `$schema: https://amorphie.io/meta/view-vocabulary/1.0` and reference a `dataSchema` URN. The `view` tree uses components like `ScrollView`, `Column`, `Card`, `ListTile`, `Button`. Data binding syntax: `$instance.fieldName`, `$schema.fieldName.label`, `$form.fieldName`, `$ui.key`, `$lov.x`, `$lookup.x.field`. Input `bind` is the **schema property path** (`"firstName"` or `"address.city"`), never with `$form.` prefix.

**Icon names — Material Symbols (MD3) only.** pseudo-ui's `Icon` component (and `Button.icon`) consumes the **Material Symbols** icon set with MD3 support. Names use lowercase `snake_case`, exactly as listed at https://fonts.google.com/icons. Never use kebab-case (`check-circle`), Font Awesome names (`fa-check`), or made-up tokens. Common mappings: clock → `schedule`, chart-line → `show_chart`, chart-bar → `bar_chart`, pencil → `edit`, user → `person`, id-card → `badge`, map-marker → `location_on`, mobile → `smartphone`, cog → `settings`, bell → `notifications`, check-circle → `check_circle`, credit-card → `credit_card`, arrow-right → `arrow_forward`. Verify unknown names against the Material Symbols catalog before placing them in JSON.

**Wizard state view placement.** When a state has `stateType: 5` (Wizard), keep `state.view = null` and put the view reference on the state's single transition. Wizard semantics expect the form to be rendered during the transition action, not on state entry.

**Initial state input placement.** When the workflow's Initial state (`stateType: 1`) gathers user input, the **default placement is `state.view`** (on the state itself), NOT on the outgoing transition. Reason: the runtime serves the state view immediately on instance start — the user sees the form right away and submits via a `view: null` transition. Putting the form on the transition forces the client to discover and trigger it before any UI appears, with no UX benefit. Ask the user to confirm (some flows intentionally want an "intro screen → tap → form" two-step); state-view is Recommended. This is the inverse of the Wizard state rule.

**Auto transition view ban.** Transitions with `triggerType: 1` (auto, rule-evaluated) and `triggerType: 2` (timer) must have `view: null` — the runtime fires them without user interaction. By extension, an Intermediate state (`stateType: 2`) whose only outgoing transitions are auto/timer typically needs `state.view = null` too; passive processing states are not user-facing and don't render. Only manual transitions (`triggerType: 0`) can carry a view.

**Button action model.** Reserved verbs: `submit` (validates by default), `select` (inline set, host NOT called), `reset` (clears formData, runs hooks), `dispatch` (domain dispatch; optional `validate`). The actual workflow/function target goes in `command` as a URN: `urn:vnext:flow:transition:{domain}:{flow}:{transition}` for workflow transitions, `urn:vnext:fn:{cmd}:{domain}:{function}` for functions (`cmd` defaults to `get`), `urn:client:nav:/path` for client-local navigation. `submit` runs validation; `dispatch` exposes an explicit `validate` flag. For `Card.onTap` (vocabulary's preferred name; legacy `action` alias still works), use `{ "action": "dispatch", "command": "urn:vnext:..." }` or the `select` form `{ "action": "select", "bind": "...", "value": "..." }`. Attach `preHooks`/`postHooks` arrays for audit/telemetry side-effects (see `.claude/references/view-author-guide.md` §4). Never use ad-hoc verbs like `"transition"` — the SDK has no built-in semantics for them.


**Stepper is not a progress bar.** vocabulary requires `Stepper.steps[].title` (string/multi-lang) **and** `steps[].content` (componentNode[]) — it renders a true multi-step form on a single screen. Don't use Stepper as a wizard progress indicator across separate state views; for that, show a small `Text` ("Adım 2 / 4") at the top of each view instead.

**View `dataSchema` selection.** A view's `dataSchema` URN must match the **role** of the view:
- **Transition / input views** (user fills a form): bind to the **transition payload schema** (e.g. `urn:vnext:res:schema:core:account-type-selection`, `:demand-deposit-input`). These schemas carry `enum`/`x-lov`/`x-validation`/`x-conditional` for the input field set.
- **Display / summary / status views** (read-only from `$instance`): bind to the **master / instance schema** (e.g. `urn:vnext:res:schema:core:account-opening-master`). The master schema covers the full instance shape so `$schema.<field>.label` and `$instance.<field>` paths resolve everywhere.

Don't point a transition view at the master schema "just to keep things uniform" — the master typically lacks the `required`/`x-lov`/`x-validation` semantics the transition needs.

**`x-lov` / `x-lookup` are for field-input, not navigation.** SDK intent: `x-lov` provides dropdown options for a bound input (typically `Dropdown.bind` — a `TextField` will NOT render LOV options, use `Dropdown`), and `x-lookup` enriches a field with read-only detail (`$lookup.X.field`). A **navigation Card grid** (each card dispatches its own workflow transition) is not a form-input — encoding transition URNs into LOV item data (`$item.transitionCommand`) blurs UI semantics with data and adds mock dependencies for no gain. Use a **static Card grid with hardcoded `onTap.command` URNs** for navigation; reserve LOV/lookup for actual `bind`-driven dropdowns and field-bound enrichment.

**`x-lookup` access is by property name.** Per `view-model-vocabulary.json`, lookup results resolve as `$lookup.{propertyName}.{field}` where `{propertyName}` is the schema property that owns the `x-lookup`. To read `$lookup.branchDetail.*`, the `x-lookup` must live on a property literally named `branchDetail` — define it as a dedicated read-only object property (not in `required`, no `bind`), **separate from** the input field it enriches (e.g. `branchCode`). Activate in the view with `lookups: ["branchDetail"]`. Filter value scope: input/transition views use `$form.X`, display/summary views (master schema) use `$instance.X`.

### Functions — script mapping pattern (`.csx`)

vNext functions wrap upstream task results as a `StandardTaskResponse` (`{ data, body, statusCode, headers, metadata, isSuccess, ... }`). Four rules avoid the common pitfalls:

0. **`rawResponse: true` for view-bound functions.** If a view binds to the function output (via `dataSchema`, `x-lov.source`, `x-lookup.source`, `$lov.X`, `$lookup.X`), set `attributes.rawResponse: true` in the function JSON (same level as `scope`). Default is `false`, which wraps the response under the function key (`{ "{functionKey}": { ... } }`) and silently breaks JsonPath like `$.data[*]` — dropdowns appear empty with no error logged. Examples: `core/Functions/account-opening/get-branches.json` and `get-branch-detail.json` both set `rawResponse: true`.

1. **Unwrap output, don't return `context.Body` raw.** Inside `OutputHandler`, `context.Body` is the parsed HTTP response body of the upstream task. If you set `ScriptResponse.Data = context.Body`, the response double-wraps (the renderer's `x-lov` JsonPath `$.data[*].code` then fails because the array lives at `$.data.data[*]`). Unwrap one level and re-envelope:
   ```csharp
   dynamic payload = context.Body?.data ?? context.Body;
   dynamic items = null; try { items = payload?.data ?? payload; } catch { items = payload; }
   return Task.FromResult(new ScriptResponse {
     Key = "...", Data = new { data = items }, Tags = new[] { "lov", "success" }
   });
   ```
2. **GET function calls don't carry a body.** When the renderer's `x-lov`/`x-lookup` invokes a function via GET, parameters arrive in `context.QueryString[…]` or `context.Headers[…]`, **not** `context.Body`. Use a multi-source resolver (QueryString → Headers → Body fallback). Reading from `context.Body?.<field>` alone breaks renderer-initiated calls.
3. **Branch on `statusCode` + tag the response.** Read `context.Body?.statusCode`; use `2xx` for success, `4xx` (esp. 404) for not-found, exceptions for transport errors. Tag `ScriptResponse.Tags` with `success` / `failure` / `not-found` / `exception` for downstream filtering.

Full template, examples, and the `IMapping` vs. `IOutputHandler` (multi-task) distinction in [`.claude/references/function-mapping-pattern.md`](./.claude/references/function-mapping-pattern.md). Working LOV/lookup examples: `core/Functions/account-opening/src/GetBranchesLovMapping.csx` (LOV cascade) and `GetBranchDetailLookupMapping.csx` (lookup).

### Flow execution mental model

How the vNext runtime executes a workflow — keep this in mind when designing states and transitions:

1. **Instance start** — POST creates an instance; runtime fires `startTransition`, lands on its `target` state.
2. **State entry** — if the state has a `view`, it renders; `onEntry` tasks fire (sequential by `order`, parallel when same order).
3. **Transition triggers** — `triggerType: 0` (manual, user-driven), `1` (auto, condition-evaluated immediately), `2` (timer, after duration), `3` (event, external signal).
4. **Auto transitions** must come in **complementary pairs** with mutually exclusive `rule` conditions, OR be a single transition with an unconditional rule (always true). A lone conditional auto transition is invalid.
5. **`onExecutionTasks`** run during transition execution; each task has a `mapping` (`.csx`) that transforms instance data into task input and back.
6. **State exit** — instance moves to the next state; loop until a final state (`isFinal: true`) is reached.

### Schema design — plan with the user

When a user wants a new schema, **do not produce JSON before gathering inputs.** Ask:

- Fields, types, required vs optional
- Validation rules (regex, min/max, format)
- Localization needs (`x-labels.tr`, `x-labels.en` on properties)
- Role-based field access (`roles[]` with `$PreviousUser`, `$CurrentUser`, etc.)
- Whether the schema is workflow data, a transition payload, or task input/output

Output: JSON Schema draft 2020-12 with `$id` set to a **URN** of the form `urn:vnext:res:schema:{domain}:{key}` (e.g. `urn:vnext:res:schema:core:account-opening-master`). Views reference the same URN in their `dataSchema` field — never use HTTP URLs for schema identifiers, the runtime resolves URNs against the registered schema set.

### Knowledge access strategy

When you need detail beyond what's in this file:

1. **First — Context7 MCP** (semantic search, low token cost; `vnext-docs` is registered)
   - `mcp__context7__resolve-library-id` with `vnext-docs`
   - `mcp__context7__query-docs` with a focused query (e.g. `"pseudo-ui ScrollView vocabulary"`, `"workflow auto transition rule format"`)

2. **Then — WebFetch** for deterministic URLs you already know:
   - `https://burgan-tech.github.io/vnext-docs/docs/components/{workflow|view|schema|extension|mappings|interfaces}`
   - `https://burgan-tech.github.io/vnext-docs/docs/components/tasks/{http|script|trigger|get-instances|notification|dapr-service|dapr-pubsub|dapr-binding|dapr-http-endpoint|soap}`
   - `https://burgan-tech.github.io/vnext-docs/docs/components/functions/{built-in|custom}`
   - `https://burgan-tech.github.io/vnext-docs/docs/how-to/view-consept/{tasarimci-rehberi|view-yapisi|schema-tanimi|data-akisi}`
   - `https://burgan-tech.github.io/vnext-docs/docs/api-reference/rest-api`
   - `https://burgan-tech.github.io/vnext-docs/sitemap.xml` (full URL list)

3. **Last resort** — example components under the resolved components root, and auto-generated `docs/` markdown (produced by vNext Forge).

Prefer Context7 for vocabulary lookups and "how does X work" questions; prefer WebFetch when you already know the exact page.

### C# mapping files (`.csx`)

Workflows reference C# script files in `src/` folders next to the workflow JSON. The vNext VS Code extension auto-encodes them as base64 in the JSON's `mapping.code` field when saving. **Never manually base64-encode `.csx` files.** Class names are PascalCase; file names are kebab-case.

### Build outputs

- **Runtime build** (`@burgan-tech/vnext-core-runtime`) — Complete domain structure for engine deployment
- **Reference build** (`@burgan-tech/vnext-core-reference`) — Exported components only, for cross-domain usage

### Local development servers

| Server | Port | Purpose |
|--------|------|---------|
| vNext Runtime | `localhost:4201` | Workflow engine; use for instance start, transitions, state queries |
| MockLab | `localhost:3001` | External API mocks; all HTTP tasks point here. Container `ghcr.io/burgan-tech/mocklab:latest` is started by `docker-compose.yml`; seed collections live under `etc/docker/config/seed/`. A `mocklab-dapr` sidecar is co-located (dapr app-id `mocklab`, http port `3500`), so endpoints can also be reached via dapr service invocation. |

**Never** call MockLab (`localhost:3001`) for workflow operations, and **never** hardcode production URLs in HTTP task configs — always route mocked dependencies through MockLab during development.

### Runtime API — HTTP test file pattern

Every workflow should have a `.http` test file demonstrating full instance progression. The base URL pattern:

```http
@baseUrl = http://localhost:4201
@apiVersion = 1
@domain = core

### Start instance
POST {{baseUrl}}/api/v{{apiVersion}}/{{domain}}/workflows/{workflow-key}/instances/start

### Execute transition (manual trigger)
PATCH {{baseUrl}}/api/v{{apiVersion}}/{{domain}}/workflows/{workflow-key}/instances/{instanceKey}/transitions/{transitionKey}

### Get state (long polling for view + data)
GET {{baseUrl}}/api/v{{apiVersion}}/{{domain}}/workflows/{workflow-key}/instances/{instanceKey}/functions/state

### Call a function
POST {{baseUrl}}/api/v{{apiVersion}}/{{domain}}/workflows/{workflow-key}/instances/{instanceKey}/functions/{functionKey}

### Retry a failed transition
POST {{baseUrl}}/api/v{{apiVersion}}/{{domain}}/workflows/{workflow-key}/instances/{instanceKey}/retry

### Query instances
GET {{baseUrl}}/api/v{{apiVersion}}/{{domain}}/workflows/{workflow-key}/instances?state={stateKey}
```

For the full Runtime API reference (request/response schemas, error codes), see `/docs/api-reference/rest-api`.

### MockLab — mock layer

**MockLab is the canonical mock API** (repo: `https://github.com/burgan-tech/mocklab`, currently private). Mockoon has been removed.

**Seed layout** — `etc/docker/config/seed/{domain}-collection.json`, one collection per business domain (current set: `account-opening-collection`, `future-pay-collection`, `money-transfer-collection`, `integration-test-collection`, `notification-collection`, `payments-collection`). MockLab recursively scans the seed directory on startup and imports each `*.json` file as a collection.

**Collection envelope**:

```jsonc
{
  "collection": { "name": "<domain>", "description": null, "color": "#6366f1" },
  "folders": [],
  "mocks": [
    {
      "httpMethod": "GET|POST|...",
      "route": "api/{domain}/{resource}/{action}",
      "queryString": null,
      "requestBody": "",
      "statusCode": 200,
      "responseBody": "<JSON string; supports Scriban: {{helpers.guid()}}, {{request.body.X}}>",
      "contentType": "application/json",
      "description": "...",
      "delayMs": null,
      "isActive": true,
      "isSequential": false,
      "folderIndex": null,
      "rules": [
        // conditionField: "query.X" | "body.X" | "header.X" | "route.X" | "method" | "path"
        // conditionOperator: equals | regex | contains | startsWith | endsWith | exists | notExists | greaterThan | lessThan
        { "conditionField": "query.currency", "conditionOperator": "equals", "conditionValue": "TRY",
          "statusCode": 200, "responseBody": "...", "contentType": "application/json",
          "priority": 0, "responseHeaders": [] }
      ],
      "sequenceItems": []   // for retry/rate-limit demos (isSequential: true)
    }
  ]
}
```

**HTTP task URL convention** — all tasks point at MockLab via `http://localhost:3001/api/{domain}/{resource}/{action}`; task type stays `"6"` (HTTP). Dapr service invocation is an optional alternative: `http://localhost:3500/v1.0/invoke/mocklab/method/api/{domain}/{resource}/{action}` (or a `dapr-service` task with app-id `mocklab`).

**Adding a mock for a new HTTP task** — append a `mocks[]` entry to the domain's existing collection file (do **not** create a separate collection for the same domain). After editing the seed, **drop the MockLab volume to force re-import** (`docker compose down -v && docker compose up -d mocklab`) — MockLab skips collections whose name already exists in the DB.

For the full seed format reference (rule operators, Scriban helpers, sequential responses, dapr invocation), see [`.claude/references/mocklab-seed-format.md`](./.claude/references/mocklab-seed-format.md).

## Skills

When a user wants to build a component end-to-end, prefer invoking the matching skill (provided by the `vnext-ai-toolkit` plugin) rather than freestyling. Invoke them by their namespaced name:

- **`vnext-ai-toolkit:view-design`** — interactive view creation; asks for renderer, loads pseudo-ui vocabulary if needed, generates view JSON
- **`vnext-ai-toolkit:schema-design`** — interactive JSON Schema authoring with localization and role-based access
- **`vnext-ai-toolkit:workflow-scaffold`** — end-to-end workflow scaffolding (states, transitions, views, schemas, `.http` test file)
- **`vnext-ai-toolkit:validate-and-fix`** — runs `npm run validate`, categorizes errors, proposes fixes
- **`vnext-ai-toolkit:component-task` / `:component-function` / `:component-extension`** — single-component scaffolders (task / function / extension) that fetch the relevant schema first
- **`vnext-ai-toolkit:integration-test`** — generates xUnit lifecycle tests against the official `VNext.Testing.Sdk`

End-to-end orchestration: **`/vnext-ai-toolkit:vnext-design-process "<workflow name>"`** runs the multi-turn architect (Discovery → Flow → Components → Assembly → Test). The legacy local skills under `.claude/skills/` have been removed — the plugin is now the source of truth.

## Critical Rules

- Each workflow must have **exactly one** initial state (defined by `startTransition.target`).
- Auto transitions (`triggerType: 1`) must come in complementary pairs with mutually exclusive conditions. A lone auto transition is only valid if its rule always returns true (unconditional).
- All component references use the format: `{domain}/{component-type}/{key}/{version}` — strict mode is enabled in `vnext.config.json`.
- Run `npm run validate` after any component change. It validates JSON syntax and schema compliance for all components.
- JSON files use **2-space indentation**, double quotes, no trailing commas.
- All keys and file names use **kebab-case**; C# class names use **PascalCase**.
- **Never** hardcode component folder paths — always resolve from `vnext.config.json`.
- **Never** manually base64-encode `.csx` mapping files — the VS Code extension handles that.
- **Always** ask the user which `renderer` to use before creating a view.
- **Always** gather field/validation/localization/role requirements from the user before producing a schema.
