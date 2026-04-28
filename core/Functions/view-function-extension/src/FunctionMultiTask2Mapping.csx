using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

public class FunctionMultiTask2Mapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var httpTask = task as HttpTask;
        if (httpTask != null)
        {
            string baseUrl = GetConfigValue("MocklabBaseUrl");
            httpTask.Url = httpTask.Url.Replace("{MocklabBaseUrl}", baseUrl);
        }
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var taskResponse = context.Body;
        dynamic result = new ExpandoObject();
        result.step = 2;
        result.vfeHttp = true;
        if (taskResponse != null && HasProperty(taskResponse, "statusCode"))
            result.statusCode = taskResponse.statusCode;
        if (taskResponse != null && HasProperty(taskResponse, "data"))
            result.userInfo = taskResponse.data;
        result.at = DateTime.UtcNow.ToString("o");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
