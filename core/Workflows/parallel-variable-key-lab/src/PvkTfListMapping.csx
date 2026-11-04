using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// GetInstances (task 15), same domain: lists this run's pvk-tf-target seeds by testId. Four of these
/// run at one order with distinct variableKeys — the shape that failed with "A second operation was
/// started on this context instance" (vnext#1161). Nothing is written: the recorder reads the slots.
/// </summary>
public class PvkTfListMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var list = task as GetInstancesTask ?? throw new InvalidOperationException("Task must be a GetInstancesTask");
        var testId = Read(context, "testId");
        list.SetPage(1);
        list.SetPageSize(50);
        list.SetFilterSpec(InstanceQuery.Create().Where("attributes.testId", f => f.Eq(testId)).Build());
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
