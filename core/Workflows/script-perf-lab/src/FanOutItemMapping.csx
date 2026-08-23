using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// script-perf-lab fan-out item input: her item için mevcut fan-out-documents MockLab
/// mock'una (api/fan-out/documents/process) yönlendirir. Output handler bilinçli yok —
/// runtime'ın default paketlemesi (resultKey satırları + Summary) yeterli.
/// </summary>
public class FanOutItemMapping : ScriptBase, IFanOutMapping
{
    public Task<ScriptResponse> ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item)
    {
        var httpTask = task as HttpTask;
        if (httpTask != null)
        {
            var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001");
            var url = httpTask.Url.Replace("API_BASEURL", apiBaseUrl);
            var documentId = item.ItemKey ?? item.Index.ToString();
            httpTask.SetUrl(url + "?documentId=" + Uri.EscapeDataString(documentId));
        }
        return Task.FromResult(new ScriptResponse());
    }
}
