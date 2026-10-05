using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Rest in the CORPORATE human state when the payload asks for it (<c>mode = "corporate"</c>) and the
/// hop budget is spent. Mutually exclusive with <c>DescendRule</c> (hops &gt; 0), so the level that
/// would otherwise rest in <c>{level}-human</c> rests in <c>{level}-corporate-human</c> instead.
/// </summary>
public class CorporateRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;

        var hops = 0;
        if (data != null && data.TryGetValue("hops", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out hops);
        }

        var corporate = data != null
                        && data.TryGetValue("mode", out var mode)
                        && mode != null
                        && mode.ToString() == "corporate";

        var rest = corporate && hops <= 0;
        LogInformation($"CorporateRule: hops={hops} corporate={corporate} rest={rest}");
        return Task.FromResult(rest);
    }
}
