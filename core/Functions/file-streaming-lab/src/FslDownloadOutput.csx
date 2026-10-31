using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// file-streaming-lab: a rawResponse function that serves the instance's inlineDoc as its own bytes
/// (vnext-client-sdk-core#99 / #142-E): the output returns the ScriptFile itself, so the response is the PDF with
/// the file's media type, not a quoted base64 string.
/// </summary>
public class FslDownloadOutput : ScriptBase, IOutputHandler
{
    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        var doc = data != null && data.TryGetValue("inlineDoc", out var raw) ? raw as IDictionary<string, object> : null;
        var file = doc != null && doc.TryGetValue("file", out var f) ? f?.ToString() : null;
        if (file == null)
            return new ScriptResponse { Data = "no inlineDoc", StatusCode = 404 };
        var read = await GetFileAsync(context.Runtime.Domain, "fsl-flow", context.Instance.Id.ToString(), file);
        return new ScriptResponse { Data = read };
    }
}
