using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// rule-schema-lab: true when the caller sends <c>x-segment: corporate</c>. Selects the corporate payload
/// schema in the rule-based <c>schemas</c> list (vnext rule-based transition schemas).
/// </summary>
public class IsCorporateSegment : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        // context.Headers is dynamic: bind it to a static type before TryGetValue (CS8197).
        object headers = context.Headers;
        string value = null;
        if (headers is IDictionary<string, string> typed && typed.TryGetValue("x-segment", out var text))
            value = text;
        else if (headers is IDictionary<string, object> loose && loose.TryGetValue("x-segment", out var raw) && raw != null)
            value = raw.ToString();

        return Task.FromResult(string.Equals(value, "corporate", System.StringComparison.OrdinalIgnoreCase));
    }
}
