using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>history-none-lab: never satisfied — the hn-stuck gate cannot leave its state.</summary>
public class HnNeverRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context) => Task.FromResult(false);
}
