using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Gate for lab-collect: fires when labUpdateCount reaches labThreshold. Evaluated by the
/// automatic-transition step after every updateData — validates that updateData always
/// re-runs auto evaluation with the freshly persisted data.
/// </summary>
public class LabThresholdRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;

        var count = 0;
        if (data != null && data.TryGetValue("labUpdateCount", out var rawCount) && rawCount != null)
        {
            int.TryParse(rawCount.ToString(), out count);
        }

        var threshold = 4;
        if (data != null && data.TryGetValue("labThreshold", out var rawThreshold) && rawThreshold != null)
        {
            int.TryParse(rawThreshold.ToString(), out threshold);
        }

        var satisfied = count >= threshold;
        LogInformation($"LabThresholdRule: labUpdateCount={count} threshold={threshold} satisfied={satisfied}");
        return Task.FromResult(satisfied);
    }
}
