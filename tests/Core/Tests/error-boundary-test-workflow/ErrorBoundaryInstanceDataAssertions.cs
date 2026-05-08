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
            $"priorityRuleApplied should be true (specific rule matched before fallback) {ContractHint}"
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
