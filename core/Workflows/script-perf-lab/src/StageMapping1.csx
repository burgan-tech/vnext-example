// nonce: 1
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using Perf.Helpers;

/// <summary>
/// Stage 1: instance data'ya chunkKb boyutunda deterministik chunk merge eder (delta-only).
/// chunkKb start body'den okunur; helper'lar (A7) chunk + stamp uretir.
/// </summary>
public class StageMapping1 : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var inst = context.Instance.Data as IDictionary<string, object>;
        var chunkKb = 4;
        if (inst != null && inst.TryGetValue("chunkKb", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out chunkKb);
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        var stage = (IDictionary<string, object>)new ExpandoObject();
        stage["stamp"] = PerfStampHelper.Stage(1, context.Instance.Id.ToString());
        stage["chunk"] = PerfChunkHelper.Build(1, chunkKb);
        target["stage1"] = stage;
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
