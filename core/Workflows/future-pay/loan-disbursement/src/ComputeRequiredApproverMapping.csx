using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// approval state onEntry order 1 — Script Task (type 7).
/// Model C-1: derives the required approver role from the approved limit and writes it to
/// approval.requiredApproverRole. The approval state/transition roleGrant references this value
/// via JSONPath ($.data.approval.requiredApproverRole) for dynamic, limit-based authorization.
/// Written from OutputHandler — a Script Task's InputHandler result is not persisted — and any
/// approval keys already on the instance are preserved.
/// </summary>
public class ComputeRequiredApproverMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        // Script Task: nothing to configure, nothing to persist.
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance?.Data as IDictionary<string, object>;
        var assessment = Get(data, "assessment") as IDictionary<string, object>;
        var existing = Get(data, "approval") as IDictionary<string, object>;

        var approvedLimit = ToNumber(Get(assessment, "approvedLimit"))
            ?? ToNumber(Get(data, "approvedLimit"))
            ?? 0m;

        // Tiered approval authority (Model C-1).
        string role;
        if (approvedLimit <= 100000m) role = "core.onay-sube";
        else if (approvedLimit <= 500000m) role = "core.onay-bolge";
        else if (approvedLimit <= 2000000m) role = "core.onay-genel-mudur-yardimcisi";
        else role = "core.onay-kredi-komitesi";

        var approval = CreateObject();
        if (existing != null)
        {
            foreach (var entry in existing)
                SetProperty(approval, entry.Key, entry.Value);
        }
        SetProperty(approval, "requiredApproverRole", role);

        var result = CreateObject();
        SetProperty(result, "approval", approval);

        LogInformation($"ComputeRequiredApproverMapping: approvedLimit={approvedLimit}, requiredApproverRole={role}");

        return Task.FromResult(new ScriptResponse
        {
            Data = result,
            Tags = new[] { "approval", "required-approver" }
        });
    }

    private static object Get(IDictionary<string, object> source, string key)
        => source != null && source.TryGetValue(key, out var value) ? value : null;

    // Only used for the tier comparison. System.Globalization is not available to the sandbox.
    private static decimal? ToNumber(object value)
    {
        if (value == null) return null;
        try { return Convert.ToDecimal(value); } catch { return null; }
    }
}
