using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// root-waiting onEntry sayaci — updateData artirmamali, $self shared transition ARTIRMALI.
/// <para>
/// DELTA-ONLY: yalnizca sahibi oldugu sayaci dondurur. Full-echo yapsaydi, eszamanli
/// yazicilarin taze degerlerini bayat snapshot degeriyle ezerdi; merge zaten head'i korur.
/// </para>
/// </summary>
public class RootEntryMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var inst = context.Instance.Data as IDictionary<string, object>;

        var current = 0;
        if (inst != null && inst.TryGetValue("rootEntries", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out current);
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        target["rootEntries"] = current + 1;

        LogInformation($"RootEntryMapping: rootEntries {current} -> {current + 1}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
