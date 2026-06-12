using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// approval state onEntry order 1 — Script Task (type 7).
/// Model C-1: derives the required approver role from the approved limit and writes it to
/// approval.requiredApproverRole. The approval state/transition roleGrant references this value
/// via JSONPath ($.data.approval.requiredApproverRole) for dynamic, limit-based authorization.
/// </summary>
public class ComputeRequiredApproverMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var data = context.Instance?.Data;
        decimal approvedLimit = 0m;
        try { approvedLimit = (decimal?)(data?.assessment?.approvedLimit) ?? 0m; } catch { }

        // Tiered approval authority (Model C-1).
        string role;
        if (approvedLimit <= 100000m) role = "core.onay-sube";
        else if (approvedLimit <= 500000m) role = "core.onay-bolge";
        else if (approvedLimit <= 2000000m) role = "core.onay-genel-mudur-yardimcisi";
        else role = "core.onay-kredi-komitesi";

        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                approval = new { requiredApproverRole = role }
            },
            Tags = new[] { "approval", "required-approver" }
        });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse { Data = context.Body });
    }
}
