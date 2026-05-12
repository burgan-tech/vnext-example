using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class AlwaysTrueRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        LogInformation("AlwaysTrueRule: returning true");
        return Task.FromResult(true);
    }
}
