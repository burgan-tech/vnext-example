using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>A rule that is never satisfied: the rules-mode candidate that must not win.</summary>
public class NeverRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context) => Task.FromResult(false);
}
