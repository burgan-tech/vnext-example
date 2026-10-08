using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// file-offload-lab: the to-review transition mapping. The client sends the file under <c>upload</c> (not an
/// x-storage path); the mapping relocates it to <c>passport</c>, so the runtime's Trusted offload of the mapping
/// output (CreateTransitionRecordStep) is what turns <c>content</c> into a handle.
/// </summary>
public class FoRelocateUploadMapping : ScriptBase, ITransitionMapping
{
    public Task<dynamic> Handler(ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        if (body != null && body.TryGetValue("upload", out var upload) && upload != null)
            target["passport"] = upload;
        target["relocated"] = true;
        return Task.FromResult<dynamic>(result);
    }
}
