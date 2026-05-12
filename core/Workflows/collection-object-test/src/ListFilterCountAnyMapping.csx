using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

public class ListFilterCountAnyMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("ListFilter, ListCount, ListAny Test - Starting");

            var data = context.Instance.Data;
            var items = GetList(data, "items");

            var activeItems = ListFilter(items, (Func<object, bool>)(x => GetPropertyValue(x, "status")?.ToString() == "active"));
            var totalCount = ListCount(items);
            var activeCount = ListCount(items, (Func<object, bool>)(x => GetPropertyValue(x, "status")?.ToString() == "active"));
            var inactiveCount = ListCount(items, (Func<object, bool>)(x => GetPropertyValue(x, "status")?.ToString() == "inactive"));
            var hasItems = ListAny(items);
            var hasActive = ListAny(items, (Func<object, bool>)(x => GetPropertyValue(x, "status")?.ToString() == "active"));
            var hasAdminRole = ListAny(items, (Func<object, bool>)(x => GetPropertyValue(x, "status")?.ToString() == "admin"));

            var emptyList = CreateList();
            var emptyHasItems = ListAny(emptyList);
            var emptyCount = ListCount(emptyList);

            LogInformation(
                $"ListFilter: active={activeCount}, inactive={inactiveCount}, total={totalCount}"
            );

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

            result.filterCountAnyResult = new ExpandoObject();
            result.filterCountAnyResult.success = true;
            result.filterCountAnyResult.totalCount = totalCount;
            result.filterCountAnyResult.activeCount = activeCount;
            result.filterCountAnyResult.inactiveCount = inactiveCount;
            result.filterCountAnyResult.activeItemsFiltered = activeItems.Count;
            result.filterCountAnyResult.hasItems = hasItems;
            result.filterCountAnyResult.hasActive = hasActive;
            result.filterCountAnyResult.hasAdminRole = hasAdminRole;
            result.filterCountAnyResult.emptyListHasItems = emptyHasItems;
            result.filterCountAnyResult.emptyListCount = emptyCount;
            result.filterCountAnyResult.filterWorked = activeItems.Count == 2;
            result.filterCountAnyResult.countWorked =
                totalCount == 3 && activeCount == 2 && inactiveCount == 1;
            result.filterCountAnyResult.anyWorked =
                hasItems && hasActive && !hasAdminRole && !emptyHasItems;

            return Task.FromResult(new ScriptResponse { Data = result });
        }
        catch (Exception ex)
        {
            LogError($"ListFilter/Count/Any Test - Failed: {ex.Message}");
            dynamic errResult = new ExpandoObject();
            errResult.error = ex.Message;
            errResult.errorStack = ex.StackTrace;
            return Task.FromResult(new ScriptResponse { Data = errResult });
        }
    }
}
