using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Trigger400Mapping - Starts a workflow that has schema validation with an empty body.
/// Missing required fields cause the runtime to return 400 Validation,
/// caught by errorCode "Task:400".
/// </summary>
public class Trigger400Mapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var startTask = task as StartTask;
        if (startTask == null)
            throw new InvalidOperationException("Task must be a StartTask");

        startTask.SetDomain("core");
        startTask.SetFlow("account-opening-workflow");
        startTask.SetKey(Guid.NewGuid().ToString());
        startTask.SetSync(false);
        // Empty body → schema required fields are missing → Validation 400
        startTask.SetBody(new { });

        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return new ScriptResponse
        {
            Key = "trigger-400-handled",
            Data = new
            {
                validationTestResult = new
                {
                    errorCode = "Task:400",
                    action = "Ignore",
                    handledAt = DateTime.UtcNow
                }
            },
            Tags = new[] { "error-boundary-test", "validation", "400" }
        };
    }
}
