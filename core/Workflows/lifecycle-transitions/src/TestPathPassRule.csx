using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class TestPathPassRule : ScriptBase, IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance?.Data;
        if (data == null)
        {
            LogInformation("TestPathPassRule: data is null, returning false");
            return false;
        }

        if (HasProperty(data, "testPath"))
        {
            var testPath = data.testPath?.ToString();
            var result = testPath == "pass";
            LogInformation($"TestPathPassRule: testPath={testPath}, result={result}");
            return result;
        }

        LogInformation("TestPathPassRule: testPath not found, returning false");
        return false;
    }
}
