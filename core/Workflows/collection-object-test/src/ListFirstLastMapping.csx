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
            var firstActive = ListFirst(items, (Func<object, bool>)(x => GetPropertyValue(x, "status")?.ToString() == "active"));
            var firstInactive = ListFirst(items, (Func<object, bool>)(x => GetPropertyValue(x, "status")?.ToString() == "inactive"));
            var firstNotFound = ListFirst(items, (Func<object, bool>)(x => GetPropertyValue(x, "status")?.ToString() == "admin"));

            var last = ListLast(items);
            var lastActive = ListLast(items, (Func<object, bool>)(x => GetPropertyValue(x, "status")?.ToString() == "active"));

            var emptyFirst = ListFirst(CreateList());
            var emptyLast = ListLast(CreateList());

            var firstName = first != null ? GetPropertyValue(first, "name")?.ToString() : null;
            var lastName = last != null ? GetPropertyValue(last, "name")?.ToString() : null;

            LogInformation($"First: {firstName}, Last: {lastName}");

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

            var firstId = first != null ? GetPropertyValue(first, "id")?.ToString() : null;
            var lastId = last != null ? GetPropertyValue(last, "id")?.ToString() : null;
            var firstActiveId = firstActive != null ? GetPropertyValue(firstActive, "id")?.ToString() : null;
            var firstActiveName = firstActive != null ? GetPropertyValue(firstActive, "name")?.ToString() : null;
            var firstInactiveName = firstInactive != null ? GetPropertyValue(firstInactive, "name")?.ToString() : null;
            var lastActiveId = lastActive != null ? GetPropertyValue(lastActive, "id")?.ToString() : null;
            var lastActiveName = lastActive != null ? GetPropertyValue(lastActive, "name")?.ToString() : null;

            result.firstLastResult = new ExpandoObject();
            result.firstLastResult.success = true;
            result.firstLastResult.firstName = firstName ?? "null";
            result.firstLastResult.lastName = lastName ?? "null";
            result.firstLastResult.firstActiveName = firstActiveName ?? "null";
            result.firstLastResult.firstInactiveName = firstInactiveName ?? "null";
            result.firstLastResult.lastActiveName = lastActiveName ?? "null";
            result.firstLastResult.firstNotFoundIsNull = firstNotFound == null;
            result.firstLastResult.emptyFirstIsNull = emptyFirst == null;
            result.firstLastResult.emptyLastIsNull = emptyLast == null;
            result.firstLastResult.firstWorked = firstId == "item-001";
            result.firstLastResult.lastWorked = lastId == "item-003";
            result.firstLastResult.firstWithPredicateWorked = firstActiveId == "item-001";
            result.firstLastResult.lastWithPredicateWorked = lastActiveId == "item-003";

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
