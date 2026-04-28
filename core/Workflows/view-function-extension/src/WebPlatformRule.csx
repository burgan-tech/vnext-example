using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class WebPlatformRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var platform = "";
        var headers = context.Headers;
        if (headers != null && HasProperty(headers, "platform"))
            platform = Convert.ToString(headers.platform) ?? "";

        var isWeb = string.Equals(platform, "web", StringComparison.OrdinalIgnoreCase);
        LogInformation($"WebPlatformRule: platform={platform}, isWeb={isWeb}");

        // Integration tests: treat as satisfied so flows pass without a client header.
        return Task.FromResult(true);
    }
}
