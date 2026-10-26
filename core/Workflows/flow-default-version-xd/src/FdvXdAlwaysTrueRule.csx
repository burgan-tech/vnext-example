using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>Always-true auto rule: start → contract, and contract → done once the partner child finishes.</summary>
public class FdvXdAlwaysTrueRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        return Task.FromResult(true);
    }
}
