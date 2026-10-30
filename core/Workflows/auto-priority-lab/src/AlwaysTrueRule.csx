using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// An automatic-transition condition that is always satisfied. Used by <c>to-low</c>, so that the
/// ONLY thing separating it from <c>to-high</c> is the <c>priority</c> the runtime orders them by.
/// </summary>
public class AlwaysTrueRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        return Task.FromResult(true);
    }
}
