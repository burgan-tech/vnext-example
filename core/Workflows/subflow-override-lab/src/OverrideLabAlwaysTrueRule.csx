using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// Each flow walks itself to its waiting state so a test only starts the top.
public class OverrideLabAlwaysTrueRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context) => Task.FromResult(true);
}
