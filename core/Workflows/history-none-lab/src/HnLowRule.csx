using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>history-none-lab: amount up to 100 routes to hn-done-low. Reads the buffered data (nothing is persisted yet).</summary>
public class HnLowRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        var amount = data != null && data.TryGetValue("amount", out var value) && value != null
            ? Convert.ToDecimal(value.ToString(), System.Globalization.CultureInfo.InvariantCulture)
            : 0m;
        return Task.FromResult(amount <= 100m);
    }
}
