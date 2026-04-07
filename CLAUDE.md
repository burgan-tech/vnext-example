# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

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

This is a **vNext domain-driven workflow automation system**. The "core" domain defines workflows, tasks, views, schemas, functions, and extensions as JSON component files, which are deployed to a vNext Runtime engine.

### Component Root

All components live under `core/` organized by type:
- `core/Workflows/` — State machine definitions; each workflow folder may contain a `src/` subfolder for C# mapping files (`.csx`)
- `core/Tasks/` — Reusable task definitions (HTTP, subprocess, script, service)
- `core/Views/` — UI component definitions bound to workflow states
- `core/Schemas/` — JSON Schema definitions for data validation
- `core/Functions/` — Reusable business logic callable from workflows
`core/Extensions/` — Runtime capability extensions

### Standard Component JSON Shape

Every component regardless of type follows this envelope:

```json
{
  "key": "kebab-case-name",
  "version": "1.0.0",
  "domain": "core",
  "flow": "sys-workflows",
  "flowVersion": "1.0.0",
  "tags": ["searchable", "tags"],
  "attributes": { /* type-specific content */ }
}
```

**Flow type values**: `sys-workflows`, `sys-tasks`, `sys-schemas`, `sys-views`, `sys-functions`, `sys-extensions`

### C# Mapping Files

Workflows reference C# script files (`.csx`) located in `src/` folders next to the workflow JSON. The vNext VS Code extension automatically handles base64-encoding these files into the workflow JSON when saving. **Never manually convert `.csx` files to base64.**

### Build Outputs

- **Runtime build** (`@burgan-tech/vnext-core-runtime`) — Complete domain structure for engine deployment
- **Reference build** (`@burgan-tech/vnext-core-reference`) — Exported components only, for cross-domain usage

### Local Development Servers

| Server | Port | Purpose |
|--------|------|---------|
| vNext Runtime | `localhost:4201` | Workflow engine; use for instance start, transitions, state queries |
| Mockoon | `localhost:3001` | External API mocks; all HTTP tasks point here |

**Never** call the Mockoon API (`localhost:3001`) for workflow operations, and **never** hardcode production URLs in HTTP task configs — always use Mockoon during development.

### HTTP Test Files

Every workflow should have a `.http` test file demonstrating full instance progression. vNext Runtime API endpoints follow this pattern:

```http
@baseUrl = http://localhost:4201
@apiVersion = 1
@domain = core

### Start instance
POST {{baseUrl}}/api/v{{apiVersion}}/{{domain}}/workflows/{workflow-key}/instances/start

### Execute transition
PATCH {{baseUrl}}/api/v{{apiVersion}}/{{domain}}/workflows/{workflow-key}/instances/{instanceKey}/transitions/{transitionKey}

### Get state (long polling)
GET {{baseUrl}}/api/v{{apiVersion}}/{{domain}}/workflows/{workflow-key}/instances/{instanceKey}/functions/state
```

### Mockoon Mock Organization

When adding new routes to `mockoon/`, always create a **folder with the domain name first**, then place routes inside. Endpoint pattern: `api/{domain}/{resource}/{action}`. Include success (2xx) and error (4xx/5xx) response scenarios with realistic latency (500–1000ms).

## Critical Rules

- Each workflow must have **exactly one** initial state (defined by `startTransition.target`).
- Auto transitions (`triggerType: 1`) must come in complementary pairs with mutually exclusive conditions. A lone auto transition is only valid if its rule always returns true (unconditional).
- All component references use the format: `{domain}/{component-type}/{key}/{version}` — strict mode is enabled in `vnext.config.json`.
- Run `npm run validate` after any component change. It validates JSON syntax and schema compliance for all components.
- JSON files use **2-space indentation**, double quotes, no trailing commas.
- All keys and file names use **kebab-case**; C# class names use **PascalCase**.
