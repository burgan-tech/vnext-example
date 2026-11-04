using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// DirectTrigger (task 12), same domain: moves seed triggerKeyB idle → touched, addressed by KEY so the
/// local key→id lookup runs inside the parallel group too. Each run targets its own seed: one instance
/// cannot take two transitions at once.
/// </summary>
public class PvkTfTriggerBMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var trigger = task as DirectTriggerTask ?? throw new InvalidOperationException("Task must be a DirectTriggerTask");
        trigger.SetKey(Read(context, "triggerKeyB"));
        trigger.SetBody(new { touchedBy = "pvk-trigger-family" });
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context) =>
        Task.FromResult(new ScriptResponse { Data = new ExpandoObject() });

    private static string Read(ScriptContext context, string name)
    {
        if (context.Instance?.Data is IDictionary<string, object> data)
            foreach (var kv in data)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value?.ToString() ?? string.Empty;
        throw new InvalidOperationException($"pvk-trigger-family: instance data has no '{name}'");
    }
}
