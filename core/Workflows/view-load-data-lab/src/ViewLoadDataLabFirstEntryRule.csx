using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Rule of vld-multi's first view entry. It always matches, so the view function serves the first
/// entry; the state function never evaluates it and takes the first entry's loadData regardless.
/// </summary>
public class ViewLoadDataLabFirstEntryRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context) => Task.FromResult(true);
}
