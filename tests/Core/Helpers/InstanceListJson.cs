using System.Text.Json;

namespace Core.IntegrationTests.Helpers;

/// <summary>
/// Parses <c>GET .../workflows/{workflow}/instances</c> list responses.
/// Standard runtime envelope (see <c>vnext-runtime/README.tr.md</c>, "Response Formati"):
/// <code>
/// {
///   "data": [ { "id": "...", "key": "...", "flow": "...", "currentState": "...", "status": "...", "attributes": { ... } } ],
///   "pagination": { "page": N, "pageSize": N, "totalCount": N, "totalPages": N }
/// }
/// </code>
/// Legacy/alternate wrappers are also handled: root array or <c>items</c>.
/// </summary>
public static class InstanceListJson
{
    /// <summary>
    /// Returns instance rows from a list response as <see cref="JsonElement"/>[].
    /// Returns empty list when none found.
    /// </summary>
    public static IReadOnlyList<JsonElement> ExtractItems(JsonElement body)
    {
        if (body.ValueKind == JsonValueKind.Array)
            return body.EnumerateArray().ToList();

        if (body.ValueKind != JsonValueKind.Object)
            return Array.Empty<JsonElement>();

        if (body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray().ToList();

        if (body.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            return items.EnumerateArray().ToList();

        return Array.Empty<JsonElement>();
    }

    /// <summary>
    /// Reads <paramref name="propertyName"/> from the <c>pagination</c> object
    /// (e.g. <c>page</c>, <c>pageSize</c>, <c>totalCount</c>, <c>totalPages</c>) as int; otherwise null.
    /// </summary>
    public static int? TryGetPaginationInt(JsonElement body, string propertyName)
    {
        if (
            body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("pagination", out var pag)
            && pag.ValueKind == JsonValueKind.Object
            && pag.TryGetProperty(propertyName, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out var value)
        )
            return value;

        return null;
    }

    /// <summary>
    /// Whether the list contains a row whose <c>id</c> equals <paramref name="instanceId"/> (case-sensitive).
    /// </summary>
    public static bool ContainsInstanceId(JsonElement body, string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId))
            return false;

        foreach (var item in ExtractItems(body))
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            if (
                item.TryGetProperty("id", out var idEl)
                && idEl.ValueKind == JsonValueKind.String
                && string.Equals(idEl.GetString(), instanceId, StringComparison.Ordinal)
            )
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns <c>attributes</c> object from an instance row, or null.
    /// </summary>
    public static JsonElement? TryGetAttributes(JsonElement instance)
    {
        if (
            instance.ValueKind == JsonValueKind.Object
            && instance.TryGetProperty("attributes", out var attrs)
            && attrs.ValueKind == JsonValueKind.Object
        )
            return attrs;
        return null;
    }

    /// <summary>
    /// Returns <c>id</c> from an instance row, or null.
    /// </summary>
    public static string? TryGetId(JsonElement instance)
    {
        if (
            instance.ValueKind == JsonValueKind.Object
            && instance.TryGetProperty("id", out var idEl)
            && idEl.ValueKind == JsonValueKind.String
        )
            return idEl.GetString();
        return null;
    }
}
