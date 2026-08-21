using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Auto-transition gate: routes to <c>documents-partial-failure</c> when the batch settled with
/// at least one failed item.
/// <para>
/// This is the recommended partial-failure pattern for fan-out and the reason
/// <c>join.policy: "allSettled"</c> exists: the FanOut task itself always succeeds, partial
/// failure is DATA rather than an error, and <c>RunAutomaticTransitionsStep</c> (order 90) — which
/// runs after the state's onEntry tasks (order 60) — branches on the summary the batch wrote.
/// The platform never decides this for you.
/// </para>
/// <para>
/// The read is tolerant on purpose. A nested object in the instance-data snapshot can arrive as
/// an <c>IDictionary&lt;string, object&gt;</c> or as a <c>JsonElement</c> depending on whether the
/// snapshot came straight from the writing task or was rehydrated from the persisted row. A
/// branching rule is the worst possible place to discover which, so it handles both and falls
/// back to the flat <c>documentsFailedCount</c> mirror the mapping also writes.
/// </para>
/// </summary>
public class PartialFailureRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance?.Data as IDictionary<string, object>;
        var failed = ReadFailedCount(data);
        var satisfied = failed > 0;

        LogInformation($"PartialFailureRule: failed={failed} satisfied={satisfied}");
        return Task.FromResult(satisfied);
    }

    internal static int ReadFailedCount(IDictionary<string, object> data)
    {
        if (data == null) return 0;

        if (data.TryGetValue("documentResultsSummary", out var summary) && summary != null)
        {
            var nested = ReadInt(summary, "failed");
            if (nested.HasValue) return nested.Value;
        }

        if (data.TryGetValue("documentsFailedCount", out var flat) && flat != null &&
            int.TryParse(flat.ToString(), out var flatValue))
        {
            return flatValue;
        }

        return 0;
    }

    private static int? ReadInt(object container, string property)
    {
        var map = container as IDictionary<string, object>;
        if (map != null && map.TryGetValue(property, out var raw) && raw != null &&
            int.TryParse(raw.ToString(), out var parsed))
        {
            return parsed;
        }

        if (container is JsonElement element && element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var jsonValue) && jsonValue.TryGetInt32(out var jsonParsed))
        {
            return jsonParsed;
        }

        return null;
    }
}
