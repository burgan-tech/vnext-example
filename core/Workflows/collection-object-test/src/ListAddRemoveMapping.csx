using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

public class ListAddRemoveMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("ListAdd and ListRemove Test - Starting");

            var data = context.Instance.Data;
            var items = GetList(data, "items");

            var countBefore = ListCount(items);

            dynamic newItem = CreateObject();
            SetProperty(newItem, "id", "item-004");
            SetProperty(newItem, "name", "Diana");
            SetProperty(newItem, "age", 28);
            SetProperty(newItem, "status", "active");

            ListAdd(items, newItem);
            var countAfterAdd = ListCount(items);

            var removedCount = ListRemove(items, (Func<object, bool>)(x => GetPropertyValue(x, "status")?.ToString() == "inactive"));
            var countAfterRemove = ListCount(items);
            var hasInactiveAfterRemove = ListAny(items, (Func<object, bool>)(x => GetPropertyValue(x, "status")?.ToString() == "inactive"));

            LogInformation(
                $"Add/Remove: before={countBefore}, afterAdd={countAfterAdd}, removed={removedCount}, afterRemove={countAfterRemove}"
            );

            dynamic result = new ExpandoObject();

            if (HasProperty(data, "testId"))
                result.testId = data.testId;
            if (HasProperty(data, "startedAt"))
                result.startedAt = data.startedAt;
            if (HasProperty(data, "metadata"))
                result.metadata = data.metadata;
            if (HasProperty(data, "createAndSetResult"))
                result.createAndSetResult = data.createAndSetResult;
            if (HasProperty(data, "getListResult"))
                result.getListResult = data.getListResult;
            if (HasProperty(data, "filterCountAnyResult"))
                result.filterCountAnyResult = data.filterCountAnyResult;
            if (HasProperty(data, "firstLastResult"))
                result.firstLastResult = data.firstLastResult;
            if (HasProperty(data, "listSelectResult"))
                result.listSelectResult = data.listSelectResult;

            result.items = items;

            result.listAddRemoveResult = new ExpandoObject();
            result.listAddRemoveResult.success = true;
            result.listAddRemoveResult.countBefore = countBefore;
            result.listAddRemoveResult.countAfterAdd = countAfterAdd;
            result.listAddRemoveResult.removedCount = removedCount;
            result.listAddRemoveResult.countAfterRemove = countAfterRemove;
            result.listAddRemoveResult.hasInactiveAfterRemove = hasInactiveAfterRemove;
            result.listAddRemoveResult.addWorked = countAfterAdd == countBefore + 1;
            result.listAddRemoveResult.removeWorked = removedCount == 1 && !hasInactiveAfterRemove;

            return Task.FromResult(new ScriptResponse { Data = result });
        }
        catch (Exception ex)
        {
            LogError($"ListAdd/Remove Test - Failed: {ex.Message}");
            dynamic errResult = new ExpandoObject();
            errResult.error = ex.Message;
            return Task.FromResult(new ScriptResponse { Data = errResult });
        }
    }
}
