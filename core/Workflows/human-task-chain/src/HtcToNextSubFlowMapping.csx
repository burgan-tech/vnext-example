using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Hands the chain down one level: decrements `hops`, carries `testId`, and gives the child its OWN
/// humanTask text. The distinct text per level is the point — a test can then tell whether the
/// listed title came from the leaf or was taken from an ancestor.
/// </summary>
/// <remarks>
/// It also FORWARDS the caller's identity (<c>sub</c>, <c>act_sub</c>). Propagating the credential into
/// a SubFlow is the flow author's job: the runtime inherits the ambient user only for the first
/// child (the post-commit start job); from depth 2 on, SubflowStarter sends the child exactly the
/// headers this mapping returns, and a child started without them records no creator — so
/// <c>$InstanceStarter</c> / <c>$InstanceBehalfOfStarter</c> at a deeper leaf could never match.
/// Role headers are deliberately NOT forwarded: the chain's role-based tests rely on the child
/// being started without them.
/// </remarks>
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

        // The corporate leaf's grants read the LEAF's own data ($user.$.context.Instance.Data.customerId)
        // and its routing reads `mode`, so both travel down every hop.
        if (data != null && data.TryGetValue("mode", out var mode) && mode != null)
        {
            childInput.mode = mode;
        }

        if (data != null && data.TryGetValue("customerId", out var customerId) && customerId != null)
        {
            childInput.customerId = customerId;
        }

        dynamic humanTask = new ExpandoObject();
        humanTask.title = "HT-D step";
        humanTask.description = "HT-D step description";
        childInput.humanTask = humanTask;

        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "sub", "act_sub" })
        {
            try
            {
                string? value = (string?)context.Headers[name];
                if (!string.IsNullOrEmpty(value)) headers[name] = value;
            }
            catch (Exception)
            {
                // header absent on this request
            }
        }

        LogInformation($"HtcToNextSubFlowMapping: descending to HT-D with hops={hops - 1}, identity headers={headers.Count}");
        return Task.FromResult(new ScriptResponse { Data = childInput, Headers = headers });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }
}
