using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Trigger409Mapping - Starts a workflow instance with a fixed key.
/// On the second call the runtime returns 409 Conflict, which the
/// task-level ErrorBoundary catches via errorCode "Task:409".
/// </summary>
public class Trigger409Mapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var startTask = task as StartTask;
        if (startTask == null)
            throw new InvalidOperationException("Task must be a StartTask");

        startTask.SetDomain("core");
        startTask.SetFlow("task-test-subflow");
        startTask.SetKey("fixed-eb-409-key");
        startTask.SetSync(false);
        startTask.SetBody(new { source = "error-boundary-409-test" });

        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return new ScriptResponse
        {
            Key = "trigger-409-handled",
            Data = new
            {
                conflictTestResult = new
                {
                    errorCode = "Task:409",
                    action = "Ignore",
                    handledAt = DateTime.UtcNow
                }
            },
            Tags = new[] { "error-boundary-test", "conflict", "409" }
        };
    }
}
