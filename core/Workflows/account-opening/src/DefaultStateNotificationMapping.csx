using BBT.Workflow.Scripting;

public class DefaultNotificationMapping : IStateNotificationMapping
{
    public Task<StateNotificationMetadata> EnrichAsync(ScriptContext context)
    {
        var headers = (Dictionary<string, string>)context.Headers;

        var metadata = new Dictionary<string, string>();

        if (headers.TryGetValue("x-device-id", out var deviceId) && deviceId is not null)
            metadata["X-Device-Id"] = deviceId.ToString()!;

        if (headers.TryGetValue("x-token-id", out var tokenId) && tokenId is not null)
            metadata["X-token-Id"] = tokenId.ToString()!;

        if (headers.TryGetValue("x-installation-id", out var installationId) && tokenId is not null)
            metadata["X-Installation-Id"] = installationId.ToString()!;

        if (headers.TryGetValue("x-request-id", out var requestId) && requestId is not null)
            metadata["X-Request-Id"] = requestId.ToString()!;

        return Task.FromResult(new StateNotificationMetadata
        {
            Metadata = metadata
        });
    }
}