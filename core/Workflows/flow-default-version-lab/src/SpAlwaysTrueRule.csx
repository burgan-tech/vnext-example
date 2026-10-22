using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class SpAlwaysTrueRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        LogInformation("SpAlwaysTrueRule: returning true");
        return Task.FromResult(true);
    }
}
