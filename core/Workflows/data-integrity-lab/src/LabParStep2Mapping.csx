using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Parallel branch 2: runs concurrently with the other three branches (same order),
/// each in its own DI scope / DbContext. The per-instance FOR UPDATE row lock must
/// serialize the four writes: VersionNo stays sequential and no branch's key is lost.
/// </summary>
public class LabParStep2Mapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var httpTask = task as HttpTask;
        if (httpTask != null)
        {
            // Base url is configuration-driven: task definitions ship the API_BASEURL
            // placeholder so the same component runs against any environment.
            var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001");
            httpTask.SetUrl(httpTask.Url.Replace("API_BASEURL", apiBaseUrl));
        }

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        // Delta-only by design: the write service merges this into the DB head under the
        // row lock, so echoing existing data is not only unnecessary — under concurrent
        // writers a stale echoed value would overwrite a fresher one.
        target["par2"] = true;
        target["par2At"] = DateTime.UtcNow.ToString("o");
        LogInformation("LabParStep2Mapping: par2 stamped");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
