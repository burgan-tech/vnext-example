using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Start Workflow Task Mapping - Starts a new workflow instance
/// Test case: Verify starting new workflow instances
/// </summary>
public class StartWorkflowMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var startTask = task as StartTask;
            if (startTask == null)
            {
                throw new InvalidOperationException("Task must be a StartTask");
            }

            // Configure target workflow
            startTask.SetDomain("core");
            startTask.SetFlow("task-test-subflow");
            startTask.SetSync(true);
            startTask.SetVersion("1.0.0");
            startTask.SetKey(Guid.NewGuid().ToString());
            startTask.SetTags(new[] { "task-test", "start-workflow", "success" });
            
            // Prepare initialization body
            var initBody = new
            {
                parentInstanceId = context.Instance?.Id,
                parentWorkflowId = context.Workflow?.Key,
                testId = context.Instance?.Data?.testId ?? Guid.NewGuid().ToString(),
                startedAt = DateTime.UtcNow,
                startedBy = "task-test-workflow",
                message = "StartTask test - new instance created"
            };
            startTask.SetBody(initBody);

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "start-workflow-input-error",
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
                    Key = "start-workflow-success",
                    Data = new
                    {
                        startWorkflowResult = new
                        {
                            success = true,
                            newInstanceId = response?.data?.id,
                            status = response?.data?.status,
                            startedAt = DateTime.UtcNow,
                            taskType = "Start"
                        },
                        // Store instance ID for later use
                        subflowInstanceId = response?.data?.id
                    },
                    Tags = new[] { "task-test", "start-workflow", "success" }
                };
            }

            return new ScriptResponse
            {
                Key = "start-workflow-failed",
                Data = new
                {
                    startWorkflowResult = new
                    {
                        success = false,
                        error = response?.errorMessage ?? "Failed to start workflow",
                        failedAt = DateTime.UtcNow,
                        taskType = "Start"
                    }
                },
                Tags = new[] { "task-test", "start-workflow", "failed" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "start-workflow-exception",
                Data = new
                {
                    startWorkflowResult = new
                    {
                        success = false,
                        error = ex.Message,
                        taskType = "Start"
                    }
                }
            };
        }
    }
}