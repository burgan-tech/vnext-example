using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

// onExecution of the invoke-loop $self transition (invoke-next): start one online-flow
// SubProcess for documents[iterIndex],
// passing this Contract instance id + render-ready / approval-received callback refs.
// OutputHandler appends the started online instance id and advances iterIndex.
public class ContractStartOnlineMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var sub = task as SubProcessTask;
        if (sub == null) throw new InvalidOperationException("Task must be a SubProcessTask");

        var data = context.Instance.Data as IDictionary<string, object>;
        int iter = ToInt(data, "iterIndex");
        string code = Str(data, "contractCode");
        string documentId = null;
        if (data != null && data.TryGetValue("documents", out var docsObj) && docsObj is System.Collections.IList docList && iter < docList.Count)
        {
            var doc = docList[iter] as IDictionary<string, object>;
            if (doc != null && doc.TryGetValue("documentId", out var did) && did != null) documentId = did.ToString();
        }

        sub.SetDomain("core");
        sub.SetFlow("online-flow");
        sub.SetVersion("1.1.0");
        // Deterministic idempotency key: a duplicate start attempt for the same document
        // (retry, race) coalesces onto the same child instead of spawning a second one.
        sub.SetKey(documentId != null
            ? $"{context.Instance.Id}-doc-{documentId}"
            : Guid.NewGuid().ToString());
        sub.SetBody(new
        {
            documentId = documentId,
            contractInstanceId = context.Instance.Id,
            contractCode = code,
            renderReady = new { domain = "core", flow = "contract-flow", key = "contract-progress" },
            approvalReceived = new { domain = "core", flow = "contract-flow", key = "contract-progress" }
        });
        sub.SetSync(false);
        return Task.FromResult(new ScriptResponse { Data = context.Instance?.Data });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic merged = new ExpandoObject();
        var target = (IDictionary<string, object>)merged;
        var inst = context.Instance.Data as IDictionary<string, object>;
        if (inst != null) foreach (var kv in inst) target[kv.Key] = kv.Value;

        var ids = new List<object>();
        if (inst != null && inst.TryGetValue("onlineInstanceIds", out var idsObj) && idsObj is System.Collections.IEnumerable en)
            foreach (var x in en) ids.Add(x);

        // SubProcess start response nests the created instance under data.value.id
        // (same shape LoginStartContractMapping reads). data.id is the legacy/fallback shape —
        // reading only data.id silently yielded null and left onlineInstanceIds empty, which in
        // turn made finalize-loop trigger online-finalize against a null instance id.
        object startedId = null;
        try { startedId = context.Body?.data?.value?.id; } catch { startedId = null; }
        if (startedId == null) { try { startedId = context.Body?.data?.id; } catch { startedId = null; } }
        if (startedId == null) LogInformation("ContractStartOnlineMapping: WARN could not read started online instance id from response");
        else ids.Add(startedId);

        target["onlineInstanceIds"] = ids;
        target["iterIndex"] = ToInt(inst, "iterIndex") + 1;
        LogInformation($"ContractStartOnlineMapping: online subprocess started ({ids.Count} total)");
        return Task.FromResult(new ScriptResponse { Data = merged });
    }

    private static int ToInt(IDictionary<string, object> d, string key)
    {
        if (d != null && d.TryGetValue(key, out var v) && v != null && int.TryParse(v.ToString(), out var n)) return n;
        return 0;
    }
    private static string Str(IDictionary<string, object> d, string key)
    {
        if (d != null && d.TryGetValue(key, out var v) && v != null) return v.ToString();
        return null;
    }
}
