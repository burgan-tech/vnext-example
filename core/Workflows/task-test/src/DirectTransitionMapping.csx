using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// DirectTransition Task Mapping - Triggers transition on existing workflow instance
/// Test case: Verify workflow-to-workflow transition triggering
/// </summary>
public class DirectTransitionMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var directTriggerTask = task as DirectTriggerTask;
            if (directTriggerTask == null)
            {
                throw new InvalidOperationException("Task must be a DirectTriggerTask");
            }

            // Configure target workflow
            directTriggerTask.SetDomain("core");
            directTriggerTask.SetFlow("task-test-subflow");
            directTriggerTask.SetTransitionName("complete-subflow");
            directTriggerTask.SetTags(new[] { "task-test", "direct-transition", "success" });
            directTriggerTask.SetSync(true);

            // Get instance ID from workflow data
            var targetInstanceId = context.Instance?.Data?.subflowInstanceId?.ToString();
            if (!string.IsNullOrEmpty(targetInstanceId))
            {
                directTriggerTask.SetInstance(targetInstanceId);
            }
            directTriggerTask.SetKey(context.Instance?.Key ?? Guid.NewGuid().ToString());

            // Prepare transition body
            var transitionBody = new
            {
                completedBy = "task-test-workflow",
                completedAt = DateTime.UtcNow,
                parentInstanceId = context.Instance?.Id,
                message = "DirectTransition test executed"
            };
            directTriggerTask.SetBody(transitionBody);

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "direct-transition-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var response = context.Body;

            if (response?.isSuccess == true)
            {
                return new ScriptResponse
                {
                    Key = "direct-transition-success",
                    Data = new
                    {
                        directTransitionResult = new
                        {
                            success = true,
                            instanceId = response?.data?.id,
                            status = response?.data?.status,
                            executedAt = DateTime.UtcNow,
                            taskType = "DirectTransition"
                        }
                    },
                    Tags = new[] { "task-test", "direct-transition", "success" }
                };
            }

            return new ScriptResponse
            {
                Key = "direct-transition-failed",
                Data = new
                {
                    directTransitionResult = new
                    {
                        success = false,
                        error = response?.errorMessage ?? "Direct transition failed",
                        failedAt = DateTime.UtcNow,
                        taskType = "DirectTransition"
                    }
                },
                Tags = new[] { "task-test", "direct-transition", "failed" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "direct-transition-exception",
                Data = new
                {
                    directTransitionResult = new
                    {
                        success = false,
                        error = ex.Message,
                        taskType = "DirectTransition"
                    }
                }
            };
        }
    }
}

