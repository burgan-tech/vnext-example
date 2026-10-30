using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// subflow-carried-headers-lab: the child's <c>explode</c> transition task. Always throws, so the
/// child faults on the hop of the request that called <c>explode</c> — the request-hop fault path
/// that captures that request's headers onto <c>instance.sub.faulted</c>.
/// </summary>
public class SfchExplodeMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public Task<ScriptResponse> OutputHandler(ScriptContext context) =>
        throw new InvalidOperationException("subflow-carried-headers-lab: deliberate child fault");
}
