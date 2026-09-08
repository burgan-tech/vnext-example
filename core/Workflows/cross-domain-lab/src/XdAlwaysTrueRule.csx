using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Single always-true auto rule. On the SubFlow state it is evaluated on the RESUME path only
/// (after the partner child completes), exactly like subflow-orchestration-parent's AlwaysTrueRule.
/// </summary>
public class XdAlwaysTrueRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        return Task.FromResult(true);
    }
}
