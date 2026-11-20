using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// eb-scripts (vnext#1268): a script task whose output handler fails in a helper method. For a script
/// task the output handler is the invocation itself, so the runtime reports a business failure
/// (status 500); the incident's stack trace must still name this file and line.
/// </summary>
public class EbThrowOutputMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        Reject("eb-scripts: the output handler rejected the response");
        return Task.FromResult(new ScriptResponse());
    }

    private static void Reject(string reason) => throw new InvalidOperationException(reason);
}
