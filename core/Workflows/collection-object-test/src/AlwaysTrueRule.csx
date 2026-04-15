using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class AlwaysTrueRule : IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        return true;
    }
}
