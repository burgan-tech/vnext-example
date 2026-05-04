using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

public class ListSelectMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("ListSelect Test - Starting");

            var data = context.Instance.Data;
            var items = GetList(data, "items");

            var names = ListSelect<string>(items, x => (string)x.name);
            var ages = ListSelect<int>(items, x => (int)x.age);
            var labels = ListSelect<string>(items, x => $"{x.name} ({x.status})");
            var emptyResult = ListSelect<string>(CreateList(), x => (string)x.name);

            LogInformation($"ListSelect: {names.Count} names, {ages.Count} ages");

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
            if (HasProperty(data, "firstLastResult"))
                result.firstLastResult = data.firstLastResult;

            result.listSelectResult = new ExpandoObject();
            result.listSelectResult.success = true;
            result.listSelectResult.names = names;
            result.listSelectResult.ages = ages;
            result.listSelectResult.labels = labels;
            result.listSelectResult.namesCount = names.Count;
            result.listSelectResult.agesCount = ages.Count;
            result.listSelectResult.emptySelectCount = emptyResult.Count;
            result.listSelectResult.selectStringWorked = names.Count == 3 && names[0] == "Alice";
            result.listSelectResult.selectIntWorked = ages.Count == 3 && ages[0] == 30;
            result.listSelectResult.selectTransformWorked = labels.Count == 3;
            result.listSelectResult.emptySelectWorked = emptyResult.Count == 0;

            return Task.FromResult(new ScriptResponse { Data = result });
        }
        catch (Exception ex)
        {
            LogError($"ListSelect Test - Failed: {ex.Message}");
            dynamic errResult = new ExpandoObject();
            errResult.error = ex.Message;
            return Task.FromResult(new ScriptResponse { Data = errResult });
        }
    }
}
