using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Increment Approved Count Mapping - Increments the approved document counter
/// </summary>
public class IncrementApprovedCountMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var currentApprovedCount = (int)(context.Instance?.Data?.approvedCount ?? 0);
            var transitionData = context.Body;
            
            // Using Mockoon contract structure - contractId instead of documentId
            return new ScriptResponse
            {
                Key = "approved-count-incremented",
                Data = new
                {
                    approvedCount = currentApprovedCount + 1,
                    lastApprovedContractId = transitionData?.contractId,
                    lastApprovedContractName = transitionData?.contractName,
                    lastApprovedAt = DateTime.UtcNow
                },
                Tags = new[] { "contract", "document-approved" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "increment-approved-error",
                Data = new { error = ex.Message }
            };
        }
    }
}

