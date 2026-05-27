# vNext Function — Script Mapping Pattern

> **Audience**: Anyone writing `.csx` mappings for vNext **functions** (`sys-functions`) — single-task or multi-task. Pair with workflow `.csx` mappings, which use a similar but distinct contract (`ITransitionMapping`).
> **Goal**: Avoid the recurring "function returns a double-wrapped or raw `StandardTaskResponse`" bug, and pick the right input source for GET vs. POST function calls.

---

## 1. The two function shapes

vNext has two function shapes; each uses a different mapping interface.

### 1a. Single-task function — `IMapping`

```jsonc
{
  "attributes": {
    "scope": "D",                        // I = Internal, D = Domain-callable (from views, etc.)
    "task": {
      "order": 1,
      "task": { "key": "<task-key>", "domain": "core", "version": "1.0.0", "flow": "sys-tasks" },
      "mapping": { "location": "./src/<PascalCase>.csx", "code": "<base64; VS Code populates>" }
    }
  }
}
```

The `.csx` implements `IMapping` (both `InputHandler` and `OutputHandler` in one class).

### 1b. Multi-task function — `onExecutionTasks[]` + `attributes.output`

```jsonc
{
  "attributes": {
    "scope": "I",
    "onExecutionTasks": [
      { "order": 1, "task": {...}, "mapping": { "location": "./src/Task1Mapping.csx", "code": "..." } },
      { "order": 2, "task": {...}, "mapping": { "location": "./src/Task2Mapping.csx", "code": "..." } }
    ],
    "output": { "location": "./src/CompositeOutput.csx", "code": "..." }
  }
}
```

Each task gets its own `IMapping` (`InputHandler` only — the per-task `OutputHandler` is bypassed in this shape). The final `output` mapping implements `IOutputHandler` and composes per-task responses via `context.OutputResponse["camelCaseTaskKey"].data`.

Reference: `core/Functions/account-opening/multi-task-function-test.json` + `src/FunctionOutputMapping.csx`.

---

## 2. Input parameter sources — GET vs. POST

vNext functions can be invoked two ways:

| HTTP verb | Endpoint | Where parameters land in `ScriptContext` |
|---|---|---|
| `POST` | `/api/v1/{domain}/.../functions/{key}` + JSON body | `context.Body?.<field>` |
| `GET` | `/api/v1/{domain}/.../functions/{key}?param=value` | `context.QueryString?["param"]` and/or `context.Headers?["param"]` |

**Renderer-initiated calls (x-lov, x-lookup, x-validation) typically use GET** — so reading from `context.Body?.<field>` alone is **wrong** for those functions; the body is empty and the parameter is silently null.

### Recommended resolver pattern

```csharp
private static string? ResolveParam(ScriptContext context, string name)
{
    // 1. GET function — query string (preferred for renderer-initiated calls)
    try
    {
        var qs = context.GetType().GetProperty("QueryString")?.GetValue(context);
        if (qs is IDictionary<string, string?> dict && dict.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v))
            return v;
    }
    catch { /* runtime may not expose QueryString — fall through */ }

    // 2. Header fallback (renderer can pass filters as headers)
    try
    {
        var hv = context.Headers?[name]?.ToString();
        if (!string.IsNullOrEmpty(hv)) return hv;
    }
    catch { }

    // 3. POST function — invoke body (back-compat)
    try
    {
        var bv = context.Body?[name]?.ToString();
        if (!string.IsNullOrEmpty(bv)) return bv;
    }
    catch { }

    return null;
}
```

Use this helper inside `InputHandler` and feed the value into `httpTask.SetUrl(...)` (for query params) or `httpTask.SetBody(...)` (for POST tasks).

**Anti-pattern**: `var code = context.Body?.code?.ToString();` — fails for GET-invoked functions.

---

## 3. Output unwrapping — beat the StandardTaskResponse

When a function call completes, vNext serialises the upstream task's result as a `StandardTaskResponse`:

```jsonc
{
  "getBranches": {                       // function-level wrapper (runtime-generated)
    "data": <ScriptResponse.Data>,           //   ← what your OutputHandler returns
    "body": "<raw response body string>",
    "statusCode": 200,
    "isSuccess": true,
    "headers": { ... },
    "metadata": { "url": "...", "method": "GET", "reasonPhrase": "OK" },
    "executionDurationMs": 686,
    "taskType": "http",
    "json": "{}",  "normalizedJson": "{}",  "jsonElement": {}
  }
}
```

Inside `OutputHandler`, **`context.Body` is the parsed HTTP response body** of the upstream task — not the StandardTaskResponse. So if MockLab returns `{"data":[...]}`, then `context.Body.data` is the array directly.

### The double-wrap mistake

```csharp
// ❌ BAD — returns context.Body raw → response shape becomes data.data[*]
return Task.FromResult(new ScriptResponse { Key = "...", Data = context.Body });
```

Renderer's `x-lov` JsonPath `$.data[*].code` then fails silently because the array lives at `$.data.data[*]`.

### The clean shape

```csharp
public Task<ScriptResponse> OutputHandler(ScriptContext context)
{
    try
    {
        var statusCode = (int?)(context.Body?.statusCode) ?? 200;
        dynamic payload = context.Body?.data ?? context.Body;        // unwrap one HTTP body layer
        dynamic items = null;
        try { items = payload?.data ?? payload; } catch { items = payload; }

        if (statusCode >= 200 && statusCode < 300 && items != null)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "branches-lov",
                Data = new { data = items },                          // explicit envelope; predictable JsonPath
                Tags = new[] { "lov", "account-opening", "success" }
            });
        }

        return Task.FromResult(new ScriptResponse
        {
            Key = "branches-lov-failure",
            Data = new { error = "Failed", statusCode = statusCode },
            Tags = new[] { "lov", "account-opening", "failure" }
        });
    }
    catch (Exception ex)
    {
        return Task.FromResult(new ScriptResponse
        {
            Key = "branches-lov-exception",
            Data = new { error = ex.Message },
            Tags = new[] { "lov", "account-opening", "exception" }
        });
    }
}
```

**The renderer's `x-lov` then evaluates `$.data[*].code` against `getBranches.data.data` — clean and predictable.**

---

## 4. Why `Data = new { data = items }` and not `Data = items`?

If the upstream returns an array and you set `Data = items` directly, the StandardTaskResponse wrapper would expose `getBranches.data = [...]` — and `x-lov.valueField: "$.data[*].code"` would resolve, but `$.data` on its own (used by `x-lookup.resultField`) would be ambiguous for object results.

Wrapping under `{ data: items }` keeps a **uniform envelope** across LOV (array) and lookup (object) results. Schema authors write JsonPath against `$.data` or `$.data[*]` without having to know whether the upstream returned a list or a single record.

This matches the MockLab seed convention (`{"data": [...] }` for LOV, `{"data": {...}}` for lookup).

---

## 5. Tags + error semantics

Use `ScriptResponse.Tags` for downstream filtering (`success`, `failure`, `exception`, `not-found`). Match on HTTP `statusCode` from `context.Body?.statusCode`:

- `2xx` + non-null payload → success path
- `4xx` (esp. 404) → not-found / failure path
- exception → exception path with `error` field

Renderer doesn't currently branch on Tags, but log aggregators and downstream tasks do.

---

## 6. HTTP task helpers (`HttpTask` injection)

In `InputHandler`, cast the `WorkflowTask` to `HttpTask` and mutate before invocation:

| Method | Purpose |
|---|---|
| `httpTask.SetUrl(string)` | Replace the URL (template substitution, query string append, etc.) |
| `httpTask.SetHeaders(Dictionary<string, string?>)` | Replace request headers |
| `httpTask.SetBody(object)` | Replace the request body (POST/PUT/PATCH) |

Always wrap in `try/catch` and check for `null` on the cast — script tasks (non-HTTP) won't cast.

---

## 7. Minimal LOV function template

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class GetMyLovMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var httpTask = task as HttpTask;
            if (httpTask == null)
                throw new InvalidOperationException("Task must be an HttpTask");

            var filterValue = ResolveParam(context, "filterParamName");
            if (!string.IsNullOrEmpty(filterValue))
            {
                var sep = httpTask.Url.Contains("?") ? "&" : "?";
                httpTask.SetUrl($"{httpTask.Url}{sep}filterParamName={filterValue}");
            }

            httpTask.SetHeaders(new Dictionary<string, string?>
            {
                ["Accept"] = "application/json",
                ["X-Request-Id"] = context.Headers?["x-request-id"] ?? Guid.NewGuid().ToString()
            });

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "my-lov-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var statusCode = (int?)(context.Body?.statusCode) ?? 200;
            dynamic payload = context.Body?.data ?? context.Body;
            dynamic items = null;
            try { items = payload?.data ?? payload; } catch { items = payload; }

            if (statusCode >= 200 && statusCode < 300 && items != null)
            {
                return Task.FromResult(new ScriptResponse
                {
                    Key = "my-lov",
                    Data = new { data = items },
                    Tags = new[] { "lov", "success" }
                });
            }

            return Task.FromResult(new ScriptResponse
            {
                Key = "my-lov-failure",
                Data = new { error = "Failed", statusCode = statusCode },
                Tags = new[] { "lov", "failure" }
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "my-lov-exception",
                Data = new { error = ex.Message },
                Tags = new[] { "lov", "exception" }
            });
        }
    }

    private static string? ResolveParam(ScriptContext context, string name)
    {
        try
        {
            var qs = context.GetType().GetProperty("QueryString")?.GetValue(context);
            if (qs is IDictionary<string, string?> dict && dict.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v))
                return v;
        }
        catch { }
        try { var hv = context.Headers?[name]?.ToString(); if (!string.IsNullOrEmpty(hv)) return hv; } catch { }
        try { var bv = context.Body?[name]?.ToString(); if (!string.IsNullOrEmpty(bv)) return bv; } catch { }
        return null;
    }
}
```

Working examples in `core/Functions/account-opening/src/GetBranchesLovMapping.csx` (LOV cascade) and `GetBranchDetailLookupMapping.csx` (lookup).
