using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>answer: appends the customer's answer to the conversation and counts the round.</summary>
public class LpAnswerMapping : ScriptBase, ITransitionMapping
{
    public Task<dynamic> Handler(ScriptContext context)
    {
        var answer = Read(context.Body, "answer").Replace("<", string.Empty).Replace(">", string.Empty).Trim();
        if (answer.Length > 500) answer = answer.Substring(0, 500);
        var conversation = Read(context.Instance?.Data, "conversation");
        int rounds;
        int.TryParse((string)Read(context.Instance?.Data, "askCount"), out rounds);
        var joined = answer.Length == 0 ? conversation : conversation + "\nEk bilgi: " + answer;
        if (joined.Length > 4000) joined = joined.Substring(joined.Length - 4000);
        return Task.FromResult<dynamic>(new Dictionary<string, object?>
        {
            ["answer"] = string.Empty,
            ["conversation"] = joined,
            ["askCount"] = System.Math.Max(0, rounds) + 1
        });
    }

    private string Read(object? source, string name)
    {
        if (source == null || !HasProperty(source, name)) return string.Empty;
        return GetPropertyValue(source, name)?.ToString() ?? string.Empty;
    }
}
