using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Auto-transition gate: routes to <c>documents-completed</c> when the batch settled with zero
/// failed items.
/// <para>
/// The exact complement of <c>PartialFailureRule</c>. The two are mutually exclusive by
/// construction, so declaration order between them carries no meaning — deliberately, because a
/// scenario whose outcome depends on which auto transition is evaluated first is testing the
/// evaluation order, not the fan-out.
/// </para>
/// <para>
/// It also requires the batch to have actually run (<c>total &gt; 0</c> or an explicit summary
/// present). Without that, an instance that somehow reached this state before the batch wrote
/// anything would read <c>failed == 0</c> and be declared a success — the fan-out's own
/// "missing itemsPath resolves to an EMPTY batch, not an error" behaviour makes that a real
/// shape, not a hypothetical one.
/// </para>
/// </summary>
public class AllSucceededRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance?.Data as IDictionary<string, object>;

        var settled = data != null &&
                      (data.ContainsKey("documentResultsSummary") || data.ContainsKey("documentsFailedCount"));
        var failed = ReadFailedCount(data);
        var satisfied = settled && failed == 0;

        LogInformation($"AllSucceededRule: settled={settled} failed={failed} satisfied={satisfied}");
        return Task.FromResult(satisfied);
    }

    private static int ReadFailedCount(IDictionary<string, object> data)
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
