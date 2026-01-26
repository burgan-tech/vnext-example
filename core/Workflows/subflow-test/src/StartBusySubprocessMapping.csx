using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// Start Busy SubProcess Mapping - Starts the subprocess when grandchild enters busy state
/// This mapping configures the SubProcessTask to launch an independent subprocess
/// </summary>
public class StartBusySubprocessMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var subProcessTask = task as SubProcessTask;
            
            LogInformation("StartBusySubprocessMapping - Preparing to start busy subprocess");
            
            // Configure subprocess
            subProcessTask.SetDomain("core");
            subProcessTask.SetFlow("sys-flows");
            subProcessTask.SetKey("busy-subprocess-workflow");
            
            // Prepare subprocess data - include parent instance ID so subprocess can trigger back
            subProcessTask.SetBody(new
            {
                grandchildInstanceId = context.Instance?.Id,
                grandchildWorkflowId = context.Workflow?.Key,
                parentInstanceId = context.Instance?.Data?.childInstanceId,
                startedAt = DateTime.UtcNow,
                taskType = "busy-state-processing",
                context = new
                {
                    nestingLevel = 3,
                    testType = "busy-state-test",
                    expectedBehavior = "subprocess-will-trigger-parent"
                }
            });
            
            LogInformation("StartBusySubprocessMapping - Subprocess configured and ready to launch");
            
            return Task.FromResult(new ScriptResponse
            {
                Data = context.Instance?.Data
            });
        }
        catch (Exception ex)
        {
            LogError("StartBusySubprocessMapping - Error: {0}", args: new object?[] { ex.Message });
            return Task.FromResult(new ScriptResponse
            {
                Key = "subprocess-start-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("StartBusySubprocessMapping - Subprocess launched successfully");
            
            // SubProcess is fire-and-forget, just track that it was initiated
            return new ScriptResponse
            {
                Data = new
                {
                    subprocessLaunched = true,
                    subprocessInstanceId = context.Body?.data?.instanceId,
                    launchedAt = DateTime.UtcNow,
                    status = "BUSY",
                    message = "Subprocess launched - grandchild now in busy state waiting for subprocess to complete"
                }
            };
        }
        catch (Exception ex)
        {
            LogError("StartBusySubprocessMapping - Output handler error: {0}", args: new object?[] { ex.Message });
            return new ScriptResponse
            {
                Key = "output-error",
                Data = new { error = ex.Message }
            };
        }
    }
}
