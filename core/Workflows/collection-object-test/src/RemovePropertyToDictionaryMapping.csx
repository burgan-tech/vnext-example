using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

public class RemovePropertyToDictionaryMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("RemoveProperty and ToDictionary Test - Starting");

            dynamic testObj = CreateObject();
            SetProperty(testObj, "id", "test-001");
            SetProperty(testObj, "name", "Test Object");
            SetProperty(testObj, "tempField", "this will be removed");
            SetProperty(testObj, "keepField", "this stays");

            var hadTempField = HasProperty(testObj, "tempField");
            var hadKeepField = HasProperty(testObj, "keepField");

            var removeResult = RemoveProperty(testObj, "tempField");

            var hasTempFieldAfter = HasProperty(testObj, "tempField");
            var hasKeepFieldAfter = HasProperty(testObj, "keepField");

            var removeNonExistent = RemoveProperty(testObj, "doesNotExist");

            var dict = ToDictionary(testObj);
            var dictHasId = dict.ContainsKey("id");
            var dictHasName = dict.ContainsKey("name");
            var dictHasTemp = dict.ContainsKey("tempField");
            var dictHasKeep = dict.ContainsKey("keepField");

            var emptyDict = ToDictionary(null);

            LogInformation($"RemoveProperty: removed={removeResult}, dictCount={dict.Count}");

            var data = context.Instance.Data;
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
            if (HasProperty(data, "listSelectResult"))
                result.listSelectResult = data.listSelectResult;
            if (HasProperty(data, "listAddRemoveResult"))
                result.listAddRemoveResult = data.listAddRemoveResult;

            result.removeToDictResult = new ExpandoObject();
            result.removeToDictResult.success = true;
            result.removeToDictResult.hadTempFieldBefore = hadTempField;
            result.removeToDictResult.hadKeepFieldBefore = hadKeepField;
            result.removeToDictResult.removePropertyResult = removeResult;
            result.removeToDictResult.hasTempFieldAfterRemove = hasTempFieldAfter;
            result.removeToDictResult.hasKeepFieldAfterRemove = hasKeepFieldAfter;
            result.removeToDictResult.removeNonExistentReturnsFalse = !removeNonExistent;
            result.removeToDictResult.dictCount = dict.Count;
            result.removeToDictResult.dictHasId = dictHasId;
            result.removeToDictResult.dictHasName = dictHasName;
            result.removeToDictResult.dictRemovedTempField = !dictHasTemp;
            result.removeToDictResult.dictKeptKeepField = dictHasKeep;
            result.removeToDictResult.nullToDictReturnsEmpty = emptyDict.Count == 0;
            result.removeToDictResult.removePropertyWorked = removeResult && !hasTempFieldAfter && hasKeepFieldAfter;
            result.removeToDictResult.toDictionaryWorked = dictHasId && dictHasName && !dictHasTemp && dictHasKeep;

            return Task.FromResult(new ScriptResponse { Data = result });
        }
        catch (Exception ex)
        {
            LogError($"RemoveProperty/ToDictionary Test - Failed: {ex.Message}");
            dynamic errResult = new ExpandoObject();
            errResult.error = ex.Message;
            return Task.FromResult(new ScriptResponse { Data = errResult });
        }
    }
}
