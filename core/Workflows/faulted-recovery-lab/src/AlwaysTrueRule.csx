using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>faulted-recovery-lab: the rule of every automatic transition in the lab.</summary>
public class AlwaysTrueRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context) => Task.FromResult(true);
}
