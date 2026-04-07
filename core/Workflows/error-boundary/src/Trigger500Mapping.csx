using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Trigger500Mapping - Triggers a nonexistent transition on a nil-UUID instance.
/// The runtime returns 500 Failure (invalid state/transition),
/// caught by errorCode "Task:500". A wildcard fallback aborts to "error-sink".
/// </summary>
public class Trigger500Mapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var directTrigger = task as DirectTriggerTask;
        if (directTrigger == null)
            throw new InvalidOperationException("Task must be a DirectTriggerTask");

        directTrigger.SetDomain("core");
        directTrigger.SetFlow("task-test-subflow");
        directTrigger.SetTransitionName("nonexistent-transition");
        // Nil UUID → instance not found or invalid state → 500
        directTrigger.SetInstance("00000000-0000-0000-0000-000000000000");
        directTrigger.SetKey(Guid.NewGuid().ToString());
        directTrigger.SetSync(false);
        directTrigger.SetBody(new { source = "error-boundary-500-test" });

        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return new ScriptResponse
        {
            Key = "trigger-500-handled",
            Data = new
            {
                failureTestResult = new
                {
                    errorCode = "Task:500",
                    action = "Ignore",
                    handledAt = DateTime.UtcNow
                }
            },
            Tags = new[] { "error-boundary-test", "failure", "500" }
        };
    }
}