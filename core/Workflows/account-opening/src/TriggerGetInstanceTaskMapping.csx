using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using System.Text.Json;

public class TriggerGetInstanceTaskMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var triggerTask = (task as GetInstanceDataTask)!;
       
        triggerTask.SetInstance(context.Instance.Data.oldInstanceId);
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {

        return Task.FromResult(new ScriptResponse()
        {
            Data = new
            {
                oldInstance=context.Body,
                success = true
            }
        });
    }
}

public class RequestModel
{
    public string key { get; set; }
    public dynamic  attributes { get; set; }
}