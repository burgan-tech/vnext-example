using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Hands the chain down one level: decrements `hops`, carries `testId`, and gives the child its OWN
/// humanTask text. The distinct text per level is the point — a test can then tell whether the
/// listed title came from the leaf or was taken from an ancestor.
/// </summary>
public class HtcToNextSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;

        var hops = 0;
        if (data != null && data.TryGetValue("hops", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out hops);
        }

        dynamic childInput = new ExpandoObject();
        childInput.hops = hops - 1;

        if (data != null && data.TryGetValue("testId", out var testId) && testId != null)
        {
            childInput.testId = testId;
        }

        dynamic humanTask = new ExpandoObject();
        humanTask.title = "HT-D step";
        humanTask.description = "HT-D step description";
        childInput.humanTask = humanTask;

        LogInformation($"HtcToNextSubFlowMapping: descending to HT-D with hops={hops - 1}");
        return Task.FromResult(new ScriptResponse { Data = childInput });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }
}
