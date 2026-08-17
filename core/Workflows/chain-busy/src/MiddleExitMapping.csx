using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// middle-waiting onExit sayaci.
/// <para>
/// DELTA-ONLY: yalnizca sahibi oldugu sayaci dondurur. Full-echo yapsaydi, eszamanli
/// yazicilarin taze degerlerini bayat snapshot degeriyle ezerdi; merge zaten head'i korur.
/// </para>
/// </summary>
public class MiddleExitMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var inst = context.Instance.Data as IDictionary<string, object>;

        var current = 0;
        if (inst != null && inst.TryGetValue("middleExits", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out current);
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        target["middleExits"] = current + 1;

        LogInformation($"MiddleExitMapping: middleExits {current} -> {current + 1}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
