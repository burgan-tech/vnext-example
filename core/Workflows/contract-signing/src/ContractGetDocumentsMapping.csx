using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

// onEntry of contract-initial: call the documents service (HTTP type 6) with contractCode,
// then seed documentCount/documents and zero the loop cursors + counters.
public class ContractGetDocumentsMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var http = task as HttpTask;
        string code = context.Instance.Data.contractCode;
        if (http != null)
        {
            // Base url is configuration-driven: the task definition ships the API_BASEURL
            // placeholder so the same component runs against any environment. Keep the path
            // from the task config — only the host is substituted here.
            var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001");
            var baseUrl = http.Url.Replace("API_BASEURL", apiBaseUrl);
            var separator = baseUrl.Contains("?") ? "&" : "?";
            http.SetUrl($"{baseUrl}{separator}contractCode={code}");
        }
        return Task.FromResult(new ScriptResponse { Data = context.Instance?.Data });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic merged = new ExpandoObject();
        var target = (IDictionary<string, object>)merged;
        var inst = context.Instance.Data as IDictionary<string, object>;
        if (inst != null) foreach (var kv in inst) target[kv.Key] = kv.Value;

        var count = ListCount(context.Body.data.documents);
        target["documents"] = context.Body.data.documents;
        target["documentCount"] = count;
        target["iterIndex"] = 0;
        target["finIndex"] = 0;
        target["onlineInstanceIds"] = new List<object>();
        // vnext#1267: the chain root the runtime resolves for this SubProcess child. Without the
        // root.instance.id key it falls back to the child's own id, so a missing key never reads as the root.
        target["rootInstanceId"] = context.Instance.GetRootInstanceId().ToString();
        LogInformation($"ContractGetDocumentsMapping: resolved {count} documents");
        return Task.FromResult(new ScriptResponse { Data = merged });
    }
}
