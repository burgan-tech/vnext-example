---
name: workflow-scaffold
description: Use when the user wants to create a new vNext Workflow end-to-end. Plans the state/transition graph, scaffolds the workflow JSON + .csx mapping files + .http test file, and chains to view-design / schema-design as needed.
---

# Workflow Scaffold

End-to-end scaffolding for a new vNext workflow. A workflow is a state machine — getting the states, transitions, and trigger types right up front saves rewriting later.

## Prerequisites

- Working directory is a vNext domain project (has `vnext.config.json`).
- The user can describe the business flow at a conceptual level (the "what happens when" story).

## Steps

### 1. Resolve paths from `vnext.config.json`

Capture:
- `paths.componentsRoot`
- `paths.workflows`
- `domain`
- `runtimeVersion` (for `.http` test file header)

Target folder: `{componentsRoot}/{paths.workflows}/{workflow-key}/`. Inside it: `{workflow-key}-workflow.json`, `src/` (for `.csx` mappings), and a `.http` test file.

### 2. Ask the workflow type

`attributes.type` values:
- **`F`** — Flow (standard top-level user-facing flow)
- **`S`** — Subflow (started from a parent workflow)
- **`P`** — Process (background / long-running)
- **`C`** — Core (system-level)

Verify the current set against Context7 (`"workflow attributes type values"`) if the user's case doesn't fit cleanly.

### 3. Map the states

Walk through the flow with the user. For each state capture:

- **Key** (kebab-case)
- **Is it final?** (`isFinal: true` ends the instance)
- **Has a view?** (if yes, note the view key — will resolve in step 6)
- **`onEntry` tasks?** (anything that must run when entering the state)

Visualize back to the user as a list before moving on.

### 4. Map the transitions

For each transition capture:

- **From state → to state**
- **`triggerType`**: `0` (manual / user action), `1` (auto / condition-evaluated), `2` (timer), `3` (event)
- **For auto transitions**: confirm complementary pair (mutually exclusive `rule` conditions) — a lone conditional auto transition is invalid; if there's only one, it must be unconditional.
- **For timer**: `duration` (ISO 8601, e.g. `PT15M`)
- **`onExecutionTasks`**: which tasks run during the transition, and which `.csx` mapping shapes each task's input/output

### 5. Identify the start transition

Exactly one initial transition; its `target` is the first state. Encode as `attributes.startTransition`.

### 6. Spin off dependencies (chain skills)

For each view referenced in step 3 that does not yet exist → invoke the `view-design` skill.
For each schema needed (workflow master schema, transition payloads) that does not yet exist → invoke the `schema-design` skill.

These can be deferred (scaffold the workflow first with placeholder refs, then fill in), but record what's pending.

### 7. Scaffold `.csx` mapping files

For each `onExecutionTasks` entry that needs input/output mapping:

- Create `src/{PascalCaseClassName}.csx` with a skeleton class.
- The workflow JSON's `mapping.location` points to `./src/{file}.csx`; the VS Code extension auto-encodes the file into `mapping.code` (base64) on save. **Do not manually base64-encode.**

### 8. Look at a sibling workflow

Read one existing workflow in this repo for envelope and reference style (e.g. `core/Workflows/account-opening/account-opening-workflow.json`). Especially confirm:
- Cross-component reference shape
- `timeout` structure
- `onExecutionTasks` ordering

### 9. Generate the workflow JSON

Envelope:

```json
{
  "key": "{workflow-key}",
  "version": "1.0.0",
  "domain": "{domain}",
  "flow": "sys-workflows",
  "flowVersion": "1.0.0",
  "tags": [],
  "attributes": {
    "type": "F",
    "timeout": { "key": "timeout", "target": "{state}", "duration": "PT15M" },
    "startTransition": { /* from step 5 */ },
    "states": [ /* from step 3, with transitions from step 4 */ ]
  }
}
```

Write to `{componentsRoot}/{paths.workflows}/{workflow-key}/{workflow-key}-workflow.json`.

### 10. Generate the `.http` test file

Create `{workflow-key}.http` next to the workflow JSON:

```http
@baseUrl = http://localhost:4201
@apiVersion = 1
@domain = {domain}

### Start instance
POST {{baseUrl}}/api/v{{apiVersion}}/{{domain}}/workflows/{workflow-key}/instances/start

### Get state
GET {{baseUrl}}/api/v{{apiVersion}}/{{domain}}/workflows/{workflow-key}/instances/{instanceKey}/functions/state

### Execute each manual transition (one block per transition)
PATCH {{baseUrl}}/api/v{{apiVersion}}/{{domain}}/workflows/{workflow-key}/instances/{instanceKey}/transitions/{transitionKey}
Content-Type: application/json

{ /* transition payload */ }
```

### 11. Validate

Run `npm run validate`. Hand off failures to the `validate-and-fix` skill.

### 12. (Optional) Mockoon routes

If transitions call HTTP tasks pointing at `localhost:3001`, add the corresponding Mockoon mock routes under `mockoon/{domain}/...`. Endpoint pattern: `api/{domain}/{resource}/{action}`. Include 2xx and 4xx/5xx scenarios with 500–1000 ms latency.

## Notes

- Exactly **one** initial state per workflow (enforced by validator).
- Auto transitions must come in complementary pairs (or be unconditional) — the validator catches this; the skill should catch it earlier in step 4.
- Never hardcode `core/Workflows/...` — always resolve from `vnext.config.json`.
- For component references, the shape is `{ "key", "domain", "flow", "version" }` — strict mode is on, so a wrong `flow` value will fail validation.
