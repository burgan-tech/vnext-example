using System.Text.Json;
using Xunit;

namespace Core.IntegrationTests.Helpers;

/// <summary>
/// <see cref="JsonElement"/> üzerinde sık kullanılan test assertion'ları (ör. GetInstance <c>attributes</c>).
/// </summary>
public static class JsonElementAssertions
{
    public static void AssertPropertyTrue(
        JsonElement parent,
        string name,
        string? message = null
    )
    {
        Assert.True(
            parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True,
            message ?? $"Property '{name}' should be JSON true."
        );
    }

    public static void AssertPropertyString(
        JsonElement parent,
        string name,
        string expected,
        string? message = null
    )
    {
        Assert.True(
            parent.TryGetProperty(name, out var el) && el.GetString() == expected,
            message ?? $"Property '{name}' should equal '{expected}'."
        );
    }

    public static void AssertPropertyNonEmptyString(
        JsonElement parent,
        string name,
        string? message = null
    )
    {
        Assert.True(
            parent.TryGetProperty(name, out var el)
                && el.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(el.GetString()),
            message ?? $"Property '{name}' should be a non-empty string."
        );
    }
}
