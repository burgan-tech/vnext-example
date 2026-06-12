# api-tests

Per-workflow REST Client (`.http`) files for driving the vNext Runtime API by hand.

## Convention

One `.http` file per workflow, named after the workflow key (e.g. `account-opening.http`).
Each file demonstrates a full instance lifecycle: start → transitions → state queries →
functions → retry.

The pattern lives in the toolkit template
`templates/.http.tmpl` (in the `vnext-ai-toolkit` plugin). It uses these REST Client
variables — fill them per workflow:

```http
@baseUrl = http://localhost:4201
@apiVersion = 1
@domain = core
@workflowKey = <your-workflow-key>
```

The `workflow-scaffold` skill generates a matching `.http` file automatically when you
scaffold a new workflow, so you usually won't hand-author these.

## Running

Use the VS Code **REST Client** extension (or JetBrains HTTP client). Start the runtime
(`localhost:4201`) and MockLab (`localhost:3001`, `docker compose up -d mocklab`) first.
