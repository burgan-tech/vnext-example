// nonce: 1
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using Perf.Helpers;

/// <summary>
/// Stage 4: instance data'ya chunkKb boyutunda deterministik chunk merge eder (delta-only).
/// chunkKb start body'den okunur; helper'lar (A7) chunk + stamp uretir. chunk: kb adet ~1KB
/// node'dan olusan bir liste -- tek buyuk string DEGIL, B9'un per-node maliyetini
/// (NormalizedJson / per-object SerializeToElement) tetiklemek icin dugum-zengin.
/// </summary>
public class StageMapping4 : ScriptBase, IMapping
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
            if (int.TryParse(raw.ToString(), out var parsed) && parsed > 0)
            {
                chunkKb = parsed;
            }
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        var stage = (IDictionary<string, object>)new ExpandoObject();
        stage["stamp"] = PerfStampHelper.Stage(4, context.Instance.Id.ToString());
        stage["chunk"] = PerfChunkHelper.Build(4, chunkKb);
        target["stage4"] = stage;
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
