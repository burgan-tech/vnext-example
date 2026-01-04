using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// ErrorBoundary SubFlow Mapping - Handles SubFlow data mapping with error propagation
/// </summary>
public class ErrorBoundarySubFlowMapping : ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        // Pass parent data to SubFlow
        return Task.FromResult(new ScriptResponse
        {
            Data = context.Instance.Data,
            Headers = null
        });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse
        {
            Data = context.Body,
            Tags = new[] { "error-boundary-test", "subflow", "completed" }
        });
    }
}

