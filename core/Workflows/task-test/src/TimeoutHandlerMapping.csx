using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Timeout Handler Mapping - Handles timeout scenarios
/// </summary>
public class TimeoutHandlerMapping : IMapping
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
                Key = "timeout-handled",
                Data = new
                {
                    timeoutResult = new
                    {
                        timeoutHandled = true,
                        originalTimeout = data?.timeoutInfo,
                        handlerAction = "abort-and-notify",
                        handledAt = DateTime.UtcNow,
                        note = "Timeout was caught by ErrorBoundary.onTimeout policy"
                    }
                },
                Tags = new[] { "error-boundary-test", "timeout-handler" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "timeout-handler-exception",
                Data = new { error = ex.Message }
            };
        }
    }
}

