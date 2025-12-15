using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// SubProcess Task Mapping - Starts a fire-and-forget subprocess
/// Test case: Verify subprocess launching (non-blocking)
/// </summary>
public class SubProcessMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var subProcessTask = task as SubProcessTask;
            if (subProcessTask == null)
            {
                throw new InvalidOperationException("Task must be a SubProcessTask");
            }

            // Configure subprocess
            subProcessTask.SetDomain("core");
            subProcessTask.SetFlow("task-test-subflow");
            subProcessTask.SetKey(context.Instance?.Key ?? Guid.NewGuid().ToString());
            subProcessTask.SetTags(new[] { "task-test", "subprocess", "success" });

            // Prepare subprocess data
            var subProcessBody = new
            {
                parentInstanceId = context.Instance?.Id,
                parentWorkflowId = context.Workflow.Key,
                testId = context.Instance?.Data?.testId ?? Guid.NewGuid().ToString(),
                launchedAt = DateTime.UtcNow,
                launchedBy = "task-test-workflow",
                isFireAndForget = true,
                message = "SubProcess test - fire-and-forget instance"
            };
            subProcessTask.SetBody(subProcessBody);

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "subprocess-input-error",
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
                    Key = "subprocess-success",
                    Data = new
                    {
                        subProcessResult = new
                        {
                            success = true,
                            subProcessInstanceId = response?.data?.id,
                            launchedAt = DateTime.UtcNow,
                            taskType = "SubProcess",
                            note = "Fire-and-forget: Parent workflow does not wait for subprocess completion"
                        }
                    },
                    Tags = new[] { "task-test", "subprocess", "success", "fire-and-forget" }
                };
            }

            return new ScriptResponse
            {
                Key = "subprocess-failed",
                Data = new
                {
                    subProcessResult = new
                    {
                        success = false,
                        error = response?.errorMessage ?? "Failed to launch subprocess",
                        failedAt = DateTime.UtcNow,
                        taskType = "SubProcess"
                    }
                },
                Tags = new[] { "task-test", "subprocess", "failed" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "subprocess-exception",
                Data = new
                {
                    subProcessResult = new
                    {
                        success = false,
                        error = ex.Message,
                        taskType = "SubProcess"
                    }
                }
            };
        }
    }
}

