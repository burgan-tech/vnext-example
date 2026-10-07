using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// history-none-lab: order 3 reads what orders 1 and 2 wrote. The data is only in the in-memory
/// buffer at this point (history: none writes nothing until Finish), so a correct summary proves
/// the buffered head is what later tasks read — parallel branches included.
/// </summary>
public class HnSummarizeMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        dynamic result = new ExpandoObject();
        ((IDictionary<string, object>)result)["summary"] = Number(data, "left") + Number(data, "right");
        return Task.FromResult(new ScriptResponse { Data = result });
    }

    private static decimal Number(IDictionary<string, object> data, string key) =>
        data != null && data.TryGetValue(key, out var value) && value != null
            ? Convert.ToDecimal(value.ToString(), System.Globalization.CultureInfo.InvariantCulture)
            : 0m;
}
