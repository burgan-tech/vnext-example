using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Increment Ready Count Mapping - Increments the ready document counter
/// </summary>
public class IncrementReadyCountMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var currentReadyCount = (int)(context.Instance?.Data?.readyCount ?? 0);
            var transitionData = context.Body;
            
            // Using Mockoon contract structure - contractId instead of documentId
            return new ScriptResponse
            {
                Key = "ready-count-incremented",
                Data = new
                {
                    readyCount = currentReadyCount + 1,
                    lastReadyContractId = transitionData?.contractId,
                    lastReadyContractName = transitionData?.contractName,
                    lastReadyAt = DateTime.UtcNow
                },
                Tags = new[] { "contract", "document-ready" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "increment-ready-error",
                Data = new { error = ex.Message }
            };
        }
    }
}

