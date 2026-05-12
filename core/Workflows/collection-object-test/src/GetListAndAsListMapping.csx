using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

public class GetListAndAsListMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("GetList and AsList Test - Starting");

            var data = context.Instance.Data;

            var items = GetList(data, "items");
            var asListFromValid = AsList(items);
            var asListFromNull = AsList(null);
            var asListFromString = AsList("not a list");

            LogInformation($"GetList Test - Retrieved {items.Count} items");

            dynamic result = new ExpandoObject();

            if (HasProperty(data, "testId"))
                result.testId = data.testId;
            if (HasProperty(data, "startedAt"))
                result.startedAt = data.startedAt;
            if (HasProperty(data, "items"))
                result.items = data.items;
            if (HasProperty(data, "metadata"))
                result.metadata = data.metadata;
            if (HasProperty(data, "createAndSetResult"))
                result.createAndSetResult = data.createAndSetResult;

            result.getListResult = new ExpandoObject();
            result.getListResult.success = true;
            result.getListResult.itemCount = items.Count;
            result.getListResult.getListWorked = items.Count == 3;
            result.getListResult.asListFromValidCount = asListFromValid.Count;
            result.getListResult.asListNullReturnsEmpty = asListFromNull.Count == 0;
            result.getListResult.asListInvalidReturnsEmpty = asListFromString.Count == 0;

            return Task.FromResult(new ScriptResponse { Data = result });
        }
        catch (Exception ex)
        {
            LogError($"GetList Test - Failed: {ex.Message}");
            dynamic errResult = new ExpandoObject();
            errResult.error = ex.Message;
            return Task.FromResult(new ScriptResponse { Data = errResult });
        }
    }
}
