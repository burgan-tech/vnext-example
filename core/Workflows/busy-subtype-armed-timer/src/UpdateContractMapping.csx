using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// onExecute of the updateData transition: copies <c>contractReady</c> from the request body into
/// instance data — the contract callback of the original QA flow. DELTA-ONLY.
/// </summary>
public class UpdateContractMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;

        var ready = false;
        if (body != null && body.TryGetValue("contractReady", out var raw) && raw != null)
        {
            bool.TryParse(raw.ToString(), out ready);
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        target["contractReady"] = ready;

        LogInformation($"UpdateContractMapping: contractReady={ready}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
