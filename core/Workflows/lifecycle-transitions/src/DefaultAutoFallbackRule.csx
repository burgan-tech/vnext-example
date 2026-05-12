using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// processing-state: only true when testPath is not pass nor fail (complements TestPathPassRule / TestPathFailRule).
public class DefaultAutoFallbackRule : ScriptBase, IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance?.Data;
        if (data == null)
        {
            LogInformation("DefaultAutoFallbackRule: data null, fallback true");
            return true;
        }

        if (!HasProperty(data, "testPath"))
        {
            LogInformation("DefaultAutoFallbackRule: testPath absent, fallback true");
            return true;
        }

        var testPath = data.testPath?.ToString();
        var neither = testPath != "pass" && testPath != "fail";
        LogInformation($"DefaultAutoFallbackRule: testPath={testPath}, neitherPassNorFail={neither}");
        return neither;
    }
}
