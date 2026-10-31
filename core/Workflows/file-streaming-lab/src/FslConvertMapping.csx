using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// file-streaming-lab: "convert" onEntry (vnext-client-sdk-core#142-A). Reads files[0] through GetFileAsync, processes
/// it with System.IO streams (OpenRead + BinaryReader in, MemoryStream + BinaryWriter out — what a PDF/TIFF library
/// would be handed), and stores the result with PutFileAsync as the instance's "converted" x-storage file.
/// Transform: the ASCII header "FSL-CONVERTED\n" followed by the source bytes in reverse order — deterministic, so the
/// test recomputes it and compares the served bytes.
/// </summary>
public class FslConvertMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        var files = data != null && data.TryGetValue("files", out var raw) ? raw as IList<object> : null;
        var first = files != null && files.Count > 0 ? files[0] as IDictionary<string, object> : null;
        dynamic result = new ExpandoObject();
        if (first == null || !first.TryGetValue("file", out var fileId) || fileId == null)
        {
            result.convertError = "no files[0]";
            return new ScriptResponse { Data = result };
        }

        var domain = context.Runtime.Domain;
        var instance = context.Instance.Id.ToString();
        var source = await GetFileAsync(domain, "fsl-flow", instance, fileId.ToString());

        var output = new MemoryStream();
        using (var reader = new BinaryReader(source.OpenRead()))
        using (var writer = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("FSL-CONVERTED\n"));
            var bytes = reader.ReadBytes((int)source.Size);
            Array.Reverse(bytes);
            writer.Write(bytes);
        }
        output.Position = 0;

        var stored = await PutFileAsync(domain, "fsl-flow", instance, "fsl-minio", output, "converted.bin", "application/octet-stream");
        result.converted = stored;
        result.convertedFrom = source.ETag;
        return new ScriptResponse { Data = result };
    }
}
