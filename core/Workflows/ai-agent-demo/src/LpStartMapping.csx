using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>submit: keeps the request text, starts the conversation and the question counter.</summary>
public class LpStartMapping : ScriptBase, ITransitionMapping
{
    public Task<dynamic> Handler(ScriptContext context)
    {
        var message = Read(context.Body, "message").Replace("<", string.Empty).Replace(">", string.Empty);
        if (message.Length > 2000) message = message.Substring(0, 2000);
        return Task.FromResult<dynamic>(new Dictionary<string, object?>
        {
            ["message"] = message,
            ["conversation"] = message,
            ["askCount"] = 0
        });
    }

    private string Read(object? source, string name)
    {
        if (source == null || !HasProperty(source, name)) return string.Empty;
        return GetPropertyValue(source, name)?.ToString() ?? string.Empty;
    }
}
