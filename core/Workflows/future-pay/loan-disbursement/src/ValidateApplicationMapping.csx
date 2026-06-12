using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// T1 (submit-application) order 1 — Script Task (type 7).
/// Validates the incoming loan application and seeds the master `application` section
/// plus a generated applicationId.
/// </summary>
public class ValidateApplicationMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var body = context.Body;
        var requestedAmount = (decimal?)(body?.requestedAmount) ?? 0m;
        if (requestedAmount <= 0)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "validate-application-invalid",
                Data = new { valid = false, reason = "requestedAmount must be greater than zero" },
                Tags = new[] { "validation", "failure" }
            });
        }

        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                application = new
                {
                    applicationId = $"APP-{Guid.NewGuid():N}".Substring(0, 16),
                    customerId = body?.customerId,
                    productType = body?.productType,
                    requestedAmount = body?.requestedAmount,
                    currency = body?.currency ?? "TRY",
                    termMonths = body?.termMonths,
                    purpose = body?.purpose,
                    monthlyIncome = body?.monthlyIncome
                }
            },
            Tags = new[] { "validation", "success" }
        });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse { Data = context.Body });
    }
}
