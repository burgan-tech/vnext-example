using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Decision script of ai-agent-lab-script (decision.mode = script). Returns the instance's
/// <c>pick</c> field verbatim, so a test chooses the outcome: a candidate key (is-fraud, is-question)
/// is taken as the decision; anything else is not a candidate and must send the state to its fallback.
/// </summary>
public class ScriptDecision : ScriptBase, IAgentDecisionMapping
{
    public Task<string?> Handler(AgentRunOutcome outcome, IReadOnlyList<AgentCandidate> candidates, ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        string? pick = null;
        if (data != null && data.TryGetValue("pick", out var raw) && raw != null)
        {
            pick = raw.ToString();
        }

        LogInformation($"ScriptDecision: pick={pick} candidates={candidates.Count} modelDecision={outcome.ModelDecision}");
        return Task.FromResult(pick);
    }
}
