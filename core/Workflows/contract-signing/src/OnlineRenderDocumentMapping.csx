using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;

// onEntry of online-initial: "render" the document identified by documentId.
// (Placeholder business step — mark rendered=true; replace with the real render task as needed.)
public class OnlineRenderDocumentMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic merged = new ExpandoObject();
        var target = (IDictionary<string, object>)merged;
        var inst = context.Instance.Data as IDictionary<string, object>;
        if (inst != null) foreach (var kv in inst) target[kv.Key] = kv.Value;
        target["rendered"] = true;
        // vnext#1267: the chain root the runtime resolves for this SubProcess child. Without the
        // root.instance.id key it falls back to the child's own id, so a missing key never reads as the root.
        target["rootInstanceId"] = context.Instance.GetRootInstanceId().ToString();
        LogInformation("OnlineRenderDocumentMapping: document rendered");
        return Task.FromResult(new ScriptResponse { Data = merged });
    }
}
