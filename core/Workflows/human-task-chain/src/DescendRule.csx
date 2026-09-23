using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Descend while `hops` is positive. Every subflow mapping decrements it, so the payload's initial
/// value alone decides which level ends up holding the human task — the definition chain is the
/// same for all three scenarios.
/// </summary>
public class DescendRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;

        var hops = 0;
        if (data != null && data.TryGetValue("hops", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out hops);
        }

        var descend = hops > 0;
        LogInformation($"DescendRule: hops={hops} descend={descend}");
        return Task.FromResult(descend);
    }
}
