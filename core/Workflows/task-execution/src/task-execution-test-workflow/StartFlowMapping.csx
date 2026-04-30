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
        // StartTask (tip 11) yeni bir hedef workflow instance'i acar.
        // Body uzerinden hedef instance data'sina parent referansi ve kaynak notu yaziyoruz ki
        // testler hedef GetInstance attributes'unda parent zincirini dogrulayabilsin.
        var startTask = (task as StartTask)!;

        var body = new
        {
            source = "task-execution-test",
            parentInstanceId = context.Instance.Id,
            note = "this is a startflow from task-execution-test-workflow"
        };
        startTask.SetBody(body);

        LogInformation("StartFlowMapping InputHandler: body set with parentInstanceId/source/note");
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
        if (HasProperty(data, "timerStartedAt")) result.timerStartedAt = data.timerStartedAt;
        if (HasProperty(data, "timerExpectedSeconds")) result.timerExpectedSeconds = data.timerExpectedSeconds;

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
