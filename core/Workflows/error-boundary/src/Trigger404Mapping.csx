using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Trigger404Mapping - Retrieves a deliberately nonexistent instance (dead UUID).
/// The runtime returns 404 NotFound, caught by errorCode "Task:404".
/// </summary>
public class Trigger404Mapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var getTask = task as GetInstanceDataTask;
        if (getTask == null)
            throw new InvalidOperationException("Task must be a GetInstanceDataTask");

        getTask.SetDomain("core");
        getTask.SetFlow("task-test-subflow");
        // Nonexistent instance → 404 NotFound
        getTask.SetInstance("00000000-dead-beef-0000-000000000000");

        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return new ScriptResponse
        {
            Key = "trigger-404-handled",
            Data = new
            {
                notFoundTestResult = new
                {
                    errorCode = "Task:404",
                    action = "Ignore",
                    handledAt = DateTime.UtcNow
                }
            },
            Tags = new[] { "error-boundary-test", "not-found", "404" }
        };
    }
}