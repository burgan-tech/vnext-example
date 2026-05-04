using System.Text.Json;

namespace Core.IntegrationTests.Helpers;

/// <summary>
/// Parses <c>GET .../functions/state</c> JSON for tests. Shape may evolve; extend with new branches if needed.
/// Transitions are usually listed as <c>transitions[].name</c>; see <see cref="TransitionsContainKey"/>.
/// </summary>
public static class StateFunctionJson
{
    /// <summary>
    /// Resolves current state key/name from a State function response body.
    /// </summary>
    public static string ExtractStateName(JsonElement body)
    {
        if (
            body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("state", out var direct)
            && direct.ValueKind == JsonValueKind.String
        )
            return direct.GetString() ?? string.Empty;

        if (
            body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("state", out var nested)
            && nested.ValueKind == JsonValueKind.String
        )
            return nested.GetString() ?? string.Empty;

        return string.Empty;
    }

    /// <summary>
    /// Runtime instance status from state function body (e.g. A/B/C/F); shape may vary by version.
    /// </summary>
    public static string? ExtractStatus(JsonElement body)
    {
        if (
            body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("status", out var direct)
            && direct.ValueKind == JsonValueKind.String
        )
            return direct.GetString();

        if (
            body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("status", out var nested)
            && nested.ValueKind == JsonValueKind.String
        )
            return nested.GetString();

        return null;
    }

    /// <summary>
    /// Returns <c>activeCorrelations[]</c> from a State function response body (root or under <c>data</c>).
    /// Empty list when missing or not an array. See <c>vnext-runtime/doc/tr/flow/function.md</c>
    /// "Sub-flow Korelasyonlari" tablosu (alanlar: <c>correlationId</c>, <c>parentState</c>,
    /// <c>subFlowInstanceId</c>, <c>subFlowType</c>, <c>subFlowDomain</c>, <c>subFlowName</c>,
    /// <c>subFlowVersion</c>, <c>isCompleted</c>, <c>status</c>).
    /// </summary>
    public static IReadOnlyList<JsonElement> ExtractActiveCorrelations(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return Array.Empty<JsonElement>();

        if (
            body.TryGetProperty("activeCorrelations", out var direct)
            && direct.ValueKind == JsonValueKind.Array
        )
            return direct.EnumerateArray().ToList();

        if (
            body.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("activeCorrelations", out var nested)
            && nested.ValueKind == JsonValueKind.Array
        )
            return nested.EnumerateArray().ToList();

        return Array.Empty<JsonElement>();
    }

    /// <summary>
    /// Locates the active correlation whose <c>subFlowInstanceId</c> equals <paramref name="subFlowInstanceId"/>.
    /// Returns <c>true</c> and assigns <paramref name="correlation"/> on match.
    /// Use after starting a SubProcess (tip 14) / SubFlow to verify runtime correlation tracking,
    /// not just the parent <c>attributes</c> data — see <c>vnext-tests-as-code</c> skill section
    /// "ActiveCorrelations ile SubProcess / SubFlow teyidi".
    /// </summary>
    public static bool TryFindActiveCorrelationBySubFlowInstanceId(
        JsonElement body,
        string subFlowInstanceId,
        out JsonElement correlation
    )
    {
        correlation = default;
        if (string.IsNullOrEmpty(subFlowInstanceId))
            return false;

        foreach (var c in ExtractActiveCorrelations(body))
        {
            if (
                c.ValueKind == JsonValueKind.Object
                && c.TryGetProperty("subFlowInstanceId", out var idEl)
                && idEl.ValueKind == JsonValueKind.String
                && string.Equals(idEl.GetString(), subFlowInstanceId, StringComparison.Ordinal)
            )
            {
                correlation = c;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads <c>subFlowType</c> from a single correlation element (e.g. <c>"P"</c> for SubProcess,
    /// <c>"F"</c>/<c>"SubFlow"</c> for SubFlow depending on runtime version).
    /// </summary>
    public static string? ExtractSubFlowType(JsonElement correlation)
    {
        if (
            correlation.ValueKind == JsonValueKind.Object
            && correlation.TryGetProperty("subFlowType", out var t)
            && t.ValueKind == JsonValueKind.String
        )
            return t.GetString();

        return null;
    }

    /// <summary>
    /// Whether <paramref name="stateBody"/><c>.transitions[]</c> contains an item whose <c>name</c> equals <paramref name="transitionKey"/> (primary for runtime state function), or whose <c>key</c> equals it if present.
    /// </summary>
    public static bool TransitionsContainKey(JsonElement stateBody, string transitionKey)
    {
        if (
            stateBody.ValueKind != JsonValueKind.Object
            || !stateBody.TryGetProperty("transitions", out var transitions)
        )
            return false;

        if (transitions.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var t in transitions.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object)
                continue;

            if (
                t.TryGetProperty("name", out var nameEl)
                && nameEl.ValueKind == JsonValueKind.String
            )
            {
                if (string.Equals(nameEl.GetString(), transitionKey, StringComparison.Ordinal))
                    return true;
            }

            if (
                t.TryGetProperty("key", out var keyEl)
                && keyEl.ValueKind == JsonValueKind.String
            )
            {
                if (string.Equals(keyEl.GetString(), transitionKey, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }
}
