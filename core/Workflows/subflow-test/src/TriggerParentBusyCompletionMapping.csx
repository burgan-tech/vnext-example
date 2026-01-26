using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// Trigger Parent Busy Completion Mapping - Triggers the parent workflow to exit busy state
/// This mapping uses DirectTriggerTask to trigger the 'complete-busy-state' transition on the parent
/// </summary>
public class TriggerParentBusyCompletionMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var directTriggerTask = task as DirectTriggerTask;
            
            LogInformation("TriggerParentBusyCompletionMapping - Preparing to trigger parent workflow busy completion");
            
            // Get parent instance ID from subprocess input data
            var parentInstanceId = context.Instance?.Data?.parentInstanceId;
            
            if (string.IsNullOrEmpty(parentInstanceId))
            {
                LogError("TriggerParentBusyCompletionMapping - Parent instance ID not found");
                return Task.FromResult(new ScriptResponse
                {
                    Key = "trigger-error",
                    Data = new { error = "Parent instance ID not found" }
                });
            }
            
            // Set the target instance and transition
            directTriggerTask.SetDomain("core");
            directTriggerTask.SetFlow("sys-flows");
            directTriggerTask.SetInstance(parentInstanceId);
            directTriggerTask.SetTransitionName("complete-busy-state");
            
            // Prepare transition payload
            directTriggerTask.SetBody(new
            {
                subprocessCompleted = true,
                subprocessInstanceId = context.Instance?.Id,
                completedAt = DateTime.UtcNow,
                result = new
                {
                    success = true,
                    message = "Subprocess completed successfully, triggering parent busy state completion",
                    processingTime = "5 seconds",
                    data = new
                    {
                        busyStateTestPassed = true,
                        subprocessExecuted = true,
                        parentTriggered = true
                    }
                }
            });
            
            LogInformation("TriggerParentBusyCompletionMapping - Triggering transition 'complete-busy-state' on parent instance {0}", args: new object?[] { parentInstanceId });
            
            return Task.FromResult(new ScriptResponse
            {
                Data = context.Instance?.Data
            });
        }
        catch (Exception ex)
        {
            LogError("TriggerParentBusyCompletionMapping - Error: {0}", args: new object?[] { ex.Message });
            return Task.FromResult(new ScriptResponse
            {
                Key = "trigger-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var response = new ScriptResponse();
            
            if (context.Body?.isSuccess == true)
            {
                LogInformation("TriggerParentBusyCompletionMapping - Parent transition triggered successfully");
                
                response.Data = new
                {
                    transitionTriggered = true,
                    triggeredAt = DateTime.UtcNow,
                    parentNewState = context.Body.data?.currentState,
                    status = "PARENT_TRANSITION_SUCCESS",
                    message = "Parent workflow successfully transitioned from busy state"
                };
            }
            else
            {
                LogError("TriggerParentBusyCompletionMapping - Parent transition failed: {0}", args: new object?[] { context.Body?.errorMessage });
                
                response.Data = new
                {
                    transitionTriggered = false,
                    error = context.Body?.errorMessage,
                    status = "PARENT_TRANSITION_FAILED"
                };
            }
            
            return response;
        }
        catch (Exception ex)
        {
            LogError("TriggerParentBusyCompletionMapping - Output handler error: {0}", args: new object?[] { ex.Message });
            return new ScriptResponse
            {
                Key = "output-error",
                Data = new { error = ex.Message }
            };
        }
    }
}
