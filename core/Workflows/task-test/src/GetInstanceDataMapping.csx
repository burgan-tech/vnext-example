using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// GetInstanceData Task Mapping - Retrieves data from another workflow instance
/// Test case: Verify cross-workflow data retrieval
/// </summary>
public class GetInstanceDataMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var getDataTask = task as GetInstanceDataTask;
            if (getDataTask == null)
            {
                throw new InvalidOperationException("Task must be a GetInstanceDataTask");
            }

            // Configure target workflow
            getDataTask.SetDomain("core");
            getDataTask.SetFlow("task-test-subflow");
            
            // Get instance ID from workflow data
            var targetInstanceId = context.Instance?.Data?.subflowInstanceId?.ToString();
            if (!string.IsNullOrEmpty(targetInstanceId))
            {
                getDataTask.SetInstance(targetInstanceId);
            }

            // Request specific extensions
            getDataTask.SetExtensions(new[] { "all" });

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "get-instance-data-input-error",
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
                    Key = "get-instance-data-success",
                    Data = new
                    {
                        getInstanceDataResult = new
                        {
                            success = true,
                            retrievedData = response?.data,
                            metadata = response?.metadata,
                            retrievedAt = DateTime.UtcNow,
                            taskType = "GetInstanceData"
                        }
                    },
                    Tags = new[] { "task-test", "get-instance-data", "success" }
                };
            }

            return new ScriptResponse
            {
                Key = "get-instance-data-failed",
                Data = new
                {
                    getInstanceDataResult = new
                    {
                        success = false,
                        error = response?.errorMessage ?? "Failed to retrieve instance data",
                        failedAt = DateTime.UtcNow,
                        taskType = "GetInstanceData"
                    }
                },
                Tags = new[] { "task-test", "get-instance-data", "failed" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "get-instance-data-exception",
                Data = new
                {
                    getInstanceDataResult = new
                    {
                        success = false,
                        error = ex.Message,
                        taskType = "GetInstanceData"
                    }
                }
            };
        }
    }
}

