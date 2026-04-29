using System.Text.Json;

namespace Core.IntegrationTests.Helpers;

/// <summary>
/// Parses <c>GET .../functions/state</c> JSON for tests. Shape may evolve; extend with new branches if needed.
/// State cevabında geçişler genelde <c>transitions[].name</c> ile listelenir; bkz. <see cref="TransitionsContainKey"/>.
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
