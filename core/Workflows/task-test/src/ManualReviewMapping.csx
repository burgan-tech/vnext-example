using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Manual Review Mapping - Handles manual review state after notify action
/// </summary>
public class ManualReviewMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var data = context.Instance?.Data;
            
            return new ScriptResponse
            {
                Key = "manual-review-ready",
                Data = new
                {
                    manualReviewResult = new
                    {
                        reviewRequired = true,
                        originalError = data?.lastError,
                        notificationSent = true,
                        reviewRequestedAt = DateTime.UtcNow,
                        note = "Manual review required after Notify action from ErrorBoundary"
                    }
                },
                Tags = new[] { "error-boundary-test", "manual-review" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "manual-review-exception",
                Data = new { error = ex.Message }
            };
        }
    }
}

