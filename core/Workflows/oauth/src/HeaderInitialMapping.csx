using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// OAuth2 Client Validation Mapping - Client validation mapping
// This mapping is used to validate OAuth2 client credentials.
/// </summary>
public class HeaderInitialMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    /// <summary>
    /// Process the client authentication result and merge it into the workflow instance
    /// </summary>
    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        LogInformation("Header Initial Mapping");
        return new ScriptResponse
            {
                Key = "header-initial-mapping",
                Data = new
                {
                    status = "initial-header",
                    headers = new
                    {
                        requestId = context.Headers?["x-request-id"],
                        deviceInfo = context.Headers?["x-device-info"],
                        deviceId = context.Headers?["x-device-id"],
                        acceptLanguage = context.Headers?["accept-language"],
                        forwardedFor = context.Headers?["x-forwarded-for"]
                    }
                }
            };
    }
}