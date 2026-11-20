using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// eb-scripts (vnext#1268): a transition mapping that fails in a helper method. The instance faults in
/// the pipeline, not in a task, so the incident is the pipeline's own fallback row.
/// </summary>
public class EbThrowTransitionMapping : ScriptBase, ITransitionMapping
{
    public Task<dynamic> Handler(ScriptContext context)
    {
        return Task.FromResult<dynamic>(Shape(context));
    }

    private static object Shape(ScriptContext context) =>
        throw new ArgumentException("eb-scripts: the transition mapping found no customer");
}
