using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// Each flow walks itself to its waiting state so a test only starts the parent.
public class SdsAlwaysTrueRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context) => Task.FromResult(true);
}
