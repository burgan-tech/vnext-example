using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>Single always-true auto rule (initial -> waiting state) for the cross-domain lab flows.</summary>
public class XdAlwaysTrueRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        return Task.FromResult(true);
    }
}
