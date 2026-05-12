using System.Text.Json;
using Xunit;

namespace Core.IntegrationTests.Tests.ViewFunctionExtensionTestWorkflow;

/// <summary>
/// Extension-specific assertions for <c>view-function-extension-test-workflow</c>.
/// <para>
/// Runtime converts kebab-case extension keys to camelCase in the response.
/// For example, <c>vfe-global-extension</c> becomes <c>vfeGlobalExtension</c>.
/// All lookups try both the original key and the camelCase variant.
/// </para>
/// </summary>
public static class VfeExtensionAssertions
{
    public static void AssertExtensionPresent(JsonElement body, string extensionKey)
    {
        Assert.True(
            TryGetExtensionData(body, extensionKey, out _),
            $"Extension '{extensionKey}' (or '{ToCamelCase(extensionKey)}') should be present in the response."
        );
    }

    public static void AssertExtensionAbsent(JsonElement body, string extensionKey)
    {
        Assert.False(
            TryGetExtensionData(body, extensionKey, out _),
            $"Extension '{extensionKey}' (or '{ToCamelCase(extensionKey)}') should NOT be present in the response."
        );
    }

    public static void AssertExtensionTypeMarker(
        JsonElement body,
        string extensionKey,
        string expectedType
    )
    {
        Assert.True(
            TryGetExtensionData(body, extensionKey, out var extData),
            $"Extension '{extensionKey}' (or '{ToCamelCase(extensionKey)}') should be present."
        );
        Assert.True(
            extData.TryGetProperty("vfeExtensionType", out var typeEl)
                && typeEl.ValueKind == JsonValueKind.String,
            $"Extension '{extensionKey}' should have 'vfeExtensionType' string property."
        );
        Assert.Equal(expectedType, typeEl.GetString());
    }

    private static bool TryGetExtensionData(
        JsonElement body,
        string extensionKey,
        out JsonElement extensionData
    )
    {
        extensionData = default;
        var camelKey = ToCamelCase(extensionKey);

        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("extensions", out var extensions)
            && extensions.ValueKind == JsonValueKind.Object)
        {
            if (extensions.TryGetProperty(extensionKey, out extensionData))
                return true;
            if (extensions.TryGetProperty(camelKey, out extensionData))
                return true;
        }

        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("extensions", out var nestedExtensions)
            && nestedExtensions.ValueKind == JsonValueKind.Object)
        {
            if (nestedExtensions.TryGetProperty(extensionKey, out extensionData))
                return true;
            if (nestedExtensions.TryGetProperty(camelKey, out extensionData))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Converts kebab-case string to camelCase: <c>vfe-global-extension</c> → <c>vfeGlobalExtension</c>.
    /// </summary>
    private static string ToCamelCase(string kebab)
    {
        var parts = kebab.Split('-');
        if (parts.Length <= 1)
            return kebab;
        return parts[0] + string.Concat(
            parts.Skip(1).Select(p =>
                p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]
            )
        );
    }
}
