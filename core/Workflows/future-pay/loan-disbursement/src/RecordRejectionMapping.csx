using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// T5 (reject) order 1 — Script Task (type 7).
/// The loan-rejection payload is merged into instance data at root level by the runtime; this
/// mapping projects it into the master `approval` section so the rejected-result view can read
/// approval.decisionReason, and stamps decision = "rejected".
/// </summary>
public class RecordRejectionMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        // Script Task: nothing to configure, nothing to persist.
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance?.Data as IDictionary<string, object>;
        var existing = Get(data, "approval") as IDictionary<string, object>;

        var approval = CreateObject();
        if (existing != null)
        {
            foreach (var entry in existing)
                SetProperty(approval, entry.Key, entry.Value);
        }

        SetProperty(approval, "decision", "rejected");
        SetProperty(approval, "decisionReason", ToStr(Get(data, "rejectionReason")) ?? ToStr(Get(existing, "decisionReason")));
        SetProperty(approval, "rejectionCode", ToStr(Get(data, "rejectionCode")) ?? ToStr(Get(existing, "rejectionCode")));
        SetProperty(approval, "decisionDate", DateTime.UtcNow.ToString("o"));

        var result = CreateObject();
        SetProperty(result, "approval", approval);

        LogInformation($"RecordRejectionMapping: decision=rejected, code={ToStr(Get(data, "rejectionCode"))}");

        return Task.FromResult(new ScriptResponse
        {
            Data = result,
            Tags = new[] { "approval", "decision", "rejected" }
        });
    }

    private static object Get(IDictionary<string, object> source, string key)
        => source != null && source.TryGetValue(key, out var value) ? value : null;

    private static string ToStr(object value)
    {
        var text = value?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
