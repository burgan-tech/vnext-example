using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Fan-in gate for the parent-collect state: fires when updateCount reaches
/// updateThreshold (default 5). Evaluated by the automatic-transition step after
/// EVERY updateData — validating that updateData always re-runs auto evaluation
/// with the freshly written data.
/// </summary>
public class CollectThresholdRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;

        var count = 0;
        if (data != null && data.TryGetValue("updateCount", out var rawCount) && rawCount != null)
        {
            int.TryParse(rawCount.ToString(), out count);
        }

        var threshold = 5;
        if (data != null && data.TryGetValue("updateThreshold", out var rawThreshold) && rawThreshold != null)
        {
            int.TryParse(rawThreshold.ToString(), out threshold);
        }

        var satisfied = count >= threshold;
        LogInformation($"CollectThresholdRule: updateCount={count} threshold={threshold} satisfied={satisfied}");
        return Task.FromResult(satisfied);
    }
}
