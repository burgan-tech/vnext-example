using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Condition of <c>to-high</c> (priority 10): satisfied unless the start body carried
/// <c>skipHigh: true</c>. That one switch lets a test make the higher-priority rule answer false
/// and watch the next priority take over.
/// </summary>
public class HighPriorityRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;

        var skip = false;
        if (data != null && data.TryGetValue("skipHigh", out var raw) && raw != null)
        {
            bool.TryParse(raw.ToString(), out skip);
        }

        var satisfied = !skip;
        LogInformation($"HighPriorityRule: skipHigh={skip} satisfied={satisfied}");
        return Task.FromResult(satisfied);
    }
}
