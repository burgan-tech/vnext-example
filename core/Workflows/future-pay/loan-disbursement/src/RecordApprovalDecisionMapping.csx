using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// T4 (approve) order 1 — Script Task (type 7).
/// The loan-approval-decision payload is merged into instance data at root level by the runtime;
/// this mapping projects it into the master `approval` section (alongside the requiredApproverRole
/// that compute-required-approver wrote on state entry) and stamps decision = "approved".
/// </summary>
public class RecordApprovalDecisionMapping : ScriptBase, IMapping
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

        SetProperty(approval, "decision", "approved");
        SetProperty(approval, "decisionDate", DateTime.UtcNow.ToString("o"));

        // The optional strings are written ONLY when a value exists. Writing an explicit null
        // instead is what used to fail the master schema: approval.conditions (and its siblings)
        // are declared `type: "string"`, and null is not a string — so an approval without a
        // `conditions` field faulted the whole transition with "JSON schema validation failed".
        // Absent and null are not the same thing here; absent is what "not provided" means.
        SetIfPresent(approval, "decisionReason", ToStr(Get(data, "decisionReason")) ?? ToStr(Get(existing, "decisionReason")));
        SetIfPresent(approval, "conditions", ToStr(Get(data, "conditions")) ?? ToStr(Get(existing, "conditions")));
        SetIfPresent(approval, "approverUserId", ToStr(Get(data, "approverUserId")) ?? ToStr(Get(existing, "approverUserId")));

        var result = CreateObject();
        SetProperty(result, "approval", approval);

        LogInformation($"RecordApprovalDecisionMapping: decision=approved, approver={ToStr(Get(data, "approverUserId"))}");

        return Task.FromResult(new ScriptResponse
        {
            Data = result,
            Tags = new[] { "approval", "decision", "approved" }
        });
    }

    private static object Get(IDictionary<string, object> source, string key)
        => source != null && source.TryGetValue(key, out var value) ? value : null;

    /// <summary>Writes the property only when the value is non-null — see the note above.</summary>
    private void SetIfPresent(object target, string name, string value)
    {
        if (value != null)
            SetProperty(target, name, value);
    }

    private static string ToStr(object value)
    {
        var text = value?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
