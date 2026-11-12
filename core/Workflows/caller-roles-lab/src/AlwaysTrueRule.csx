using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>caller-roles-lab: unconditional automatic rule (the parent's hop after the child completes).</summary>
public class AlwaysTrueRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context) => Task.FromResult(true);
}
