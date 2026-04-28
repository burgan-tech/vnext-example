using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class HumanTaskMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();

        if (HasProperty(data, "testId")) result.testId = data.testId;
        if (HasProperty(data, "initCompleted")) result.initCompleted = data.initCompleted;

        result.taskResults = new ExpandoObject();
        if (HasProperty(data, "taskResults"))
        {
            if (HasProperty(data.taskResults, "daprHttp")) result.taskResults.daprHttp = data.taskResults.daprHttp;
            if (HasProperty(data.taskResults, "daprService")) result.taskResults.daprService = data.taskResults.daprService;
            if (HasProperty(data.taskResults, "daprBinding")) result.taskResults.daprBinding = data.taskResults.daprBinding;
            if (HasProperty(data.taskResults, "daprPubSub")) result.taskResults.daprPubSub = data.taskResults.daprPubSub;
            if (HasProperty(data.taskResults, "notification")) result.taskResults.notification = data.taskResults.notification;
            if (HasProperty(data.taskResults, "triggerTransition")) result.taskResults.triggerTransition = data.taskResults.triggerTransition;
            if (HasProperty(data.taskResults, "getInstances")) result.taskResults.getInstances = data.taskResults.getInstances;
            if (HasProperty(data.taskResults, "subprocess")) result.taskResults.subprocess = data.taskResults.subprocess;
        }

        result.taskResults.humanTask = new ExpandoObject();
        result.taskResults.humanTask.completed = true;
        result.taskResults.humanTask.approvedAt = DateTime.UtcNow.ToString("o");

        LogInformation("HumanTaskMapping completed");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
