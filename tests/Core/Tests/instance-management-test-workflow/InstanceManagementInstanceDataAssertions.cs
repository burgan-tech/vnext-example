using System.Globalization;
using System.Text.Json;
using Core.IntegrationTests.Helpers;
using Xunit;

namespace Core.IntegrationTests.Tests.InstanceManagementTestWorkflow;

/// <summary>
/// Workflow-specific assertions for instance <c>attributes</c> on <c>instance-management-test-workflow</c>.
/// <c>InitInstanceMgmtMapping.csx</c> under <c>startTransition.onExecutionTasks</c> establishes:
/// <list type="bullet">
///   <item><c>category</c>: string (default "default")</item>
///   <item><c>priority</c>: int (default 1)</item>
///   <item><c>testStarted</c>: true</item>
///   <item><c>startedAt</c>: ISO-8601 UTC string (parsable with DateTimeStyles.RoundtripKind)</item>
/// </list>
/// Per vnext-tests-as-code layout rules, assertions specific to one workflow live in that scenario folder;
/// shared helpers stay in <see cref="JsonElementAssertions"/> and <see cref="InstanceListJson"/>.
/// </summary>
public static class InstanceManagementInstanceDataAssertions
{
    /// <summary>
    /// Validates baseline fields produced by init mapping. If <paramref name="expectedCategory"/> and
    /// <paramref name="expectedPriority"/> are omitted, only presence and contract checks apply.
    /// </summary>
    public static void AssertInitialAttributes(
        JsonElement attributes,
        string? expectedCategory = null,
        int? expectedPriority = null
    )
    {
        JsonElementAssertions.AssertPropertyTrue(
            attributes,
            "testStarted",
            "InitInstanceMgmtMapping should set 'testStarted = true'."
        );

        JsonElementAssertions.AssertPropertyNonEmptyString(
            attributes,
            "startedAt",
            "InitInstanceMgmtMapping should set 'startedAt' as ISO-8601 UTC string."
        );

        var startedAtRaw = attributes.GetProperty("startedAt").GetString()!;
        var parsed = DateTime.Parse(
            startedAtRaw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind
        );
        Assert.True(
            parsed.Kind == DateTimeKind.Utc,
            $"'startedAt' should be UTC (Kind=Utc); got Kind={parsed.Kind}, raw='{startedAtRaw}'."
        );

        JsonElementAssertions.AssertPropertyNonEmptyString(
            attributes,
            "category",
            "InitInstanceMgmtMapping should set 'category' (falls back to \"default\")."
        );
        if (expectedCategory is not null)
        {
            JsonElementAssertions.AssertPropertyString(
                attributes,
                "category",
                expectedCategory,
                $"'category' should equal '{expectedCategory}' (propagated from start attributes)."
            );
        }

        Assert.True(
            attributes.TryGetProperty("priority", out var priorityEl)
                && priorityEl.ValueKind == JsonValueKind.Number,
            "InitInstanceMgmtMapping should set 'priority' as a number (falls back to 1)."
        );
        if (expectedPriority is not null)
        {
            Assert.Equal(expectedPriority.Value, priorityEl.GetInt32());
        }
    }
}
