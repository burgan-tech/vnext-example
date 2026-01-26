using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// Busy SubProcess Start Mapping - Initializes the subprocess that will run while parent is busy
/// This subprocess will complete its work and trigger the parent to exit busy state
/// </summary>
public class BusySubprocessStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("BusySubprocessStartMapping - Starting busy state subprocess");
            
            var inputData = context.Instance?.Data;
            
            return new ScriptResponse
            {
                Key = "busy-subprocess-started",
                Data = new
                {
                    subprocessWorkflowId = context.Workflow?.Key,
                    subprocessInstanceId = context.Instance?.Id,
                    parentInstanceId = inputData?.grandchildInstanceId,
                    parentWorkflowId = inputData?.grandchildWorkflowId,
                    startedAt = DateTime.UtcNow,
                    status = "processing",
                    message = "Busy state subprocess started - will trigger parent when complete",
                    processingTask = new
                    {
                        taskType = "background-processing",
                        expectedDuration = "5-10 seconds",
                        willTriggerParent = true
                    }
                },
                Tags = new[] { "busy-subprocess", "started", "background-task" }
            };
        }
        catch (Exception ex)
        {
            LogError("BusySubprocessStartMapping - Error: {0}", args: new object?[] { ex.Message });
            return new ScriptResponse
            {
                Key = "busy-subprocess-start-error",
                Data = new { error = ex.Message }
            };
        }
    }
}
