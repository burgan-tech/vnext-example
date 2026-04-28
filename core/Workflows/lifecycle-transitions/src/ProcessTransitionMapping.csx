using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class ProcessTransitionMapping : ScriptBase, ITransitionMapping
{
    public async Task<dynamic> Handler(ScriptContext context)
    {
        var data = context.Instance.Data;

        dynamic result = new ExpandoObject();

        if (HasProperty(data, "testPath"))
            result.testPath = data.testPath;
        if (HasProperty(data, "initialized"))
            result.initialized = data.initialized;
        if (HasProperty(data, "initializedAt"))
            result.initializedAt = data.initializedAt;
        if (HasProperty(data, "stepLog"))
            result.stepLog = data.stepLog;

        result.transitionMappingExecuted = true;
        result.transitionMappingAt = DateTime.UtcNow.ToString("o");

        LogInformation("ProcessTransitionMapping executed");

        return result;
    }
}
