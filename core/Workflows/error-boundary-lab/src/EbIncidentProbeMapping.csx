using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Records what <c>context.Incident</c> shows to a mapping running inside the root's error-boundary
/// handling of a subflow fault (eb-sf-root: the <c>r-has-error</c> transition and the
/// <c>r-error-end</c> OnEntry). The slot prefix comes from the task key so one mapping serves both
/// slots. This is exactly what a domain developer reads to branch on the subflow's failure.
/// </summary>
public class EbIncidentProbeMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var slot = context.Instance?.CurrentState == "r-error-end" ? "probeEntry" : "probeTransition";
        var info = context.Incident;
        var active = info?.ActiveIncident;

        dynamic probe = new ExpandoObject();
        probe.contextHasIncidentInfo = info != null;
        probe.hasActiveIncident = info?.HasActiveIncident ?? false;
        probe.incidentsLoaded = info?.IncidentsLoaded ?? false;
        probe.totalIncidentCount = info?.TotalIncidentCount ?? -1;
        probe.activeIncidentPresent = active != null;
        probe.activeErrorCode = active?.ErrorCode;
        probe.activeStatusCode = active?.StatusCode;
        probe.activeTask = active?.Task;
        probe.activeBoundaryAction = active?.BoundaryAction;

        dynamic result = new ExpandoObject();
        ((IDictionary<string, object>)result)[slot] = probe;
        LogInformation($"EbIncidentProbeMapping[{slot}]: has={probe.hasActiveIncident} active={probe.activeIncidentPresent} loaded={probe.incidentsLoaded} count={probe.totalIncidentCount}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
