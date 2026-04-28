using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class AlwaysTrueRule : ScriptBase, IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        LogInformation("AlwaysTrueRule: returning true");
        return true;
    }
}
