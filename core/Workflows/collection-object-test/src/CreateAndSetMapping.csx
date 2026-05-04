using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

public class CreateAndSetMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("CreateAndSet Test - Starting");

            dynamic item1 = CreateObject();
            SetProperty(item1, "id", "item-001");
            SetProperty(item1, "name", "Alice");
            SetProperty(item1, "age", 30);
            SetProperty(item1, "status", "active");

            dynamic item2 = CreateObject();
            SetProperty(item2, "id", "item-002");
            SetProperty(item2, "name", "Bob");
            SetProperty(item2, "age", 25);
            SetProperty(item2, "status", "inactive");

            dynamic item3 = CreateObject();
            SetProperty(item3, "id", "item-003");
            SetProperty(item3, "name", "Charlie");
            SetProperty(item3, "age", 35);
            SetProperty(item3, "status", "active");

            var items = CreateList();
            ListAdd(items, item1);
            ListAdd(items, item2);
            ListAdd(items, item3);

            dynamic metadata = CreateObject();
            SetProperty(metadata, "createdAt", DateTime.UtcNow.ToString("o"));
            SetProperty(metadata, "source", "collection-object-test");
            SetProperty(metadata, "itemCount", items.Count);

            LogInformation($"CreateAndSet Test - Created {items.Count} items");

            var data = context.Instance.Data;
            dynamic result = new ExpandoObject();

            if (HasProperty(data, "testId"))
                result.testId = data.testId;
            if (HasProperty(data, "startedAt"))
                result.startedAt = data.startedAt;

            result.items = items;
            result.metadata = metadata;

            result.createAndSetResult = new ExpandoObject();
            result.createAndSetResult.success = true;
            result.createAndSetResult.objectsCreated = 3;
            result.createAndSetResult.listItemCount = items.Count;
            result.createAndSetResult.propertiesSet = true;

            return Task.FromResult(new ScriptResponse { Data = result });
        }
        catch (Exception ex)
        {
            LogError($"CreateAndSet Test - Failed: {ex.Message}");
            dynamic errResult = new ExpandoObject();
            errResult.error = ex.Message;
            return Task.FromResult(new ScriptResponse { Data = errResult });
        }
    }
}
