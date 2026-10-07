using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>history-none-lab: always throws; the entry's error boundary aborts, so the instance faults.</summary>
public class HnThrowMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public Task<ScriptResponse> OutputHandler(ScriptContext context) =>
        throw new InvalidOperationException("history-none-lab: deliberate task failure");
}
