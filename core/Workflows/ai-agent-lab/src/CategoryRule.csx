using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Rule of ai-agent-lab-rules (decision.mode = rules): true when the agent's structured output,
/// written to <c>triage</c> before the automatic transitions run, has <c>category == "question"</c>.
/// The local provider answers the first enum value of the output schema, which is "question".
/// </summary>
public class CategoryRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        string? category = null;
        if (data != null && data.TryGetValue("triage", out var raw) && raw is IDictionary<string, object> triage
            && triage.TryGetValue("category", out var value) && value != null)
        {
            category = value.ToString();
        }

        LogInformation($"CategoryRule: category={category}");
        return Task.FromResult(category == "question");
    }
}
