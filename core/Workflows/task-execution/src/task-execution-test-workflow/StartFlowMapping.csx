using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

public class StartFlowMapping : ScriptBase, IMapping
{
    public async Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var startTask = (task as StartTask)!;
        var data = context.Instance.Data;

        var body = new
        {
            source = "task-execution-test",
            parentInstanceId = context.Instance.Id
        };
        startTask.SetBody(body);

        LogInformation("StartFlowMapping InputHandler: body set for target workflow");
        return new ScriptResponse();
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        var taskResponse = context.Body;
        var responseBody = taskResponse.data;

        dynamic result = new ExpandoObject();
        if (HasProperty(data, "testId")) result.testId = data.testId;
        if (HasProperty(data, "httpTaskCompleted")) result.httpTaskCompleted = data.httpTaskCompleted;
        if (HasProperty(data, "processId")) result.processId = data.processId;
        if (HasProperty(data, "scriptProcessed")) result.scriptProcessed = data.scriptProcessed;
        if (HasProperty(data, "crossWorkflowCompleted")) result.crossWorkflowCompleted = data.crossWorkflowCompleted;

        result.startFlowCompleted = true;
        result.startFlowIsSuccess = taskResponse.isSuccess;

        if (responseBody != null)
        {
            if (HasProperty(responseBody, "id"))
                result.startedInstanceId = responseBody.id;
            else if (HasProperty(responseBody, "instanceId"))
                result.startedInstanceId = responseBody.instanceId;
        }

        LogInformation($"StartFlowMapping OutputHandler: isSuccess={taskResponse.isSuccess}");
        return new ScriptResponse { Data = result };
    }
}
