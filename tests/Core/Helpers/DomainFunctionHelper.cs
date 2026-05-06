using System.Net;
using System.Text.Json;
using VNext.Testing.Sdk.Client;
using Xunit;

namespace Core.IntegrationTests.Helpers;

/// <summary>
/// Helpers for domain-scoped function calls (<c>GET .../functions/{functionName}</c>)
/// that are NOT tied to a specific workflow key.
/// </summary>
public static class DomainFunctionHelper
{
    public static async Task<JsonElement> CallDomainScopeFunctionAsync(
        VNextApiClient api,
        string domain,
        string apiVersion,
        string functionName,
        Dictionary<string, string>? queryParams = null
    )
    {
        var qs = queryParams != null
            ? string.Join("&", queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"))
            : "";
        var path = $"/api/v{apiVersion}/{domain}/functions/{Uri.EscapeDataString(functionName)}";
        if (!string.IsNullOrEmpty(qs))
            path += "?" + qs;

        var response = await api.GetRawAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Body;
    }
}
