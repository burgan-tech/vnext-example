using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
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
            http.SetUrl($"http://localhost:3001/api/contract-signing/contracts/documents?contractCode={code}");
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
        LogInformation($"ContractGetDocumentsMapping: resolved {count} documents");
        return Task.FromResult(new ScriptResponse { Data = merged });
    }
}
