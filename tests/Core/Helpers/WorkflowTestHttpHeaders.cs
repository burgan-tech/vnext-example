namespace Core.IntegrationTests.Helpers;

/// <summary>
/// Common HTTP header maps for vNext Runtime integration tests (e.g. <c>role</c> for state filtering / transitions).
/// </summary>
public static class WorkflowTestHttpHeaders
{
    public static Dictionary<string, string>? Role(string role) =>
        string.IsNullOrEmpty(role) ? null : new Dictionary<string, string> { ["role"] = role };
}
