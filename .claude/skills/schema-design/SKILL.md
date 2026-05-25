---
name: schema-design
description: Use when the user wants to create or modify a vNext Schema component. Interactively gathers fields, types, validation rules, localization (x-labels), and role-based access (roles[]) before producing JSON Schema draft 2020-12 at the path resolved from vnext.config.json.
---

# Schema Design

Schemas drive view rendering, transition validation, and task I/O. **Never** produce schema JSON before gathering the field shape from the user — guessing leads to schemas that fail validation or mismatch the workflow.

## Prerequisites

- Working directory is a vNext domain project (has `vnext.config.json`).
- The user has a rough idea of what data the schema must describe (a workflow state's data, a transition payload, task I/O, etc.).

## Steps

### 1. Resolve paths from `vnext.config.json`

Read the repo-root `vnext.config.json` and capture:
- `paths.componentsRoot`
- `paths.schemas`
- `domain`

Target write path: `{componentsRoot}/{paths.schemas}/{domain-subfolder}/{key}.json`.

### 2. Classify the schema

Ask the user which category — this shapes `attributes.type` and the surrounding usage:

- **`workflow`** — the master data shape for a workflow instance
- **`transition`** — the payload required to execute a specific transition
- **`task-input`** / **`task-output`** — bound to a task's mapping
- **`view-data`** — drives a view's `dataSchema`

### 3. Gather the field list (interactive)

Walk through each field with the user. For every field, capture:

- **Name** (kebab-case in JSON keys, camelCase if user prefers — confirm convention)
- **Type** — `string`, `number`, `integer`, `boolean`, `array`, `object`
- **Required?**
- **Format / constraints** — `format: email`, `pattern`, `minLength`, `maxLength`, `minimum`, `maximum`
- **Enum values** if applicable
- **Default**

For nested objects, recurse. For arrays, ask the item shape.

### 4. Ask about localization

Does each field need bilingual labels? If yes, add `x-labels`:

```json
"properties": {
  "currency": {
    "type": "string",
    "x-labels": { "tr": "Para Birimi", "en": "Currency" }
  }
}
```

### 5. Ask about role-based access

Does any field need restricted visibility? If yes, add `roles[]`:

```json
"branchCode": {
  "type": "string",
  "roles": [ { "role": "$PreviousUser", "grant": "allow" } ]
}
```

Built-in roles include `$PreviousUser`, `$CurrentUser`. Confirm exact role tokens with Context7 (`"schema roles field access"`) if uncertain.

### 6. Look at a sibling example

Read one existing schema in the same domain folder for envelope reference (e.g. `core/Schemas/account-opening/account-opening-master.json`). Confirm the `$id` URN pattern used in this repo (this project uses `urn:amorphie:res:schema:{domain}:{key}`).

### 7. Generate the schema JSON

Envelope:

```json
{
  "key": "{key}",
  "version": "1.0.0",
  "domain": "{domain}",
  "flow": "sys-schemas",
  "flowVersion": "1.0.0",
  "tags": [],
  "attributes": {
    "type": "{workflow|transition|...}",
    "schema": {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "$id": "urn:amorphie:res:schema:{domain}:{key}",
      "type": "object",
      "required": [ /* names */ ],
      "properties": { /* fields from step 3-5 */ }
    }
  }
}
```

### 8. Write the file

Write to `{componentsRoot}/{paths.schemas}/{domain-subfolder}/{key}.json`.

### 9. Validate

Run `npm run validate`. Hand off failures to the `validate-and-fix` skill.

### 10. Wire references (if requested)

If the user wants the schema referenced from a view's `dataSchema` or a workflow's transition payload, edit the target JSON and re-validate.

## Notes

- JSON Schema draft used is **2020-12** — confirm the `$schema` URL matches.
- `$id` is a **URN** (`urn:amorphie:res:schema:{domain}:{key}`), not an HTTP URL — the runtime resolves it against the registered schema set, never via fetch. The URN must match the value used by any view's `dataSchema` or workflow transition payload reference. Keep it stable across versions.
- `x-labels` and `roles[]` are vNext extensions to JSON Schema, not standard keywords — they're consumed by the runtime and view layer.
- Never hardcode `core/Schemas/...` — always resolve from `vnext.config.json`.
