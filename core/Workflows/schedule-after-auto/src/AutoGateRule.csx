using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// The gate's automatic-transition condition: satisfied only when the caller asked for
/// <c>mode = "auto"</c>.
/// <para>
/// This rule is the whole point of the scenario. It is evaluated by RunAutomaticTransitionsStep,
/// which — since the epilogue reorder — runs BEFORE ScheduleTransitionsStep. When it answers true
/// the step sets <c>Directives.NextTransition</c> and the Schedule step arms nothing; when it
/// answers false the state's scheduled transition is armed exactly as before.
/// </para>
/// </summary>
public class AutoGateRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;

        var mode = "park";
        if (data != null && data.TryGetValue("mode", out var rawMode) && rawMode != null)
        {
            mode = rawMode.ToString();
        }

        var satisfied = mode == "auto";
        LogInformation($"AutoGateRule: mode={mode} satisfied={satisfied}");
        return Task.FromResult(satisfied);
    }
}
