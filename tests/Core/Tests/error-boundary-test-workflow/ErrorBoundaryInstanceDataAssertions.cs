using System.Globalization;
using System.Text.Json;
using Core.IntegrationTests.Helpers;
using Xunit;

namespace Core.IntegrationTests.Tests.ErrorBoundaryTestWorkflow;

/// <summary>
/// Instance <c>attributes</c> contract for <c>error-boundary-test-workflow</c>.
/// Field names align with <c>core/Workflows/error-boundary/src/*.csx</c> mappings.
/// </summary>
internal static class ErrorBoundaryInstanceDataAssertions
{
    /// <summary>
    /// Matches <c>task-retry-state</c>: <c>maxRetries: 2</c> → initial failure + 2 retries = 3 executions
    /// before Ignore fallback (verify against runtime if this changes).
    /// </summary>
    private const int ExpectedRetryThrowAttempts = 3;

    /// <summary>
    /// Backoff: <c>initialDelay PT1S</c>, then ×2 → ≥ ~1s and ≥ ~2s between InputHandler stamps (slack for CI).
    /// </summary>
    private const double MinMsBetweenAttempt1And2 = 750;

    private const double MinMsBetweenAttempt2And3 = 1600;

    private const string ContractHint =
        "(error-boundary-test-workflow CSX contract)";

    public static void AssertInitMappingExecuted(JsonElement attributes)
    {
        JsonElementAssertions.AssertPropertyTrue(
            attributes, "errorTestStarted",
            $"errorTestStarted should be true {ContractHint}"
        );
        JsonElementAssertions.AssertPropertyNonEmptyString(
            attributes, "testId",
            $"testId should be non-empty {ContractHint}"
        );
    }

    public static void AssertRetryHandled(JsonElement attributes)
    {
        JsonElementAssertions.AssertPropertyTrue(
            attributes, "retryHandled",
            $"retryHandled should be true after retry+ignore fallback {ContractHint}"
        );
    }

    /// <summary>
    /// Proves Retry ran multiple times via RetryThrowMapping counters,
    /// and exponential backoff delays via UTC stamps (workflow: PT1S, multiplier 2, no jitter).
    /// </summary>
    public static void AssertRetryThrowAttemptsAndBackoff(JsonElement attributes)
    {
        Assert.True(
            attributes.TryGetProperty("retryThrowAttemptCount", out var countEl),
            $"retryThrowAttemptCount missing {ContractHint}"
        );

        Assert.True(
            countEl.TryGetInt32(out var attempts),
            $"retryThrowAttemptCount should be an integral JSON number {ContractHint}"
        );

        Assert.Equal(
            ExpectedRetryThrowAttempts,
            attempts
        );

        Assert.True(
            attributes.TryGetProperty("retryAttempt1Utc", out var u1)
                && u1.ValueKind == JsonValueKind.String,
            $"retryAttempt1Utc missing {ContractHint}"
        );
        Assert.True(
            attributes.TryGetProperty("retryAttempt2Utc", out var u2)
                && u2.ValueKind == JsonValueKind.String,
            $"retryAttempt2Utc missing {ContractHint}"
        );
        Assert.True(
            attributes.TryGetProperty("retryAttempt3Utc", out var u3)
                && u3.ValueKind == JsonValueKind.String,
            $"retryAttempt3Utc missing {ContractHint}"
        );

        var t1 = DateTimeOffset.Parse(
            u1.GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind
        );
        var t2 = DateTimeOffset.Parse(
            u2.GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind
        );
        var t3 = DateTimeOffset.Parse(
            u3.GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind
        );

        var ms12 = (t2 - t1).TotalMilliseconds;
        var ms23 = (t3 - t2).TotalMilliseconds;

        Assert.True(
            ms12 >= MinMsBetweenAttempt1And2,
            $"Expected ≥{MinMsBetweenAttempt1And2}ms between retry InputHandler 1→2 (initialDelay PT1S), got {ms12}ms {ContractHint}"
        );
        Assert.True(
            ms23 >= MinMsBetweenAttempt2And3,
            $"Expected ≥{MinMsBetweenAttempt2And3}ms between retry InputHandler 2→3 (backoff ×2), got {ms23}ms {ContractHint}"
        );
    }

    public static void AssertIgnoreExecuted(JsonElement attributes)
    {
        JsonElementAssertions.AssertPropertyTrue(
            attributes, "errorIgnored",
            $"errorIgnored should be true after ignore boundary {ContractHint}"
        );
    }

    public static void AssertLogHandled(JsonElement attributes)
    {
        JsonElementAssertions.AssertPropertyTrue(
            attributes, "logHandled",
            $"logHandled should be true after log boundary {ContractHint}"
        );
    }

    public static void AssertPriorityRuleApplied(JsonElement attributes)
    {
        JsonElementAssertions.AssertPropertyTrue(
            attributes, "priorityRuleApplied",
            $"priorityRuleApplied should be true (InvalidOperationException → Ignore before wildcard Log fallback) {ContractHint}"
        );
    }

    public static void AssertStateLevelHandled(JsonElement attributes)
    {
        JsonElementAssertions.AssertPropertyTrue(
            attributes, "stateLevelHandled",
            $"stateLevelHandled should be true (state-level boundary caught error) {ContractHint}"
        );
    }
}
