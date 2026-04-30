using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

public class GetInstanceDataMapping : ScriptBase, IMapping
{
    public async Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var getDataTask = (task as GetInstanceDataTask)!;
        var data = context.Instance.Data;

        if (HasProperty(data, "startedInstanceId"))
        {
            var targetId = data.startedInstanceId?.ToString();
            getDataTask.SetInstance(targetId);
            LogInformation($"GetInstanceDataMapping InputHandler: target id={targetId}");
        }

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
        if (HasProperty(data, "startFlowCompleted")) result.startFlowCompleted = data.startFlowCompleted;
        if (HasProperty(data, "startedInstanceId")) result.startedInstanceId = data.startedInstanceId;

        result.getInstanceDataCompleted = true;
        result.getInstanceDataIsSuccess = taskResponse.isSuccess;

        if (responseBody != null)
            result.remoteInstanceData = responseBody;

        LogInformation($"GetInstanceDataMapping OutputHandler: isSuccess={taskResponse.isSuccess}");
        return new ScriptResponse { Data = result };
    }
}
