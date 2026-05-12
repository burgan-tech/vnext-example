using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

public class DaprPubSubMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var pubsubTask = (task as DaprPubSubTask)!;
        if (pubsubTask == null)
            throw new InvalidOperationException("Task must be a DaprPubSubTask");

        // Use DaprPubSubName from Vault when set; otherwise keep task JSON default (test-pubsub).
        // Keeps Vault optional in dev; other envs (e.g. vnext-pubsub) only need the key in Vault.
        string vaultValue = GetConfigValue("DaprPubSubName");
        if (!string.IsNullOrWhiteSpace(vaultValue))
        {
            pubsubTask.SetPubSubName(vaultValue);
            LogInformation($"DaprPubSubMapping: Vault override pubSubName = {vaultValue}");
        }
        else
        {
            LogInformation(
                $"DaprPubSubMapping: DaprPubSubName not in Vault, using task JSON default"
            );
        }

        // Publish requires non-null `data` (ArgumentNullException). Even if task JSON has data,
        // ordering/legacy publish is unreliable; enforce via mapping.
        var instanceData = context.Instance.Data;
        dynamic messageData = new ExpandoObject();
        messageData.eventType = "IntegrationTest";
        messageData.source = "extended-tasks-test-workflow";
        messageData.timestamp = DateTime.UtcNow.ToString("o");
        if (HasProperty(instanceData, "testId"))
            messageData.testId = instanceData.testId;
        pubsubTask.SetData(messageData);
        LogInformation("DaprPubSubMapping: SetData completed");

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();

        if (HasProperty(data, "testId"))
            result.testId = data.testId;
        if (HasProperty(data, "initCompleted"))
            result.initCompleted = data.initCompleted;

        result.taskResults = new ExpandoObject();
        if (HasProperty(data, "taskResults"))
        {
            if (HasProperty(data.taskResults, "daprHttp"))
                result.taskResults.daprHttp = data.taskResults.daprHttp;
            if (HasProperty(data.taskResults, "daprService"))
                result.taskResults.daprService = data.taskResults.daprService;
            if (HasProperty(data.taskResults, "daprBinding"))
                result.taskResults.daprBinding = data.taskResults.daprBinding;
            if (HasProperty(data.taskResults, "notification"))
                result.taskResults.notification = data.taskResults.notification;
            if (HasProperty(data.taskResults, "triggerTransition"))
                result.taskResults.triggerTransition = data.taskResults.triggerTransition;
            if (HasProperty(data.taskResults, "getInstances"))
                result.taskResults.getInstances = data.taskResults.getInstances;
            if (HasProperty(data.taskResults, "subprocess"))
                result.taskResults.subprocess = data.taskResults.subprocess;
        }

        result.taskResults.daprPubSub = new ExpandoObject();
        result.taskResults.daprPubSub.completed = true;
        result.taskResults.daprPubSub.executedAt = DateTime.UtcNow.ToString("o");

        // Skill vnext-workflow-creation §6.4: "completed = true" only proves the mapping ran.
        // Align with dapr-pubsub.md §5: read published / messageId or error from context.Body;
        // integration tests assert published==true.
        // statusCode is not in the doc; kept for observability.
        var taskResponse = context.Body;
        if (taskResponse == null)
        {
            result.taskResults.daprPubSub.published = false;
            LogInformation("DaprPubSubMapping: context.Body null, published=false");
        }
        else if (HasProperty(taskResponse, "isSuccess") && taskResponse.isSuccess)
        {
            result.taskResults.daprPubSub.published = true;
            if (HasProperty(taskResponse, "data") && taskResponse.data != null)
            {
                var responseData = taskResponse.data;
                if (HasProperty(responseData, "messageId"))
                    result.taskResults.daprPubSub.messageId = responseData.messageId;
            }
            if (HasProperty(taskResponse, "statusCode"))
                result.taskResults.daprPubSub.statusCode = taskResponse.statusCode;
        }
        else
        {
            result.taskResults.daprPubSub.published = false;
            if (HasProperty(taskResponse, "errorMessage"))
                result.taskResults.daprPubSub.error = taskResponse.errorMessage;
            if (HasProperty(taskResponse, "statusCode"))
                result.taskResults.daprPubSub.statusCode = taskResponse.statusCode;
        }

        LogInformation("DaprPubSubMapping completed");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
