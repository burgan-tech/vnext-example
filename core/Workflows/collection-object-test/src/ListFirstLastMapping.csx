using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

public class ListFirstLastMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("ListFirst and ListLast Test - Starting");

            var data = context.Instance.Data;
            var items = GetList(data, "items");

            var first = ListFirst(items);
            var firstActive = ListFirst(items, x => x.status == "active");
            var firstInactive = ListFirst(items, x => x.status == "inactive");
            var firstNotFound = ListFirst(items, x => x.status == "admin");

            var last = ListLast(items);
            var lastActive = ListLast(items, x => x.status == "active");

            var emptyFirst = ListFirst(CreateList());
            var emptyLast = ListLast(CreateList());

            LogInformation($"First: {first?.name}, Last: {last?.name}");

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
            if (HasProperty(data, "getListResult"))
                result.getListResult = data.getListResult;
            if (HasProperty(data, "filterCountAnyResult"))
                result.filterCountAnyResult = data.filterCountAnyResult;

            result.firstLastResult = new ExpandoObject();
            result.firstLastResult.success = true;
            result.firstLastResult.firstName = (string)(first?.name ?? "null");
            result.firstLastResult.lastName = (string)(last?.name ?? "null");
            result.firstLastResult.firstActiveName = (string)(firstActive?.name ?? "null");
            result.firstLastResult.firstInactiveName = (string)(firstInactive?.name ?? "null");
            result.firstLastResult.lastActiveName = (string)(lastActive?.name ?? "null");
            result.firstLastResult.firstNotFoundIsNull = firstNotFound == null;
            result.firstLastResult.emptyFirstIsNull = emptyFirst == null;
            result.firstLastResult.emptyLastIsNull = emptyLast == null;
            result.firstLastResult.firstWorked = first?.id == "item-001";
            result.firstLastResult.lastWorked = last?.id == "item-003";
            result.firstLastResult.firstWithPredicateWorked = firstActive?.id == "item-001";
            result.firstLastResult.lastWithPredicateWorked = lastActive?.id == "item-003";

            return Task.FromResult(new ScriptResponse { Data = result });
        }
        catch (Exception ex)
        {
            LogError($"ListFirst/Last Test - Failed: {ex.Message}");
            dynamic errResult = new ExpandoObject();
            errResult.error = ex.Message;
            return Task.FromResult(new ScriptResponse { Data = errResult });
        }
    }
}
