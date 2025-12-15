using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// DaprPubSub Task Mapping - Publishes events to message bus
/// Test case: Verify event publishing to Dapr PubSub component
/// </summary>
public class DaprPubSubMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var pubsubTask = task as DaprPubSubTask;
            if (pubsubTask == null)
            {
                throw new InvalidOperationException("Task must be a DaprPubSubTask");
            }

            // Configure PubSub component
            pubsubTask.SetPubSubName("vnext-execution-pubsub");
            pubsubTask.SetTopic("vnext.test.events");

            // Create Cloud Events format data
            var eventData = new
            {
                specversion = "1.0",
                type = "com.vnext.task-test.TestEvent",
                source = "task-test-workflow",
                id = Guid.NewGuid().ToString(),
                time = DateTime.UtcNow.ToString("O"),
                datacontenttype = "application/json",
                data = new
                {
                    testId = context.Instance?.Data?.testId ?? Guid.NewGuid().ToString(),
                    workflowId = context.Workflow.Key,
                    instanceId = context.Instance?.Id,
                    timestamp = DateTime.UtcNow,
                    message = "DaprPubSub test event published"
                }
            };

            pubsubTask.SetData(eventData);

            // Set metadata
            var metadata = new Dictionary<string, string?>
            {
                ["priority"] = "normal",
                ["correlationId"] = context.Instance.Id.ToString(),
                ["source"] = "task-test-workflow"
            };
            pubsubTask.SetMetadata(metadata);

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "dapr-pubsub-input-error",
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
                    Key = "dapr-pubsub-success",
                    Data = new
                    {
                        daprPubSubResult = new
                        {
                            success = true,
                            publishedAt = DateTime.UtcNow,
                            taskType = "DaprPubSub"
                        }
                    },
                    Tags = new[] { "task-test", "dapr-pubsub", "success" }
                };
            }

            return new ScriptResponse
            {
                Key = "dapr-pubsub-failed",
                Data = new
                {
                    daprPubSubResult = new
                    {
                        success = false,
                        error = response?.errorMessage ?? "Unknown error",
                        failedAt = DateTime.UtcNow,
                        taskType = "DaprPubSub"
                    }
                },
                Tags = new[] { "task-test", "dapr-pubsub", "failed" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "dapr-pubsub-exception",
                Data = new
                {
                    daprPubSubResult = new
                    {
                        success = false,
                        error = ex.Message,
                        taskType = "DaprPubSub"
                    }
                }
            };
        }
    }
}

