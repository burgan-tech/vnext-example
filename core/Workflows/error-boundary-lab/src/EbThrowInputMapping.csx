using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// eb-scripts (vnext#1268): a task input handler that fails in a helper method, with the real cause
/// kept as the INNER exception. The incident's stack trace must carry both exception types, the
/// inner one first, and this file's line numbers.
/// </summary>
public class EbThrowInputMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            ReadAmount("not-a-number");
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("eb-scripts: the input handler could not read the amount", ex);
        }

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context) => Task.FromResult(new ScriptResponse());

    private static int ReadAmount(string raw) => int.Parse(raw);
}
