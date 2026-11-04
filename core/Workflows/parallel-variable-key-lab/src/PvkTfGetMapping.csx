using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// GetInstance (task 19), same domain: reads this run's readKey seed by KEY. Both runs at one order read the
/// same instance — two same-schema reads in flight at once, beside the group's other trigger tasks.
/// </summary>
public class PvkTfGetMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var read = task as GetInstanceTask ?? throw new InvalidOperationException("Task must be a GetInstanceTask");
        read.SetKey(Read(context, "readKey"));
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
