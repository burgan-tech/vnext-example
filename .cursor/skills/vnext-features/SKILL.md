---
name: vnext-features
description: Repo-wide domain knowledge (not bound to a single workspace). vNext Runtime platform reference. Covers workflow, state, transition, task, schema, view, function and extension concepts, JSON definition structures, and C# mapping/rule patterns. Use this skill when creating or editing vNext components. Trigger whenever vNext workflow/task/schema/view/function/extension JSON or `.csx` mapping/rule files are created or edited anywhere in the monorepo (or in target vNext domain repositories).
---

# vNext Skill

> **Scope:** Repo-wide domain reference. Applies in every workspace when working with vNext components (workflow, task, schema, view, function, extension JSON and `.csx` mapping/rule files); not limited to a single `apps/*` or `packages/*`.

This file is a compact reference for understanding vNext Runtime quickly yet completely. Its goal is to give one place where someone working with vNext can grasp the core model, JSON definitions, supported structures, and extension points.

## 1. What is vNext?

vNext is a platform where processes are modeled as workflows defined and versioned in JSON and executed as state machines at runtime.

Core ideas:

- Processes are defined as `workflow`/flow artifacts.
- Progress through a workflow is controlled by `state` and `transition`.
- Units of work are `task`s.
- Data contracts are defined with `schema`.
- Client/UI presentation uses `view`.
- Runtime read/access endpoints are surfaced as `function`.
- Response enrichment uses `extension`.
- Reuse favors `reference` and version strategy rather than duplication.

So vNext brings together:

- process modeling,
- orchestration,
- integrations,
- a stateful runtime,
- versioned definition management,

under one umbrella.

## 2. Mental Model

The shortest path to reasoning about vNext is this lifecycle:

1. A workflow JSON is published.
2. The runtime starts an `instance` of that workflow.
3. The instance sits in a state.
4. A user, event, timer, or automatic rule triggers a transition.
5. Tasks run before/around the transition.
6. Data is carried and validated per schema rules.
7. Optionally subflows/subprocesses are started.
8. The runtime changes state; view and function outputs follow.

So vNext is about **moving from state to state**, not about “screens” alone.

## 3. Building Blocks

### 3.1 Workflow / Flow

A workflow is the primary process definition. It must include:

- `type`
- `startTransition`
- `states`

Supported workflow kinds:

- `C`: Core
- `F`: Flow
- `S`: SubFlow
- `P`: SubProcess

Common extra fields:

- `labels`
- `timeout`
- `functions`
- `features`
- `extensions`
- `sharedTransitions`
- `cancel`
- `schema`
- `errorBoundary`

When to use which:

- `F`: primary business flow
- `S`: child flow tightly coupled to parent
- `P`: independent, fire-and-forget–style subprocess
- `C`: system/core definitions

### 3.2 State

State is where the instance is at a moment in time.

State types:

- `1`: Initial
- `2`: Intermediate
- `3`: Finish
- `4`: SubFlow

State subtypes:

- `0`: None
- `1`: Success
- `2`: Error
- `3`: Terminated
- `4`: Suspended
- `5`: Busy
- `6`: Human

Typical state fields:

- `key`
- `stateType`
- `subType`
- `labels`
- `transitions`
- `onEntries`
- `onExits`
- `view`
- `subFlow`
- `errorBoundary`

Notes:

- A `SubFlow` state binds parent and child hierarchically.
- The runtime may expose richer external state via `effectiveState` in nested flows.

### 3.3 Transition

Transitions define valid state moves.

Main fields:

- `key`
- `from`
- `target`
- `triggerType`

Optional fields:

- `timer`
- `rule`
- `schema`
- `availableIn`
- `labels`
- `view`
- `mapping`
- `onExecutionTasks`

Trigger types:

- `0`: Manual
- `1`: Automatic
- `2`: Scheduled
- `3`: Event

Important behaviour:

- `startTransition` is the entry transition (no `from`).
- `availableIn` pins shared transitions across multiple states.
- `target: "$self"` is common for scenarios like refreshing parent data after subflow completion.
- If there is no mapping, transition payloads merge straight into instance data.

### 3.4 Task

Tasks perform real work—they are integration and execution primitives.

Tasks can run:

- state `onEntries`
- state `onExits`
- transition `onExecutionTasks`
- function definitions
- extension definitions

Ordering:

- same `order`: parallel
- different `order`: ascending sequential

Families:

- `Http`
- `DaprService`
- `DaprPubSub`
- `Script`
- `Trigger`
- `GetInstances`
- `Condition` (generated)
- `Timer` (generated)

Notables in the Trigger family:

- `StartTask`
- `DirectTriggerTask`
- `GetInstanceDataTask`
- `SubProcessTask`

Typical response metadata:

- `Data`
- `StatusCode`
- `IsSuccess`
- `ErrorMessage`
- `Headers`
- `Metadata`
- `ExecutionDurationMs`
- `TaskType`

So tasks are not only adapters—they orchestrate workflows.

### 3.5 Schema

Schema is the contract. It applies to workflow data and typed definitions (tasks, functions, views, etc.).

Common envelope fields:

- `key`
- `version`
- `domain`
- `flow`
- `flowVersion`
- `tags`
- `attributes`

`attributes.type` values include:

- `workflow`
- `task`
- `function`
- `view`
- `schema`
- `extension`
- `headers`

Engines use JSON Schema Draft 2020-12 primitives and keywords (constraints, combinators).

At flow scope, `master schema` is usually the authoritative instance contract.

Schemas also carry field visibility/authorization notions and transition-scoped validation.

### 3.6 View

View describes what clients render—not necessarily imperative UI—structured renderable payloads.

Important fields:

- key, flow, domain, version (envelope), plus `type`, `content`, `display`, labels, overrides.

Representations include JSON/HTML/Markdown/DeepLink/etc.

Modes like `full-page`, `popup`, `bottom-sheet`, etc.

Selections can target state transitions, platforms, locales.

Key idea: **vNext does not dictate UX**—it emits configurable presentation payloads.

### 3.7 Function

Functions are first-class endpoints for inspecting or interacting without leaking orchestration internals.

Built-ins include `state`, `data`, `view`.

Benefits:

- clients stay decoupled from orchestration internals
- polling/ETags
- aligns with authorization/roles

### 3.8 Extension

Extensions enrich runtime responses—not external endpoints themselves.

Compared to Functions:

- Function = dedicated endpoint path
- Extension = merged into responses under `extensions`

Types/scopes classify global vs selective attachment.

Purpose: enrichment (profiles, aggregates, lookups, computed fields).

### 3.9 Mapping

Mappings adapt data flowing into/out of tasks and transitions (`IMapping`, `ITimerMapping`, conditional/transition/subflow/subprocess mappings, etc.)

Script context exposes body, headers, query, routing, instance and definition metadata, responses, metadata.

Engines use Roslyn/C# plus encoded scripts where applicable.

### 3.10 Error Boundary

Handled hierarchically: task/state/global.

Policies include abort/retry/rollback/ignore/log/notify with retry/backoff settings.

Important for modelling failure paths—not just happy paths.

### 3.11 Reference & Versioning

Definitions link via `{ key, domain, version, flow }` refs across tasks, views, schemas, flows, subprocesses, extensions.

System flow tokens include `sys-flows`, `sys-views`, `sys-functions`, `sys-tasks`, `sys-extensions`, `sys-schemas`.

Semver strategies keep runtime separate from authoring.

### 3.12 Instance & Persistence

Runtime executes instances, not mere definitions.

Important instance concepts: identifiers, versioning, etag, tags, payload, metadata, persisted history.

Architecturally: master instance records plus auditable histories; optionally domain-split storage.

---

## 4. How Definitions Are Structured

Envelope:

```json
{
  "key": "my-definition",
  "version": "1.0.0",
  "domain": "banking",
  "flow": "sys-flows",
  "flowVersion": "1.0.0",
  "tags": ["banking", "example"],
  "attributes": {}
}
```

Practical reminders:

- `key`: readable unique id
- `version`: semver
- `domain`: ownership boundary
- `flow`: which system collection owns the artifact
- `attributes`: the actual definition body

## 5. Minimal JSON Skeletons

### 5.1 Minimal Workflow

```json
{
  "key": "account-opening",
  "version": "1.0.0",
  "domain": "banking",
  "flow": "sys-flows",
  "flowVersion": "1.0.0",
  "tags": ["banking", "onboarding"],
  "attributes": {
    "type": "F",
    "startTransition": {
      "key": "start",
      "target": "draft",
      "triggerType": 1
    },
    "states": [
      {
        "key": "draft",
        "stateType": 1,
        "subType": 6,
        "transitions": [
          {
            "key": "submit",
            "from": "draft",
            "target": "completed",
            "triggerType": 0
          }
        ]
      },
      {
        "key": "completed",
        "stateType": 3,
        "subType": 1
      }
    ]
  }
}
```

### 5.2 Tasks Inside a Transition

```json
{
  "key": "submit",
  "from": "draft",
  "target": "completed",
  "triggerType": 0,
  "onExecutionTasks": [
    {
      "order": 1,
      "task": {
        "key": "notify-customer",
        "domain": "notification",
        "version": "1.0.0",
        "flow": "sys-tasks"
      }
    }
  ]
}
```

### 5.3 Minimal Schema

```json
{
  "key": "account-opening-master-schema",
  "version": "1.0.0",
  "domain": "banking",
  "flow": "sys-schemas",
  "flowVersion": "1.0.0",
  "tags": ["workflow", "schema"],
  "attributes": {
    "type": "workflow",
    "schema": {
      "type": "object",
      "properties": {
        "customerId": { "type": "string" },
        "amount": { "type": "number", "minimum": 0 }
      },
      "required": ["customerId"]
    }
  }
}
```

### 5.4 Minimal View

```json
{
  "key": "draft-form-view",
  "version": "1.0.0",
  "domain": "banking",
  "flow": "sys-views",
  "flowVersion": "1.0.0",
  "tags": ["view"],
  "attributes": {
    "type": "Json",
    "display": "full-page",
    "content": {
      "component": "AccountOpeningForm"
    }
  }
}
```

### 5.5 Minimal Extension

```json
{
  "key": "extension-customer-profile",
  "version": "1.0.0",
  "domain": "crm",
  "flow": "sys-extensions",
  "flowVersion": "1.0.0",
  "tags": ["profile"],
  "attributes": {
    "type": 4,
    "scope": 1,
    "task": {
      "order": 1,
      "task": {
        "key": "get-customer-profile",
        "domain": "crm",
        "version": "1.0.0",
        "flow": "sys-tasks"
      }
    }
  }
}
```

## 6. Major Feature Areas

Summaries:

### 6.1 Process Modeling
Versioned workflows, state machines, start/finish semantics, transitions, cancellations, timeouts, hierarchies.

### 6.2 Orchestration & Integration
HTTP, Dapr invocation/pubsub, scripts, spawning flows, triggering transitions, querying instance data/lists, timers/conditions.

### 6.3 Data & Validation
JSON Schema drafts, master/transition schemas, headers schema, camelCase payloads, mappings.

### 6.4 UI & Clients
Stateful views, selectors, locales, overrides.

### 6.5 Runtime APIs
Polling, data/view fetch, authorize endpoints, etag support, enrichment.

### 6.6 Enterprise Capabilities
Version strategies, domains, auditing, backoff, roles/queryRoles, nested error boundaries.

## 7. Product Roadmap Hints for Downstream Builders

Likely tooling surfaces:

- workflow editors/generators,
- validators for schema/view parity,
- client SDKs,
- monitoring dashboards / transition explorers,
- diff/version inspectors,
- task catalogue maintainers.

Guiding truths:

1. Treat everything as a state machine narrative.
2. Anchor durable contracts on master schema.
3. Prefer references over cloning definitions.
4. Derive UX from views + schemas + states, not workflows alone.
5. Place integration semantics in tasks + mappings.
6. Consume data via Functions; decorate via Extensions.

## 8. Design Rules of Thumb

- Keep flows purposeful and cohesive.
- State names encode business milestones.
- Transition names behave like verbs.
- Reusable tasks beat one-off payloads.
- Do not confuse extension vs function responsibilities.
- Treat schema as the source of truth.
- Keep subprocess vs subflow distinctions crisp.
- Define error boundaries as locally as feasible with global fallback.

## 9. Closing

vNext is a JSON-defined, versioned, stateful workflow runtime. Pillars:

`workflow • state • transition • task • schema • view • function • extension • mapping • reference/versioning • instance persistence • error boundary`

Use this primer to bootstrap tooling for modeling, authoring, integrating, deploying, observing, or governing vNext-based systems.
