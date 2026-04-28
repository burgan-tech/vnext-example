using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class TestPathFailRule : ScriptBase, IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance?.Data;
        if (data == null)
        {
            LogInformation("TestPathFailRule: data is null, returning false");
            return false;
        }

        if (HasProperty(data, "testPath"))
        {
            var testPath = data.testPath?.ToString();
            var result = testPath == "fail";
            LogInformation($"TestPathFailRule: testPath={testPath}, result={result}");
            return result;
        }

        LogInformation("TestPathFailRule: testPath not found, returning false");
        return false;
    }
}
