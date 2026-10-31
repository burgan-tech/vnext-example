using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>file-streaming-lab: the download function's single task does nothing; the output script reads the file.</summary>
public class FslNoopMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) => Task.FromResult(new ScriptResponse());
    public Task<ScriptResponse> OutputHandler(ScriptContext context) => Task.FromResult(new ScriptResponse());
}
