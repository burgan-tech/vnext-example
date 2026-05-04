using System.Linq;
using System.Text.Json;
using Core.IntegrationTests.Helpers;
using Xunit;

namespace Core.IntegrationTests.Tests.LifecycleTransitionsTestWorkflow;

/// <summary>
/// Instance <c>attributes</c> contract for <c>lifecycle-transitions-test-workflow</c> pass path (timer to <c>pre-complete-state</c>).
/// Field names align with <c>core/Workflows/lifecycle-transitions/src/*.csx</c>.
/// </summary>
internal static class LifecyclePassPathInstanceDataAssertions
{
    private const string CsxContractHint =
        "(lifecycle-transitions *.csx onEntry / onExit / transition mapping contract)";

    public static void AssertAfterPreCompleteViaTimer(JsonElement attributes)
    {
        JsonElementAssertions.AssertPropertyTrue(
            attributes,
            "initialized",
            $"initialized should be true {CsxContractHint}"
        );
        JsonElementAssertions.AssertPropertyNonEmptyString(attributes, "initializedAt");
        JsonElementAssertions.AssertPropertyString(attributes, "testPath", "pass");

        Assert.True(
            attributes.TryGetProperty("stepLog", out var stepLog)
                && stepLog.ValueKind == JsonValueKind.Array,
            "stepLog should be a JSON array (InitializeDataMapping)"
        );
        var steps = stepLog.EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("initialize-state:onEntry", steps);

        JsonElementAssertions.AssertPropertyTrue(
            attributes,
            "transitionMappingExecuted",
            $"transitionMappingExecuted should be true {CsxContractHint}"
        );
        JsonElementAssertions.AssertPropertyNonEmptyString(attributes, "transitionMappingAt");

        JsonElementAssertions.AssertPropertyTrue(
            attributes,
            "processEntryExecuted",
            $"processEntryExecuted should be true {CsxContractHint}"
        );
        JsonElementAssertions.AssertPropertyNonEmptyString(attributes, "processedAt");

        JsonElementAssertions.AssertPropertyTrue(
            attributes,
            "processExitExecuted",
            $"processExitExecuted should be true {CsxContractHint}"
        );
        JsonElementAssertions.AssertPropertyNonEmptyString(attributes, "exitedAt");

        JsonElementAssertions.AssertPropertyTrue(
            attributes,
            "timerTriggered",
            $"timerTriggered should be true {CsxContractHint}"
        );
        JsonElementAssertions.AssertPropertyNonEmptyString(attributes, "timerTriggeredAt");
    }
}
